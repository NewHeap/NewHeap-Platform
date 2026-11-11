import { readFile, stat } from 'node:fs/promises';
import { basename, isAbsolute, relative, resolve } from 'node:path';
import {
  consumerSkillBundleName,
  consumerSkillModuleDirectories,
  consumerSkillNames,
  readJson,
  repositoryRoot,
  walkFiles
} from './lib.mjs';

// The canonical skill evals in skill-evals/evals.json become a `claude plugin eval` suite for the
// distributed newheap-platform plugin. The suite is generated; tools/guidance/run-skill-evals.mjs
// stages it next to a copy of the plugin and materializes the workspace fixtures it names.

export const skillEvalsPath = resolve(repositoryRoot, 'skill-evals', 'evals.json');
export const skillEvalFixturesRoot = resolve(repositoryRoot, 'skill-evals', 'fixtures');
export const pluginEvalSuiteRoot = resolve(repositoryRoot, 'skill-evals', 'plugin-eval');
export const pluginEvalSkills = new Set([consumerSkillBundleName, ...consumerSkillNames]);

/** Run limits and tools shared by every generated case. */
export const pluginEvalRun = {
  maxTurns: 30,
  timeoutSeconds: 900,
  allowedTools: ['Read', 'Glob', 'Grep', 'Skill', 'Write', 'Edit']
};

/** Appended to the system prompt of both arms, so the final reply carries what the rubric judges. */
export const pluginEvalSystemPrompt = [
  'This is an automated evaluation without a person to answer questions.',
  'The current directory is the repository that the request refers to.',
  'When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead.',
  'If you would need to ask the user something before continuing, ask it in your final message and stop.',
  'End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run.'
].join(' ');

const generatedNotice = 'Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.';
const kebabCase = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const checkFields = new Map([
  ['file_exists', ['path']],
  ['file_matches', ['path', 'pattern']],
  ['response_matches', ['pattern']],
  ['response_excludes', ['pattern']],
  ['module_not_read', ['skill']]
]);
const reservedFixtureNames = new Set(['prompt.md', 'case.yaml']);

export async function loadSkillEvaluations() {
  return readJson(skillEvalsPath);
}

export function isPluginEval(item) {
  return pluginEvalSkills.has(item.skill);
}

export function pluginEvals(evaluations) {
  return (evaluations.evals ?? []).filter(isPluginEval);
}

export function workspaceOf(evaluations, item) {
  return item.workspace ?? evaluations.defaultWorkspace;
}

/** The workspace and its `extends` ancestors, base first. */
export function workspaceChain(evaluations, name) {
  const chain = [];
  const seen = new Set();
  let current = name;
  while (current !== undefined) {
    if (seen.has(current)) throw new Error(`Skill eval workspace ${name} extends itself.`);
    seen.add(current);
    const workspace = evaluations.workspaces?.[current];
    if (!workspace) throw new Error(`Unknown skill eval workspace ${current}.`);
    chain.unshift({ name: current, ...workspace });
    current = workspace.extends;
  }
  return chain;
}

export function workspaceHasFiles(evaluations, name) {
  return workspaceChain(evaluations, name).some(workspace => workspace.bootstrap || workspace.files);
}

