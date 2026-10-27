// Runs the tests a change reaches instead of every suite: the .NET test projects that depend on a
// changed project, one database provider unless the change concerns persistence, the Angular
// libraries that changed and the sample checks when the sample changed. One build makes every
// affected test project first; then up to three test projects run at a time, the slowest first.
// The Angular libraries (one at a time) and the sample checks need no build and run alongside from
// the start. With both providers a project's SQL Server variants run in a second process next to
// its other tests. Each job's output is printed in one block when it ends, and a summary closes.
//
//   npm run test:affected                    uncommitted changes: what you are working on now
//   npm run test:affected -- --branch        everything since the merge base with origin/main (a whole PR)
//   npm run test:affected -- --base <ref>    everything since <ref>, uncommitted included
//   npm run test:affected -- --all-providers run SQL Server and PostgreSQL variants, side by side
//   npm run test:affected -- --all           every test project, provider and library
//   npm run test:affected -- --max-parallel <n>  test projects at a time (default 3); 1 runs every job in turn
//   npm run test:affected -- --dry-run       print the plan only
//   npm run test:affected -- --files a,b     plan and run for these files instead of the git changes
import { readdir, readFile } from 'node:fs/promises';
import { homedir, tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  affectedFrontEndLibraries,
  affectedTestProjects,
  buildsThroughSolution,
  durationsFilePath,
  frontEndRoot,
  isTestProject,
  jobReport,
  needsAllProviders,
  normalizePath,
  outcomeSummary,
  parseMaxParallel,
  parseProjectReferences,
  providerRuns,
  runJobs,
  sampleRoot,
  summaryLines,
  testableFrontEndLibraries,
  testPlan
} from './affected-lib.mjs';
import {
  createRunFolder,
  readDurations,
  removeRunFolder,
  solutionFileName,
  writeDurations,
  writeSolution
} from './affected-state.mjs';

const repositoryRoot = resolve(fileURLToPath(new URL('.', import.meta.url)), '..', '..');
const projectRoots = ['src/Back-end', 'examples/SampleProjectManagement/src/Back-end'];
const skippedFolders = new Set(['bin', 'obj', 'node_modules', '.git', '.claude', '.angular', 'dist']);

/**
 * The durations of the last run of this checkout, which order the next one, in the user's own cache
 * folder. The generated solution of a run goes into a private folder of that run (createRunFolder).
 */
const durationsFile = durationsFilePath({ platform: process.platform, env: process.env, home: homedir(), repositoryRoot });

const args = process.argv.slice(2);
const option = name => args.includes(name);
const valueOf = name => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};

function git(...gitArgs) {
  const result = spawnSync('git', gitArgs, { cwd: repositoryRoot, encoding: 'utf8' });
  return result.status === 0 ? result.stdout.trim() : null;
}

/**
 * The commit to compare the working tree with. By default `HEAD`, so only what is not committed yet
 * counts: on a long-lived branch the merge base would make every run the whole branch again.
 */
function comparisonBase() {
  const explicit = valueOf('--base');
  if (explicit) {
    return git('merge-base', 'HEAD', explicit) ?? explicit;
  }
  if (option('--branch')) {
    return git('merge-base', 'HEAD', 'origin/main') ?? git('merge-base', 'HEAD', 'main');
  }
  return git('rev-parse', 'HEAD');
}

function changedFiles(base) {
  const lines = [
    git('diff', '--name-only', base) ?? '',
    git('ls-files', '--others', '--exclude-standard') ?? ''
  ].join('\n');
  return [...new Set(lines.split(/\r?\n/).filter(Boolean).map(normalizePath))];
}

async function findProjects(folder, found = []) {
  let entries;
  try {
    entries = await readdir(resolve(repositoryRoot, folder), { withFileTypes: true });
  } catch {
    return found;
  }
  for (const entry of entries) {
    if (entry.isDirectory() && !skippedFolders.has(entry.name)) {
      await findProjects(join(folder, entry.name), found);
    } else if (entry.isFile() && entry.name.endsWith('.csproj')) {
      found.push(normalizePath(join(folder, entry.name)));
    }
  }
  return found;
}

