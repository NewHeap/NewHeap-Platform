// Pure helpers of `npm run test:affected`: which test projects and front-end libraries a change
// reaches, whether it needs both database providers, and how the build and test jobs are planned,
// started and reported. Kept free of I/O so they can be tested.

/** Normalizes a repository-relative path to forward slashes without a leading `./`. */
export function normalizePath(path) {
  return path.replaceAll('\\', '/').replace(/^\.\//, '');
}

/** Resolves `reference` relative to the directory of `fromFile`, both repository-relative. */
export function resolveRelative(fromFile, reference) {
  const parts = normalizePath(fromFile).split('/').slice(0, -1);
  for (const segment of normalizePath(reference).split('/')) {
    if (segment === '..') {
      parts.pop();
    } else if (segment !== '.' && segment !== '') {
      parts.push(segment);
    }
  }
  return parts.join('/');
}

/** The project references of a `.csproj`, as repository-relative project paths. */
export function parseProjectReferences(projectPath, content) {
  const references = [];
  for (const match of content.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
    references.push(resolveRelative(projectPath, match[1]));
  }
  return references;
}

/**
 * Whether a project runs tests: it references a test framework or says `IsTestProject`. A project
 * under `Tests` that is an executable, such as an AOT smoke test, is not one.
 */
export function isTestProject(content) {
  return /<IsTestProject>\s*true\s*<\/IsTestProject>/i.test(content)
    || /<PackageReference\s+Include="(xunit(\.v3)?|Microsoft\.NET\.Test\.Sdk|NUnit|MSTest\.TestFramework)"/i.test(content);
}

/** The project that owns `file`: the project whose folder is the longest prefix of the file's path. */
export function ownerProject(file, projects) {
  const path = normalizePath(file);
  let owner = null;
  for (const project of projects) {
    const folder = project.slice(0, project.lastIndexOf('/') + 1);
    if (path.startsWith(folder) && (owner === null || folder.length > owner.slice(0, owner.lastIndexOf('/') + 1).length)) {
      owner = project;
    }
  }
  return owner;
}

/**
 * Build files that change every project below their folder, such as `Directory.Packages.props`.
 * A change to one of them marks every project in that folder as changed.
 */
export const sharedBuildFiles = ['Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config', 'nuget.config'];

/**
 * The .NET test projects a change reaches: those whose own files changed and those that depend,
 * directly or through other projects, on a changed project.
 * @param {string[]} changedFiles repository-relative paths
 * @param {Map<string, string[]>} graph project path to the project paths it references
 * @param {Set<string>} testProjects the projects that run tests
 */
export function affectedTestProjects(changedFiles, graph, testProjects) {
  const projects = [...graph.keys()];
  const changed = new Set();
  for (const file of changedFiles.map(normalizePath)) {
    const name = file.slice(file.lastIndexOf('/') + 1);
    if (sharedBuildFiles.includes(name)) {
      const folder = file.slice(0, file.lastIndexOf('/') + 1);
      projects.filter(project => project.startsWith(folder)).forEach(project => changed.add(project));
      continue;
    }
    if (graph.has(file)) {
      changed.add(file);
      continue;
    }
    const owner = ownerProject(file, projects);
    if (owner !== null) {
      changed.add(owner);
    }
  }

  const dependents = new Map(projects.map(project => [project, []]));
  for (const [project, references] of graph) {
    for (const reference of references) {
      dependents.get(reference)?.push(project);
    }
  }

  const reached = new Set(changed);
  const queue = [...changed];
  while (queue.length > 0) {
    for (const dependent of dependents.get(queue.shift()) ?? []) {
      if (!reached.has(dependent)) {
        reached.add(dependent);
        queue.push(dependent);
      }
    }
  }

  return [...reached].filter(project => testProjects.has(project)).sort();
}

/**
 * The Angular projects of `angular.json` that `ng test` can run: those with a test target and at
 * least one spec file below their root. Karma stops with TS18003 for a library without specs, so such
 * a library is reported as untested instead of failing the run. `specFiles` are paths relative to the
 * Angular workspace, such as `projects/nh-common/src/x.spec.ts`.
 */
export function testableFrontEndLibraries(projects, specFiles) {
  const specs = specFiles.map(normalizePath);
  const testable = [];
  const withoutSpecs = [];
  for (const [name, project] of Object.entries(projects)) {
    if (!project.architect?.test) {
      continue;
    }
    const root = normalizePath(project.root ?? `projects/${name}`).replace(/\/$/, '');
    if (specs.some(file => file.startsWith(`${root}/`))) {
      testable.push(name);
    } else {
      withoutSpecs.push(name);
    }
  }
  return { testable, withoutSpecs };
}

/** The Angular libraries under `src/Front-end/projects` whose files changed. */
export function affectedFrontEndLibraries(changedFiles, libraries) {
  const affected = new Set();
  for (const file of changedFiles.map(normalizePath)) {
    const match = /^src\/Front-end\/projects\/([^/]+)\//.exec(file);
    if (match && libraries.includes(match[1])) {
      affected.add(match[1]);
    }
  }
  return [...affected].sort();
}

/**
 * The `npx` arguments that test one Angular library once. Karma runs it in headless Chrome: a
 * visible Chrome window that is hidden or minimized throttles timers and focus, and specs that need
 * a visible page, such as the collaboration specs of `nh-ai-chat`, then fail. The dots reporter
 * replaces the default progress reporter, whose "Executed n of m" line moves the cursor back over
 * itself: kept as one block, that output is a line per spec and pushes the failures out of view.
 * Dots prints each failing spec with its message and stack, and the totals at the end.
 */
export function angularTestArguments(library) {
  return ['ng', 'test', library, '--watch=false', '--browsers=ChromeHeadless', '--reporters=dots'];
}

/**
 * Changes that concern how data is stored, which must be verified on SQL Server and PostgreSQL:
 * migrations, EF models and contexts, stores, repositories, provider projects, provider tests and
 * the relational test infrastructure they run on.
 */
const persistencePatterns = [
  /\/Migrations\//,
  /\/Persistence\//,
  /\/DAL\//,
  /\/Entities\//,
  /DbContext[^/]*\.cs$/,
  /Repository[^/]*\.cs$/,
  /Store\.cs$/,
  /\.(SqlServer|PostgreSql|Sqlite)\//,
  /ProviderTest[^/]*\.cs$/,
  /DatabaseFixture[^/]*\.cs$/,
  /SchemaPool[^/]*\.cs$/,
  /\/NewHeap\.Platform\.Tests\.Relational\//
];

/**
 * The boolean parameters in C# test source whose name says SQL Server, such as `bool sqlServer`.
 * `providerRuns` sends a test to the SQL Server process when its display name contains `SqlServer`
 * in any casing, and a theory flag puts its name into the display name of both of its cases: the
 * other provider's case then leaves the PostgreSQL process and the default run. Name the provider
 * instead, for example with `RelationalTestProvider`, so only the SQL Server case says so.
 */
export function sqlServerFlagParameters(source) {
  return [...source.matchAll(/[(,]\s*bool\??\s+(\w*sql_?server\w*)\s*(?=[,)=])/gi)].map(match => match[1]);
}

/** Whether a change needs both database providers. */
export function needsAllProviders(changedFiles) {
  return changedFiles.map(normalizePath).some(file => persistencePatterns.some(pattern => pattern.test(file)));
}

/**
 * The `dotnet test` processes of one test project, each with its filter. Without a persistence change
 * one process leaves the SQL Server variants out and PostgreSQL, the faster container, stands for
 * both. With both providers the SQL Server variants and all other tests run as two processes at the
 * same time: together they run every test once, each starts only the database container its tests
 * use, and a test class no longer runs its SQL Server and PostgreSQL variants one after the other.
 * @returns {{ label: string, filter: string }[]}
 */
export function providerRuns(allProviders) {
  const withoutSqlServer = 'DisplayName!~SqlServer';
  return allProviders
    ? [
      { label: 'SQL Server', filter: 'DisplayName~SqlServer' },
      { label: 'PostgreSQL and other tests', filter: withoutSqlServer }
    ]
    : [{ label: 'PostgreSQL', filter: withoutSqlServer }];
}

/**
 * How the console logger of `dotnet test` reports: each failed test with its error message and stack
 * trace, and no line per passed test. `-v q` alone keeps the build quiet but names a failed test
 * without its message.
 */
export const dotnetConsoleLogger = 'console;verbosity=minimal';

/**
 * The arguments of `dotnet test` for one provider process. Processes that run at the same time test
 * the one build made beforehand (`noBuild`): building in each would write the same files. Through the
 * Windows shell the filter and the logger, whose `;` the shell would split on, need quotes; spawned
 * directly they do not.
 */
export function dotnetTestArguments(project, filter, { noBuild = false, windowsShell = false } = {}) {
  const quoted = value => windowsShell ? `"${value}"` : value;
  return [
    'test',
    project,
    '-v',
    'q',
    '-nologo',
    ...(noBuild ? ['--no-build'] : []),
    '--logger',
    quoted(dotnetConsoleLogger),
    '--filter',
    quoted(filter)
  ];
}

/** Where `ng test` and the sample checks run, relative to the repository. */
export const frontEndRoot = 'src/Front-end';
export const sampleRoot = 'examples/SampleProjectManagement';

/** How many .NET test projects run at the same time unless `--max-parallel <n>` says otherwise. */
export const defaultMaxParallel = 3;

/** The value of `--max-parallel`: the default when it is absent, null when it is not a whole number of at least 1. */
export function parseMaxParallel(value) {
  if (value === undefined) {
    return defaultMaxParallel;
  }
  return /^[1-9]\d*$/.test(value) ? Number(value) : null;
}

/**
 * Seconds a test project typically takes with both providers, so that a first run starts the
 * slowest projects first. Later runs use the durations of the run before. A project not listed
 * counts as fast.
 */
export const typicalTestSeconds = {
  'SampleProjectManagement.Core.Tests': 80,
  'NewHeap.Platform.AI.Chat.Tests': 45,
  'NewHeap.Platform.DatabaseRead.Tool.Tests': 25,
  'NewHeap.Platform.AspNet.Common.Tests': 20,
  'NewHeap.Platform.Media.Tests': 17,
  'NewHeap.Platform.AspNet.Proxy.Sqlite.Tests': 11,
  'NewHeap.Platform.AI.Tests': 9
};

/** The name of a project file without its folder and extension. */
export function projectName(project) {
  return project.slice(project.lastIndexOf('/') + 1).replace(/\.csproj$/, '');
}

/**
 * The test projects in the order they start: the slowest first, so that the longest runs do not
 * start last while the other slots are idle. A project counts its seconds of the previous run, else
 * its typical seconds, else 0; equal seconds keep path order.
 * @param {string[]} projects
 * @param {Record<string, number>} previousSeconds project path to the seconds its last run took
 */
export function slowestFirst(projects, previousSeconds = {}) {
  const seconds = project => previousSeconds[project] ?? typicalTestSeconds[projectName(project)] ?? 0;
  return [...projects].sort((first, second) => seconds(second) - seconds(first) || (first < second ? -1 : first > second ? 1 : 0));
}

/** The arguments of the one `dotnet build` before the tests. Through the Windows shell the target is quoted. */
export function dotnetBuildArguments(target, { windowsShell = false } = {}) {
  return ['build', windowsShell ? `"${target}"` : target, '-v', 'q', '-nologo'];
}

/**
 * A solution file of the given projects (absolute paths). One `dotnet build` of it builds every
 * affected test project of both solutions: MSBuild builds the libraries they share once and builds
 * independent projects in parallel.
 */
export function solutionOf(projectPaths) {
  const escape = path => path.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
  return ['<Solution>', ...projectPaths.map(path => `  <Project Path="${escape(path)}" />`), '</Solution>', ''].join('\n');
}

/**
 * Everything `npm run test:affected` runs, as jobs in the order they start. The first job builds
 * every affected .NET test project once; the test jobs need it and run with `--no-build`, so
 * processes that run at the same time never write the same files. Then each .NET test project runs,
 * the slowest first and at most `maxParallel` at a time, with its provider processes side by side.
 * The Angular libraries, one at a time because their headless Chrome instances compete for the CPU,
 * and the sample checks, which read sources only, need no build and start next to it. `maxParallel`
 * 1 runs one job after the other, in this order.
 * @param {object} options
 * @param {string} options.repositoryRoot absolute path; the generated solution lists absolute project paths
 * @param {string[]} options.testProjects repository-relative project paths
 * @param {{ label: string, filter: string }[]} options.runs the provider processes of each project
 * @param {string[]} options.angularLibraries
 * @param {boolean} options.sampleChecks
 * @param {number} options.maxParallel
 * @param {string} options.solutionPath where the solution of several test projects is written
 * @param {Record<string, number>} [options.previousSeconds]
 * @param {boolean} [options.windowsShell]
 */
export function testPlan({
  repositoryRoot,
  testProjects,
  runs,
  angularLibraries,
  sampleChecks,
  maxParallel,
  solutionPath,
  previousSeconds = {},
  windowsShell = false
}) {
  const throughSolution = testProjects.length > 1;
  const root = normalizePath(repositoryRoot).replace(/\/$/, '');
  const single = (name, lane, command, args, cwd) => ({ name, lane, processes: [{ label: name, command, args, cwd }] });
  const build = testProjects.length === 0
    ? null
    : {
      ...single(
        `build (${testProjects.length} test project${testProjects.length === 1 ? '' : 's'})`,
        'build',
        'dotnet',
        dotnetBuildArguments(throughSolution ? solutionPath : testProjects[0], { windowsShell }),
        '.'),
      solution: throughSolution
        ? { path: solutionPath, content: solutionOf(testProjects.map(project => `${root}/${project}`)) }
        : null
    };
  const jobs = [
    ...(build ? [build] : []),
    ...slowestFirst(testProjects, previousSeconds).map(project => ({
      name: project,
      lane: 'dotnet',
      needs: build?.name,
      processes: runs.map(providerRun => ({
        label: runs.length === 1 ? project : `${project} [${providerRun.label}]`,
        command: 'dotnet',
        args: dotnetTestArguments(project, providerRun.filter, { noBuild: true, windowsShell }),
        cwd: '.'
      }))
    })),
    ...angularLibraries.map(library => single(`ng test ${library}`, 'angular', 'npx', angularTestArguments(library), frontEndRoot)),
    ...(sampleChecks ? [single('sample verify:samples', 'sample', 'npm', ['run', 'verify:samples'], sampleRoot)] : [])
  ];
  return {
    build,
    jobs,
    limits: { total: maxParallel === 1 ? 1 : Infinity, lanes: { build: 1, dotnet: maxParallel, angular: 1, sample: 1 } }
  };
}

/**
 * Starts `jobs` in their order as far as `limits` allow: at most `limits.total` at a time, at most
 * `limits.lanes[lane]` of one lane, and a job with `needs` only after the job of that name ended. A
 * job that cannot start yet does not hold up the jobs behind it. Once `stopAfter` returns true for a
 * result, no further job starts and the jobs that run are awaited. Resolves with the result of each
 * job in job order; a job that never started has none.
 * @template T
 * @param {{ name: string, lane: string, needs?: string }[]} jobs
 * @param {{ total: number, lanes: Record<string, number> }} limits
 * @param {(job: { name: string, lane: string }) => Promise<T>} start
 * @param {{ stopAfter?: (result: T) => boolean }} [options]
 * @returns {Promise<(T | undefined)[]>}
 */
export function runJobs(jobs, limits, start, { stopAfter = () => false } = {}) {
  return new Promise((resolve, reject) => {
    const results = new Array(jobs.length).fill(undefined);
    const waiting = jobs.map((_, index) => index);
    const ended = new Set();
    const running = new Map();
    let runningTotal = 0;
    let stopped = false;
    const startWhatFits = () => {
      for (let position = 0; !stopped && position < waiting.length && runningTotal < limits.total;) {
        const index = waiting[position];
        const { lane, needs } = jobs[index];
        if ((running.get(lane) ?? 0) >= (limits.lanes[lane] ?? Infinity) || (needs !== undefined && !ended.has(needs))) {
          position++;
          continue;
        }
        waiting.splice(position, 1);
        running.set(lane, (running.get(lane) ?? 0) + 1);
        runningTotal++;
        Promise.resolve(start(jobs[index])).then(result => {
          results[index] = result;
          ended.add(jobs[index].name);
          running.set(lane, running.get(lane) - 1);
          runningTotal--;
          stopped ||= stopAfter(result);
          startWhatFits();
        }).catch(reject);
      }
      if (runningTotal === 0) {
        if (stopped || waiting.length === 0) {
          resolve(results);
        } else {
          reject(new Error(`These jobs need a job that is not planned: ${waiting.map(index => jobs[index].name).join(', ')}`));
        }
      }
    };
    startWhatFits();
  });
}

/** `text` without ANSI escape sequences: colours and the cursor moves of progress output. */
export function withoutAnsi(text) {
  return text.replace(/\x1b\[[0-9;?]*[A-Za-z]/g, '');
}

/** The count line a test run ends with, such as `Passed! - Failed: 0, ...` or Karma's `TOTAL: 12 SUCCESS`, without colours. */
export function outcomeSummary(output) {
  return withoutAnsi(output).split(/\r?\n/).reverse().find(line => /Passed!|Failed!|TOTAL:|Executed .* of/.test(line))?.trim();
}

/** Karma's line for a failed spec, `<browser> (<platform>) <suites> <spec> FAILED`, as the full spec name. */
function karmaFailedSpec(line) {
  return line.includes(': Executed ') ? undefined : /^\S.*? \([^)]*\) (.+) FAILED$/.exec(line)?.[1];
}

/**
 * The console logger's line for a failed .NET test, `  Failed <full name> [<duration>]`, as the test's
 * full name. Below it the logger prints the error message and the stack trace.
 */
function dotnetFailedTest(line) {
  return /^\s+Failed (\S.*?) \[[^\]]+\]$/.exec(line)?.[1];
}

