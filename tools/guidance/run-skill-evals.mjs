import { spawnSync } from 'node:child_process';
import { chmod, cp, mkdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { relative, resolve } from 'node:path';
import { consumerPluginRoot, consumerSkillBundleName, consumerSkillModuleDirectories, loadRules, repositoryRoot } from './lib.mjs';
import {
  loadSkillEvaluations,
  pluginEvalSuiteRoot,
  pluginEvalTags,
  pluginEvals,
  renderPluginEvalSuite,
  stalePluginEvalSuiteFiles,
  validateSkillEvalSuiteDefinition,
  workspaceChain,
  workspaceHasFiles,
  workspaceOf
} from './skill-eval-suite.mjs';

// Runs the generated skill eval suite with `claude plugin eval` against a staged copy of the
// distributed newheap-platform plugin, optionally against a no-plugin baseline, and summarizes
// the result per case. Every agent run and judge call is a paid model call; the runner therefore
// needs an explicit --case, --tag or --all selection and is never part of CI by default.

const usage = `Usage: npm run skills:eval:run -- (--case <glob> | --tag <module> | --all) [options]

Selection (one is required):
  --case <glob>          Case ids, as a glob such as consumer-frontend-* (one value)
  --tag <tag>            Module tag such as backend, frontend or router (repeatable)
  --all                  Every case of the suite; combine with --max-cost-usd

Options:
  --baseline             Also run each case without the plugin and report the delta
  --runs <n>             Runs per case and arm (claude plugin eval default: 3)
  --model <model>        Model under test; pin it to compare runs over time
  --judge-model <model>  Model for the rubric grader (claude plugin eval default: haiku)
  --max-cost-usd <usd>   Stop starting runs once the estimated cost reaches this ceiling
  --concurrency <n>      Agent runs at once, 1 to 8 (default 1)
  --threshold <0..1>     Exit with 1 when a case scores below this (default 1.0)
  --allow-bash           Also grant Bash; needs a sandbox backend (macOS, Linux or WSL2)
  --trust-plugin         Skip the trust prompt for the staged plugin (non-interactive use)
  --publish              Publish the HTML report to claude.ai instead of keeping it local
  --keep-temp            Keep each run's sandbox directory for debugging
  --dry-run              Stage the plugin and suite and print the command without running it`;

const minimumClaudeVersion = [2, 1, 269];
const valueOptions = new Set(['--case', '--runs', '--model', '--judge-model', '--max-cost-usd', '--concurrency', '--threshold']);
const flagOptions = new Set(['--all', '--baseline', '--allow-bash', '--trust-plugin', '--publish', '--keep-temp', '--dry-run', '--help']);

function fail(message) {
  console.error(message);
  process.exit(2);
}

function parseArguments(args) {
  const options = { tags: [] };
  for (let index = 0; index < args.length; index++) {
    const argument = args[index] === '-j' ? '--concurrency' : args[index];
    if (argument === '--tag') {
      const value = args[++index];
      if (!value || value.startsWith('--')) fail('--tag needs a value.');
      options.tags.push(value);
    } else if (valueOptions.has(argument)) {
      const value = args[++index];
      if (value === undefined || value.startsWith('--')) fail(`${argument} needs a value.`);
      options[argument.slice(2)] = value;
    } else if (flagOptions.has(argument)) {
      options[argument.slice(2)] = true;
    } else {
      fail(`Unknown argument ${argument}.\n\n${usage}`);
    }
  }
  return options;
}

function globPattern(glob) {
  let source = '';
  for (const character of glob) {
    if (character === '*') source += '.*';
    else if (character === '?') source += '.';
    else if (character === '{') source += '(?:';
    else if (character === '}') source += ')';
    else if (character === ',') source += '|';
    else source += character.replace(/[.+^$()|[\]\\]/g, '\\$&');
  }
  return new RegExp(`^${source}$`);
}

function claudeVersion(binary) {
  const result = spawnSync(binary, ['--version'], { encoding: 'utf8' });
  if (result.error || result.status !== 0) return undefined;
  return /(\d+)\.(\d+)\.(\d+)/.exec(result.stdout)?.slice(1).map(Number);
}

const atLeast = (version, minimum) => {
  const difference = version.map((part, index) => part - minimum[index]).find(value => value !== 0) ?? 0;
  return difference >= 0;
};

const formatScore = value => typeof value === 'number' ? value.toFixed(2) : '-';
const formatDelta = value => typeof value === 'number' ? `${value >= 0 ? '+' : ''}${value.toFixed(2)}` : '-';
const repositoryPath = path => relative(repositoryRoot, path).replaceAll('\\', '/');

const options = parseArguments(process.argv.slice(2));
if (options.help) {
  console.log(usage);
  process.exit(0);
}
if (!options.case && options.tags.length === 0 && !options.all) {
  fail(`Select the cases to run with --case, --tag or --all; every run is a paid model call.\n\n${usage}`);
}
if (options.all && (options.case || options.tags.length > 0)) fail('--all cannot be combined with --case or --tag.');

const evaluations = await loadSkillEvaluations();
const rules = await loadRules();
const ruleById = new Map(rules.map(rule => [rule.id, rule]));
const definitionFailures = await validateSkillEvalSuiteDefinition(evaluations);
if (definitionFailures.length > 0) fail(definitionFailures.join('\n'));
const stale = await stalePluginEvalSuiteFiles(renderPluginEvalSuite(evaluations, rules));
if (stale.length > 0) fail(`The generated plugin eval suite is stale. Run npm run guidance:generate first.\n${stale.map(path => `- ${repositoryPath(path)}`).join('\n')}`);

const casePattern = options.case ? globPattern(options.case) : undefined;
const selected = pluginEvals(evaluations).filter(item => {
  if (casePattern && !casePattern.test(item.id)) return false;
  if (options.tags.length > 0 && !pluginEvalTags(item, ruleById).some(tag => options.tags.includes(tag))) return false;
  return true;
});
if (selected.length === 0) fail('No plugin eval case matches the selection.');

const claudeBinary = process.env.NH_CLAUDE_BIN || 'claude';
const version = claudeVersion(claudeBinary);
const versionProblem = !version
  ? `Claude Code was not found as "${claudeBinary}"; install it or set NH_CLAUDE_BIN.`
  : !atLeast(version, minimumClaudeVersion)
    ? `claude plugin eval needs Claude Code ${minimumClaudeVersion.join('.')} or later; found ${version.join('.')}. Run claude update.`
    : undefined;
if (versionProblem && !options['dry-run']) fail(versionProblem);

// Stage the plugin exactly as distributed, plus the Claude Code manifest that `claude plugin eval`
// needs and the eval suite with its workspace fixtures. The staging directory is ignored by Git.
const stagingRoot = resolve(repositoryRoot, 'skill-evals', '.staging');
const pluginRoot = resolve(stagingRoot, 'newheap-platform');
const evalRoot = resolve(pluginRoot, 'evals');
await rm(stagingRoot, { recursive: true, force: true });
await cp(consumerPluginRoot, pluginRoot, { recursive: true });
const codexManifest = JSON.parse(await readFile(resolve(consumerPluginRoot, '.codex-plugin', 'plugin.json'), 'utf8'));
await mkdir(resolve(pluginRoot, '.claude-plugin'), { recursive: true });
await writeFile(resolve(pluginRoot, '.claude-plugin', 'plugin.json'), `${JSON.stringify({
  name: codexManifest.name,
  version: codexManifest.version,
  description: codexManifest.description,
  author: codexManifest.author
}, null, 2)}\n`, 'utf8');
await cp(pluginEvalSuiteRoot, evalRoot, { recursive: true });

const bootstrapScript = resolve(
  pluginRoot,
  'skills',
  consumerSkillBundleName,
  'skills',
  consumerSkillModuleDirectories.get('newheap-consumer-development'),
  'scripts',
  'bootstrap-newheap-consumer.mjs'
);
const workspaces = [...new Set(selected.map(item => workspaceOf(evaluations, item)))].filter(name => workspaceHasFiles(evaluations, name));
for (const name of workspaces) {
  const target = resolve(evalRoot, 'fixtures', name);
  await mkdir(target, { recursive: true });
  for (const workspace of workspaceChain(evaluations, name)) {
    if (workspace.bootstrap) {
      const result = spawnSync(process.execPath, [bootstrapScript, target, ...workspace.bootstrap, '--skip-install'], { encoding: 'utf8' });
      if (result.status !== 0) throw new Error(`Bootstrapping the ${name} workspace failed:\n${result.stderr || result.stdout}`);
    }
    if (workspace.files) await cp(resolve(repositoryRoot, workspace.files), target, { recursive: true, force: true });
  }
}
for (const item of selected) await chmod(resolve(evalRoot, item.id, 'scaffold.sh'), 0o755);

const outputDirectory = resolve(repositoryRoot, 'skill-evals', 'results', new Date().toISOString().replaceAll(':', '-').replace('.', '-'));
const claudeArguments = [
  'plugin', 'eval', pluginRoot,
  '--allow-tools', 'Write', 'Edit', ...(options['allow-bash'] ? ['Bash'] : []),
  '--scaffold',
  '--ablation', options.baseline ? 'with-without' : 'none',
  '--output-dir', outputDirectory,
  ...(options.publish ? [] : ['--no-publish']),
  ...(options.case ? ['--case', options.case] : []),
  ...(options.runs ? ['--runs', options.runs] : []),
  ...(options.model ? ['--model', options.model] : []),
  ...(options['judge-model'] ? ['--judge-model', options['judge-model']] : []),
  ...(options['max-cost-usd'] ? ['--max-cost-usd', options['max-cost-usd']] : []),
  ...(options.concurrency ? ['--concurrency', options.concurrency] : []),
  ...(options.threshold ? ['--threshold', options.threshold] : []),
  ...(options['trust-plugin'] ? ['--trust-plugin'] : []),
  ...(options['keep-temp'] ? ['--keep-temp'] : []),
  ...(options.tags.length > 0 ? ['--tag', ...options.tags] : [])
];

const runsPerArm = Number(options.runs ?? 3);
const arms = options.baseline ? 2 : 1;
console.log(`Staged newheap-platform ${codexManifest.version} with ${selected.length} of ${pluginEvals(evaluations).length} cases at ${repositoryPath(pluginRoot)}.`);
console.log(`Workspaces: ${[...new Set(selected.map(item => workspaceOf(evaluations, item)))].join(', ')}.`);
console.log(`About ${selected.length * runsPerArm * arms} agent runs (${selected.length} cases x ${runsPerArm} runs x ${arms} arm${arms > 1 ? 's' : ''}) plus up to three judge calls per rubric grade.`);
if (options.all && !options['max-cost-usd']) console.warn('Warning: --all without --max-cost-usd has no cost ceiling.');

if (options['dry-run']) {
  if (versionProblem) console.warn(`Warning: ${versionProblem}`);
  console.log(`Cases: ${selected.map(item => item.id).join(', ')}`);
  console.log(`Command: ${claudeBinary} ${claudeArguments.map(argument => /\s/.test(argument) ? JSON.stringify(argument) : argument).join(' ')}`);
  process.exit(0);
}

const run = spawnSync(claudeBinary, claudeArguments, { stdio: 'inherit' });
if (run.error) throw run.error;

const resultPath = resolve(outputDirectory, 'aggregate-result.json');
const result = await readFile(resultPath, 'utf8').then(JSON.parse).catch(() => undefined);
if (!result) {
  console.error(`No aggregate-result.json was written to ${repositoryPath(outputDirectory)}.`);
  process.exit(run.status ?? 1);
}

const indicatorRate = (runs, matches) => {
  const graded = runs.filter(entry => !entry.error && Array.isArray(entry.graders) && entry.graders.some(grader => matches(grader.name)));
  if (graded.length === 0) return '-';
  const passed = graded.filter(entry => entry.graders.filter(grader => matches(grader.name)).every(grader => grader.passed)).length;
  return `${passed}/${graded.length}`;
};
const rows = (result.cases ?? []).map(entry => {
  const withRuns = entry.arms?.with ?? [];
  const delta = entry.aggregates?.delta;
  const score = entry.aggregates?.score;
  const errors = withRuns.filter(run => run.error).length;
  return {
    name: entry.name,
    score,
    without: typeof delta === 'number' && typeof score === 'number' ? score - delta : undefined,
    delta,
    skill: indicatorRate(withRuns, name => name === 'skill-fired'),
    modules: indicatorRate(withRuns, name => name.startsWith('module-')),
    note: errors > 0 ? `${errors} run error${errors > 1 ? 's' : ''}` : ''
  };
});
const width = Math.max(4, ...rows.map(row => row.name.length));
console.log('');
console.log(`${'CASE'.padEnd(width)}  WITH   W/OUT  DELTA  SKILL  MODULES  NOTE`);
for (const row of rows) {
  console.log(`${row.name.padEnd(width)}  ${formatScore(row.score).padEnd(5)}  ${formatScore(row.without).padEnd(5)}  ${formatDelta(row.delta).padEnd(5)}  ${row.skill.padEnd(5)}  ${row.modules.padEnd(7)}  ${row.note}`);
}
const notFired = rows.filter(row => row.skill !== '-' && row.skill.startsWith('0/'));
const noEffect = rows.filter(row => typeof row.delta === 'number' && row.delta <= 0);
if (notFired.length > 0) console.log(`\nThe router skill never fired for: ${notFired.map(row => row.name).join(', ')}. Check the router description against these prompts.`);
if (noEffect.length > 0) console.log(`\nNo positive delta for: ${noEffect.map(row => row.name).join(', ')}. Read the rubric verdicts in the report before changing the skill.`);
if (result.partial) console.log(`\nThe run is partial (${result.partialReason}); leave it out of comparisons.`);
const report = resolve(outputDirectory, 'report.html');
if (await stat(report).catch(() => undefined)) console.log(`\nReport: ${repositoryPath(report)}`);
console.log(`Result: ${repositoryPath(resultPath)}`);
process.exit(run.status ?? 1);