async function projectGraph() {
  const graph = new Map();
  const tests = new Set();
  for (const root of projectRoots) {
    for (const project of await findProjects(root)) {
      const content = await readFile(resolve(repositoryRoot, project), 'utf8');
      graph.set(project, parseProjectReferences(project, content));
      if (isTestProject(content)) {
        tests.add(project);
      }
    }
  }
  return { graph, tests };
}

async function findSpecFiles(folder, found = []) {
  let entries;
  try {
    entries = await readdir(resolve(repositoryRoot, frontEndRoot, folder), { withFileTypes: true });
  } catch {
    return found;
  }
  for (const entry of entries) {
    if (entry.isDirectory() && !skippedFolders.has(entry.name)) {
      await findSpecFiles(join(folder, entry.name), found);
    } else if (entry.isFile() && entry.name.endsWith('.spec.ts')) {
      found.push(normalizePath(join(folder, entry.name)));
    }
  }
  return found;
}

async function frontEndLibraries() {
  const angular = JSON.parse(await readFile(resolve(repositoryRoot, frontEndRoot, 'angular.json'), 'utf8'));
  return testableFrontEndLibraries(angular.projects, await findSpecFiles('projects'));
}

const windowsShell = process.platform === 'win32';

/**
 * The file, arguments and options that start a process. On Windows it starts through the shell as
 * one command line: the shell would join the arguments anyway, and Node warns about separate
 * arguments together with a shell (DEP0190).
 */
function processStart(command, commandArgs, cwd) {
  const options = { cwd: resolve(repositoryRoot, cwd) };
  return windowsShell
    ? [[command, ...commandArgs].join(' '), [], { ...options, shell: true }]
    : [command, commandArgs, options];
}

/** Runs one process and keeps its output, so that it is printed in one block when its job ends. */
function runProcess({ label, command, args: commandArgs, cwd }) {
  const started = Date.now();
  return new Promise(done => {
    const child = spawn(...processStart(command, commandArgs, cwd));
    const output = [];
    child.stdout.on('data', chunk => output.push(chunk));
    child.stderr.on('data', chunk => output.push(chunk));
    child.on('error', error => output.push(Buffer.from(String(error))));
    child.on('close', status => {
      const text = Buffer.concat(output).toString('utf8');
      done({ label, passed: status === 0, seconds: Math.round((Date.now() - started) / 1000), summary: outcomeSummary(text), output: text });
    });
  });
}

/** Runs the processes of one job side by side and prints their results together when all have ended. */
async function runJob(job) {
  const started = Date.now();
  const results = await Promise.all(job.processes.map(runProcess));
  console.log(jobReport(results));
  return { job, results, seconds: Math.round((Date.now() - started) / 1000) };
}

const maxParallel = parseMaxParallel(option('--max-parallel') ? valueOf('--max-parallel') ?? '' : undefined);
if (maxParallel === null) {
  console.error('--max-parallel needs a whole number of at least 1.');
  process.exit(2);
}
const everything = option('--all');
const explicitFiles = valueOf('--files')?.split(',').map(file => normalizePath(file.trim())).filter(Boolean);
const base = everything || explicitFiles ? null : comparisonBase();
if (!everything && !explicitFiles && !base) {
  console.error('No commit to compare with; pass --base <ref>, --files a,b or --all.');
  process.exit(2);
}
const branchScope = option('--branch') || valueOf('--base') !== undefined;

const files = everything ? [] : explicitFiles ?? changedFiles(base);
const { graph, tests } = await projectGraph();
const { testable: libraries, withoutSpecs: librariesWithoutSpecs } = await frontEndLibraries();
const testProjects = everything ? [...tests].sort() : affectedTestProjects(files, graph, tests);
const allProviders = everything || option('--all-providers') || needsAllProviders(files);
const runs = providerRuns(allProviders);
const angularLibraries = everything ? libraries : affectedFrontEndLibraries(files, libraries);
const sampleChanged = everything || files.some(file => file.startsWith(`${sampleRoot}/`));
const previousSeconds = await readDurations(durationsFile);
const dryRun = option('--dry-run');
// A dry run writes nothing, so it names the run folder that a real run creates.
const runFolder = !dryRun && buildsThroughSolution(testProjects) ? await createRunFolder() : null;
const plan = testPlan({
  repositoryRoot,
  testProjects,
  runs,
  angularLibraries,
  sampleChecks: sampleChanged,
  maxParallel,
  solutionPath: join(runFolder ?? join(tmpdir(), 'newheap-test-affected-<run>'), solutionFileName),
  previousSeconds,
  windowsShell
});
const sequential = plan.limits.total === 1;