/** The full name of the failed Karma spec or .NET test that `line` reports, if it reports one. */
function failedSpecOrTest(line) {
  return karmaFailedSpec(line) ?? dotnetFailedTest(line);
}

/**
 * The full names of the specs a Karma run, or the tests a `dotnet test` run, reports as failed, in
 * the order it reports them. Below each such line Karma prints the message and stack, every line
 * indented with a tab, and the .NET console logger the error message and stack trace.
 */
export function failedSpecs(output) {
  return withoutAnsi(output).split(/\r?\n/).map(failedSpecOrTest).filter(Boolean);
}

/** How many lines of a failed process's output its job block shows at least. */
const reportedTailLines = 60;

/**
 * The output a failed process shows: its last lines without ANSI escape sequences and, for a Karma
 * or `dotnet test` run, everything from the first failed spec or test on, so that no failure,
 * message or stack falls off.
 */
export function failureOutput(output) {
  const lines = withoutAnsi(output).split(/\r?\n/);
  const firstFailure = lines.findIndex(line => failedSpecOrTest(line) !== undefined);
  const tail = Math.max(0, lines.length - reportedTailLines);
  return lines.slice(firstFailure >= 0 ? Math.min(firstFailure, tail) : tail).join('\n');
}

/** The line of one process: `PASS <label> (12s) - <count line>`. */
export function resultLine({ label, passed, seconds, summary }) {
  return `${passed ? 'PASS' : 'FAIL'} ${label} (${seconds}s)${summary ? ` - ${summary}` : ''}`;
}

/** The block printed when a job ends: a line per process and, for a process that failed, its failures and the end of its output. */
export function jobReport(results) {
  return results
    .flatMap(result => result.passed ? [resultLine(result)] : [resultLine(result), failureOutput(result.output)])
    .join('\n');
}

/**
 * The summary after the run: every process with its duration, then the failures, each with the specs
 * that failed in it, or the closing line.
 * @param {{ label: string, passed: boolean, seconds: number, output?: string }[]} results
 * @param {number} wallSeconds
 */
export function summaryLines(results, wallSeconds) {
  const failed = results.filter(result => !result.passed);
  const width = Math.max(0, ...results.map(result => String(result.seconds).length));
  return [
    `Summary, ${wallSeconds}s in total:`,
    ...results.map(result => `  ${result.passed ? 'PASS' : 'FAIL'} ${String(result.seconds).padStart(width)}s  ${result.label}`),
    ...(failed.length === 0
      ? ['All affected tests passed. Run the validators AGENTS.md lists for the files you changed.']
      : [
        `Affected tests failed: ${failed.length} of ${results.length}.`,
        ...failed.flatMap(result => [`  ${result.label}`, ...failedSpecs(result.output ?? '').map(spec => `    ${spec}`)])
      ])
  ];
}
