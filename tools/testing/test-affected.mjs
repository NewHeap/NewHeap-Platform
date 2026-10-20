// Runs the tests a change reaches instead of every suite: the .NET test projects that depend on a
// changed project, one database provider unless the change concerns persistence, the Angular
// libraries that changed and the sample checks when the sample changed. With both providers a test
// project is built once and its SQL Server variants run in a second process next to the other tests.
//
//   npm run test:affected                    uncommitted changes: what you are working on now
//   npm run test:affected -- --branch        everything since the merge base with origin/main (a whole PR)
//   npm run test:affected -- --base <ref>    everything since <ref>, uncommitted included
//   npm run test:affected -- --all-providers run SQL Server and PostgreSQL variants, side by side
//   npm run test:affected -- --all           every test project, provider and library
//   npm run test:affected -- --dry-run       print the plan only
//   npm run test:affected -- --files a,b     plan and run for these files instead of the git changes
import { readdir, readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  affectedFrontEndLibraries,
  affectedTestProjects,
  angularTestArguments,
  dotnetTestArguments,
  isTestProject,
  needsAllProviders,
  normalizePath,
  parseProjectReferences,
  providerRuns,
  testableFrontEndLibraries
} from './affected-lib.mjs';

const repositoryRoot = resolve(fileURLToPath(new URL('.', import.meta.url)), '..', '..');
const projectRoots = ['src/Back-end', 'examples/SampleProjectManagement/src/Back-end'];
const frontEndRoot = 'src/Front-end';
const sampleRoot = 'examples/SampleProjectManagement';
const skippedFolders = new Set(['bin', 'obj', 'node_modules', '.git', '.claude', '.angular', 'dist']);

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

/** Prints one line per process and, when it failed, the end of its output. */
function report(label, started, status, output) {
  const seconds = Math.round((Date.now() - started) / 1000);
  const passed = status === 0;
  const summary = output.split(/\r?\n/).reverse().find(line => /Passed!|Failed!|TOTAL:|Executed .* of/.test(line))?.trim();
  console.log(`${passed ? 'PASS' : 'FAIL'} ${label} (${seconds}s)${summary ? ` - ${summary.replace(/\x1b\[[0-9;]*[A-Za-z]/g, '')}` : ''}`);
  if (!passed) {
    console.log(output.split(/\r?\n/).slice(-60).join('\n'));
  }
  return passed;
}

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

function run(label, command, commandArgs, cwd) {
  const started = Date.now();
  const [file, fileArgs, options] = processStart(command, commandArgs, cwd);
  const result = spawnSync(file, fileArgs, { ...options, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
  return report(label, started, result.status, `${result.stdout ?? ''}${result.stderr ?? ''}`);
}

/** Like `run` without blocking, so that processes run side by side; each reports when it ends. */
function runAlongside(label, command, commandArgs, cwd) {
  const started = Date.now();
  return new Promise(done => {
    const child = spawn(...processStart(command, commandArgs, cwd));
    const output = [];
    child.stdout.on('data', chunk => output.push(chunk));
    child.stderr.on('data', chunk => output.push(chunk));
    child.on('error', error => output.push(Buffer.from(String(error))));
    child.on('close', status => done(report(label, started, status, Buffer.concat(output).toString('utf8'))));
  });
}

/**
 * Tests one project with one process per provider run. Several runs share one build, made first,
 * and run at the same time.
 */
async function testProject(project, runs) {
  if (runs.length === 1) {
    return run(project, 'dotnet', dotnetTestArguments(project, runs[0].filter, { windowsShell }), '.');
  }
  if (!run(`${project} (build)`, 'dotnet', ['build', project, '-v', 'q', '-nologo'], '.')) {
    return false;
  }
  const results = await Promise.all(runs.map(providerRun => runAlongside(
    `${project} [${providerRun.label}]`,
    'dotnet',
    dotnetTestArguments(project, providerRun.filter, { noBuild: true, windowsShell }),
    '.')));
  return results.every(Boolean);
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

console.log(everything
  ? 'Scope: everything (--all).'
  : explicitFiles
    ? `Scope: ${files.length} given files.`
    : branchScope
      ? `Scope: ${files.length} changed files since ${base.slice(0, 10)}.`
      : `Scope: ${files.length} uncommitted changed files (--branch covers everything since origin/main).`);
console.log(allProviders
  ? 'Database providers: SQL Server and PostgreSQL. Each project is built once, then two test processes run at the same time: '
    + runs.map(providerRun => `${providerRun.label} (--filter "${providerRun.filter}")`).join(' next to ') + '.'
  : `Database providers: PostgreSQL only (--filter "${runs[0].filter}"; no persistence change; --all-providers runs both).`);
console.log(`.NET test projects: ${testProjects.length === 0 ? 'none' : ''}`);
testProjects.forEach(project => console.log(`  ${project}`));
console.log(`Angular libraries: ${angularLibraries.length === 0 ? 'none' : angularLibraries.join(', ')}`);
if (librariesWithoutSpecs.length > 0) {
  console.log(`Angular libraries without specs, not run: ${librariesWithoutSpecs.join(', ')}`);
}
console.log(`Sample checks: ${sampleChanged ? 'npm run verify:samples' : 'none'}`);

if (option('--dry-run')) {
  process.exit(0);
}

let passed = true;
for (const project of testProjects) {
  passed = await testProject(project, runs) && passed;
}
for (const library of angularLibraries) {
  passed = run(`ng test ${library}`, 'npx', angularTestArguments(library), frontEndRoot) && passed;
}
if (sampleChanged) {
  passed = run('sample verify:samples', 'npm', ['run', 'verify:samples'], sampleRoot) && passed;
}

console.log(passed
  ? 'All affected tests passed. Run the validators AGENTS.md lists for the files you changed.'
  : 'Affected tests failed.');
process.exit(passed ? 0 : 1);