console.log(everything
  ? 'Scope: everything (--all).'
  : explicitFiles
    ? `Scope: ${files.length} given files.`
    : branchScope
      ? `Scope: ${files.length} changed files since ${base.slice(0, 10)}.`
      : `Scope: ${files.length} uncommitted changed files (--branch covers everything since origin/main).`);
console.log(allProviders
  ? 'Database providers: SQL Server and PostgreSQL. Each test project runs two test processes at the same time: '
    + runs.map(providerRun => `${providerRun.label} (--filter "${providerRun.filter}")`).join(' next to ') + '.'
  : `Database providers: PostgreSQL only (--filter "${runs[0].filter}"; no persistence change; --all-providers runs both).`);
console.log(plan.build
  ? `Build, once before any .NET test runs: dotnet ${plan.build.processes[0].args.join(' ')}${plan.build.solution ? ' (a generated solution of the test projects below, in a private folder of this run)' : ''}`
  : 'Build: none');
console.log(`.NET test projects: ${testProjects.length === 0
  ? 'none'
  : sequential
    ? 'slowest first, one at a time (--max-parallel 1)'
    : `slowest first, up to ${maxParallel} at a time (--max-parallel <n>)`}`);
plan.jobs
  .filter(job => job.lane === 'dotnet')
  .forEach(job => console.log(`  ${job.name}${previousSeconds[job.name] === undefined ? '' : ` (last run ${previousSeconds[job.name]}s)`}`));
console.log(`Angular libraries: ${angularLibraries.length === 0
  ? 'none'
  : `${angularLibraries.join(', ')}; one at a time, ${sequential ? 'after the .NET tests' : 'from the start, alongside the build and the .NET tests'}`}`);
if (librariesWithoutSpecs.length > 0) {
  console.log(`Angular libraries without specs, not run: ${librariesWithoutSpecs.join(', ')}`);
}
console.log(`Sample checks: ${sampleChanged ? `npm run verify:samples; ${sequential ? 'last' : 'from the start, alongside the rest'}` : 'none'}`);

if (dryRun) {
  process.exit(0);
}

/** Keeps how long each .NET test project took, so that the next run starts the slowest first. */
async function keepDurations(jobResults) {
  const durations = { ...previousSeconds };
  jobResults
    .filter(jobResult => jobResult?.job.lane === 'dotnet')
    .forEach(jobResult => {
      durations[jobResult.job.name] = jobResult.seconds;
    });
  try {
    await writeDurations(durationsFile, durations);
  } catch {
    // The durations only order the next run; without them it falls back to the typical durations.
  }
}

const started = Date.now();
let keepRunFolder = false;
try {
  if (plan.build?.solution) {
    await writeSolution(plan.build.solution.path, plan.build.solution.content);
  }
  const buildFailed = jobResult => jobResult.job === plan.build && !jobResult.results[0].passed;
  const jobResults = await runJobs(plan.jobs, plan.limits, runJob, { stopAfter: buildFailed });
  await keepDurations(jobResults);
  const results = jobResults.filter(Boolean).flatMap(jobResult => jobResult.results);
  summaryLines(results, Math.round((Date.now() - started) / 1000)).forEach(line => console.log(line));
  if (jobResults.some(jobResult => jobResult && buildFailed(jobResult))) {
    const notRun = plan.jobs.filter((_, index) => !jobResults[index]).map(job => job.name);
    console.log(`The build failed, so no .NET test ran; the run stopped. Run dotnet ${plan.build.processes[0].args.join(' ')} to see every error.`);
    if (runFolder) {
      // That command builds the generated solution, so its private folder stays for it.
      keepRunFolder = true;
      console.log(`The generated solution stays in ${runFolder}; remove the folder when you no longer need it.`);
    }
    if (notRun.length > 0) {
      console.log(`Not run: ${notRun.join(', ')}`);
    }
  }
  process.exitCode = results.every(result => result.passed) ? 0 : 1;
} finally {
  if (runFolder && !keepRunFolder) {
    await removeRunFolder(runFolder);
  }
}