/** Validates the workspace and check definitions that the plugin eval suite is generated from. */
export async function validateSkillEvalSuiteDefinition(evaluations) {
  const failures = [];
  const workspaces = evaluations.workspaces;
  if (!workspaces || typeof workspaces !== 'object' || Array.isArray(workspaces)) {
    return ['skill-evals/evals.json must define a workspaces object.'];
  }
  if (!workspaces[evaluations.defaultWorkspace]) failures.push(`Unknown default skill eval workspace ${evaluations.defaultWorkspace}.`);

  for (const [name, workspace] of Object.entries(workspaces)) {
    if (!kebabCase.test(name)) failures.push(`Invalid skill eval workspace name ${name}.`);
    if (typeof workspace.description !== 'string' || !workspace.description.trim()) failures.push(`Workspace ${name} needs a description.`);
    if (workspace.bootstrap !== undefined) {
      if (!Array.isArray(workspace.bootstrap) || workspace.bootstrap.some(value => typeof value !== 'string')) {
        failures.push(`Workspace ${name}: bootstrap must be an array of bootstrap arguments.`);
      } else if (workspace.bootstrap.some(value => ['--skip-install', '--root'].includes(value))) {
        failures.push(`Workspace ${name}: the runner supplies the bootstrap root and --skip-install.`);
      }
    }
    if (workspace.extends !== undefined) {
      try { workspaceChain(evaluations, name); }
      catch (error) { failures.push(error.message); }
    }
    if (workspace.files !== undefined) {
      const files = typeof workspace.files === 'string' ? resolve(repositoryRoot, workspace.files) : undefined;
      const inside = files && relative(skillEvalFixturesRoot, files);
      if (!files || !inside || inside.startsWith('..') || isAbsolute(inside)) {
        failures.push(`Workspace ${name}: files must name a directory below skill-evals/fixtures.`);
      } else if (!(await stat(files).catch(() => undefined))?.isDirectory()) {
        failures.push(`Workspace ${name}: ${workspace.files} does not exist.`);
      } else {
        for (const path of await walkFiles(files)) {
          if (reservedFixtureNames.has(basename(path))) {
            failures.push(`Workspace ${name}: ${relative(repositoryRoot, path).replaceAll('\\', '/')} would be loaded as an eval case.`);
          }
        }
      }
    }
  }

  for (const item of evaluations.evals ?? []) {
    if (!isPluginEval(item)) {
      if (item.workspace !== undefined || item.checks !== undefined) {
        failures.push(`${item.id}: ${item.skill} is not distributed in the newheap-platform plugin, so it takes no workspace or checks.`);
      }
      continue;
    }
    if (item.workspace !== undefined && !workspaces[item.workspace]) failures.push(`${item.id}: unknown workspace ${item.workspace}`);
    if (item.checks === undefined) continue;
    if (!Array.isArray(item.checks) || item.checks.length === 0) {
      failures.push(`${item.id}: checks must be a non-empty array when present.`);
      continue;
    }
    const names = new Set();
    for (const check of item.checks) {
      const label = `${item.id}: check ${check.name ?? '(unnamed)'}`;
      if (!kebabCase.test(check.name ?? '') || names.has(check.name)) failures.push(`${label} needs a unique kebab-case name.`);
      names.add(check.name);
      const required = checkFields.get(check.type);
      if (!required) {
        failures.push(`${label} has unknown type ${check.type}.`);
        continue;
      }
      const allowed = new Set(['name', 'type', 'flags', ...required]);
      for (const key of Object.keys(check)) if (!allowed.has(key)) failures.push(`${label} has unsupported field ${key}.`);
      for (const key of required) if (typeof check[key] !== 'string' || !check[key]) failures.push(`${label} needs ${key}.`);
      if (check.path !== undefined && !/^(?!\/)(?!.*(?:^|\/)\.\.(?:\/|$))[^\\:*?"<>|]+$/.test(check.path)) {
        failures.push(`${label} needs a relative workspace path with forward slashes.`);
      }
      if (check.flags !== undefined && (check.pattern === undefined || !/^[imsu]*$/.test(check.flags))) {
        failures.push(`${label} supports only the regular-expression flags i, m, s and u.`);
      }
      if (check.pattern !== undefined) {
        try { new RegExp(check.pattern, check.flags ?? ''); }
        catch (error) { failures.push(`${label} has an invalid pattern: ${error.message}`); }
      }
      if (check.type === 'module_not_read' && !consumerSkillNames.includes(check.skill)) {
        failures.push(`${label} names unknown consumer module ${check.skill}.`);
      }
    }
  }
  return failures;
}

const yamlScalar = value => JSON.stringify(value);
const yamlList = values => `[${values.map(yamlScalar).join(', ')}]`;
const escapeRegExp = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const moduleReadPattern = directories => {
  const names = directories.map(escapeRegExp);
  const group = names.length === 1 ? names[0] : `(?:${names.join('|')})`;
  return `skills[\\\\/]+${group}[\\\\/]+SKILL\\.md`;
};

function frontmatter(lines, body = '') {
  return `---\n# ${generatedNotice}\n${lines.join('\n')}\n---\n${body ? `\n${body}\n` : ''}`;
}

/** The module directories a case must read, one group per alternative set of modules. */
export function expectedModuleGroups(item, ruleById) {
  if (item.skill !== consumerSkillBundleName) return [[consumerSkillModuleDirectories.get(item.skill)]];
  const groups = new Map();
  for (const ruleId of item.expectedRules) {
    const directories = [...new Set((ruleById.get(ruleId)?.skills ?? []).map(skill => consumerSkillModuleDirectories.get(skill)))].sort();
    if (directories.length > 0) groups.set(directories.join('|'), directories);
  }
  return [...groups.values()].sort((left, right) => left.join('|').localeCompare(right.join('|')));
}

/** The case tags: the module directory, or `router` plus every expected module for a router case. */
export function pluginEvalTags(item, ruleById) {
  if (item.skill !== consumerSkillBundleName) return [consumerSkillModuleDirectories.get(item.skill)];
  return ['router', ...new Set(expectedModuleGroups(item, ruleById).flat())];
}

function renderCheck(check) {
  const lines = [];
  if (check.type === 'file_exists') {
    lines.push('type: file_exists', `path: ${yamlScalar(check.path)}`);
  } else if (check.type === 'file_matches') {
    lines.push('type: regex', `target: { source: file, path: ${yamlScalar(check.path)} }`, `pattern: ${yamlScalar(check.pattern)}`);
  } else if (check.type === 'response_matches' || check.type === 'response_excludes') {
    lines.push('type: regex', 'target: last_message', `pattern: ${yamlScalar(check.pattern)}`);
    if (check.type === 'response_excludes') lines.push('match: not_contains');
  } else if (check.type === 'module_not_read') {
    const directory = consumerSkillModuleDirectories.get(check.skill);
    lines.push('type: tool_used', 'tool: Read', `input_match: ${yamlScalar(moduleReadPattern([directory]))}`, 'min: 0', 'max: 0', 'arm: with-only');
  }
  if (check.flags) lines.push(`flags: ${yamlScalar(check.flags)}`);
  return frontmatter(lines);
}

function renderRubric(item, workspace) {
  return [
    'The user gave an AI coding agent this request:',
    '',
    '<request>',
    item.prompt,
    '</request>',
    '',
    `The agent worked without a person to answer questions. Its starting repository: ${workspace.description}`,
    'When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.',
    '',
    'Expected outcome, to be treated as a checklist:',
    '',
    item.expectedOutcome,
    '',
    'PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.',
    'FAIL if any element is missing, contradicted or only mentioned as a vague intention.',
    'Judge the substance, not the formatting or wording.'
  ].join('\n');
}

function renderScaffold(evaluations, name) {
  const workspace = evaluations.workspaces[name];
  const lines = [
    '#!/usr/bin/env bash',
    `# ${generatedNotice}`,
    `# Prepares the ${name} workspace: ${workspace.description}`,
    'set -euo pipefail',
    ''
  ];
  if (workspaceHasFiles(evaluations, name)) {
    lines.push(
      'case_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"',
      `fixture="$case_directory/../fixtures/${name}"`,
      'if [ ! -d "$fixture" ]; then',
      `  echo "The ${name} workspace fixture is missing. Run the suite with npm run skills:eval:run, which materializes it." >&2`,
      '  exit 1',
      'fi',
      'cp -R "$fixture/." .',
      '',
      '# A repository with the fixture committed lets the agent and a reviewer see what the run changed.',
      'if command -v git >/dev/null 2>&1; then',
      '  {',
      '    git init --quiet',
      '    git add --all',
      `    git -c user.name="NewHeap eval" -c user.email="eval@example.invalid" -c commit.gpgsign=false commit --quiet --message "${name} workspace"`,
      '  } || echo "Git initialization of the workspace was skipped." >&2',
      'fi'
    );
  } else {
    lines.push(
      'if command -v git >/dev/null 2>&1; then',
      '  git init --quiet || echo "Git initialization of the workspace was skipped." >&2',
      'fi'
    );
  }
  return `${lines.join('\n')}\n`;
}

/**
 * Renders the generated suite as absolute path to file content. Every plugin case gets a rubric on
 * its expected outcome, plugin-fired indicators for the router skill and the expected modules, and
 * the deterministic checks declared in evals.json.
 */
export function renderPluginEvalSuite(evaluations, rules) {
  const ruleById = new Map(rules.map(rule => [rule.id, rule]));
  const outputs = new Map();
  for (const item of pluginEvals(evaluations)) {
    const caseRoot = resolve(pluginEvalSuiteRoot, item.id);
    const workspaceName = workspaceOf(evaluations, item);
    const workspace = evaluations.workspaces[workspaceName];
    const moduleGroups = expectedModuleGroups(item, ruleById);
    const tags = pluginEvalTags(item, ruleById);
    const checks = item.checks ?? [];
    const scoredChecks = checks.filter(check => check.type !== 'module_not_read').length;

    outputs.set(resolve(caseRoot, 'prompt.md'), frontmatter([
      `description: ${yamlScalar(`Skill ${item.skill}; guidance rules ${item.expectedRules.join(', ')}; workspace ${workspaceName}.`)}`,
      `tags: ${yamlList(tags)}`,
      `expected_outcome: ${yamlScalar(item.expectedOutcome)}`,
      `max_turns: ${pluginEvalRun.maxTurns}`,
      `timeout_seconds: ${pluginEvalRun.timeoutSeconds}`,
      `allowed_tools: ${yamlList(pluginEvalRun.allowedTools)}`,
      `append_system_prompt: ${yamlScalar(pluginEvalSystemPrompt)}`
    ], item.prompt));
    outputs.set(resolve(caseRoot, 'case.yaml'), [
      `# ${generatedNotice}`,
      'schema_version: "1.1"',
      `name: ${yamlScalar(item.id)}`,
      'context:',
      '  scaffold_script: scaffold.sh',
      ''
    ].join('\n'));
    outputs.set(resolve(caseRoot, 'scaffold.sh'), renderScaffold(evaluations, workspaceName));

    const graders = resolve(caseRoot, 'graders');
    outputs.set(resolve(graders, 'outcome.md'), frontmatter([
      'type: llm',
      'focus: last_message',
      `weight: ${Math.max(1, scoredChecks)}`
    ], renderRubric(item, workspace)));
    outputs.set(resolve(graders, 'skill-fired.md'), frontmatter([
      'type: tool_used',
      'tool: Skill',
      `input_match: ${yamlScalar(`"skill"\\s*:\\s*"(?:[\\w-]+:)?${consumerSkillBundleName}"`)}`
    ]));
    for (const directories of moduleGroups) {
      outputs.set(resolve(graders, `module-${directories.join('-or-')}.md`), frontmatter([
        'type: tool_used',
        'tool: Read',
        `input_match: ${yamlScalar(moduleReadPattern(directories))}`,
        'arm: with-only'
      ]));
    }
    for (const check of checks) outputs.set(resolve(graders, `check-${check.name}.md`), renderCheck(check));
  }
  return outputs;
}

/** Generated suite files that are missing, stale or no longer generated. */
export async function stalePluginEvalSuiteFiles(outputs) {
  const stale = [];
  const normalize = value => value?.replaceAll('\r\n', '\n');
  for (const [path, content] of outputs) {
    let current;
    try { current = await readFile(path, 'utf8'); } catch { current = undefined; }
    if (normalize(current) !== normalize(content)) stale.push(path);
  }
  for (const path of await walkFiles(pluginEvalSuiteRoot).catch(() => [])) {
    if (!outputs.has(path)) stale.push(path);
  }
  return stale;
}
