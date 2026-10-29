// Tests of the helpers behind `npm run test:affected`, and a check that the repository's tests keep
// to the naming its SQL Server filter relies on.
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  affectedFrontEndLibraries,
  affectedTestProjects,
  angularTestArguments,
  buildsThroughSolution,
  checkoutKey,
  defaultHangTimeoutSeconds,
  defaultMaxParallel,
  dotnetTestArguments,
  durationsFilePath,
  failedSpecs,
  hungTests,
  isTestProject,
  jobReport,
  needsAllProviders,
  normalizePath,
  outcomeSummary,
  ownerProject,
  parseHangTimeout,
  parseMaxParallel,
  parseProjectReferences,
  providerRuns,
  resolveRelative,
  resultLine,
  runJobs,
  slowestFirst,
  sqlServerFlagParameters,
  summaryLines,
  testableFrontEndLibraries,
  solutionOf,
  testPlan,
  typicalTestSeconds,
  validDurations
} from './affected-lib.mjs';
import {
  createRunFolder,
  readDurations,
  removeRunFolder,
  solutionFileName,
  writeDurations,
  writeSolution
} from './affected-state.mjs';

const common = 'src/Back-end/Libraries/NewHeap.Platform.Common/NewHeap.Platform.Common.csproj';
const chat = 'src/Back-end/Libraries/NewHeap.Platform.AI.Chat/NewHeap.Platform.AI.Chat.csproj';
const chatAspNet = 'src/Back-end/Libraries/NewHeap.Platform.AI.Chat.AspNet/NewHeap.Platform.AI.Chat.AspNet.csproj';
const media = 'src/Back-end/Libraries/NewHeap.Platform.Media/NewHeap.Platform.Media.csproj';
const commonTests = 'src/Back-end/Tests/NewHeap.Platform.Common.Tests/NewHeap.Platform.Common.Tests.csproj';
const chatTests = 'src/Back-end/Tests/NewHeap.Platform.AI.Chat.Tests/NewHeap.Platform.AI.Chat.Tests.csproj';
const mediaTests = 'src/Back-end/Tests/NewHeap.Platform.Media.Tests/NewHeap.Platform.Media.Tests.csproj';
const sampleApi = 'examples/SampleProjectManagement/src/Back-end/Applications/SampleProjectManagement.Api/SampleProjectManagement.Api.csproj';
const sampleTests = 'examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests/SampleProjectManagement.Core.Tests.csproj';

const graph = new Map([
  [common, []],
  [chat, [common]],
  [chatAspNet, [chat]],
  [media, [common]],
  [commonTests, [common]],
  [chatTests, [chatAspNet]],
  [mediaTests, [media]],
  [sampleApi, [chatAspNet, media]],
  [sampleTests, [sampleApi]]
]);

// Project references resolve relative to the project file.
assert.deepEqual(
  parseProjectReferences(sampleTests, '<ProjectReference Include="../../../../../../src/Back-end/Libraries/NewHeap.Platform.Common/NewHeap.Platform.Common.csproj" />'),
  [common]);
assert.equal(resolveRelative('a/b/c.csproj', String.raw`..\d\e.csproj`), 'a/d/e.csproj');
assert.ok(isTestProject('<ItemGroup><PackageReference Include="xunit" /></ItemGroup>'));
assert.ok(isTestProject('<ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup>'));
assert.ok(!isTestProject('<PropertyGroup><OutputType>Exe</OutputType><PublishAot>true</PublishAot></PropertyGroup>'));
const tests = new Set([commonTests, chatTests, mediaTests, sampleTests]);

// A file belongs to the project with the longest folder prefix, not to a sibling with a shared name start.
assert.equal(ownerProject('src/Back-end/Libraries/NewHeap.Platform.AI.Chat.AspNet/Endpoints.cs', [...graph.keys()]), chatAspNet);
assert.equal(ownerProject('docs/plans/plan.md', [...graph.keys()]), null);

// A change reaches the tests of its project and of every project that depends on it.
assert.deepEqual(affectedTestProjects(['src/Back-end/Libraries/NewHeap.Platform.AI.Chat/Agents/Agent.cs'], graph, tests), [sampleTests, chatTests].sort());
assert.deepEqual(affectedTestProjects(['src/Back-end/Libraries/NewHeap.Platform.Media/Media.cs'], graph, tests), [sampleTests, mediaTests].sort());
assert.deepEqual(affectedTestProjects(['src/Back-end/Tests/NewHeap.Platform.Common.Tests/SomeTest.cs'], graph, tests), [commonTests]);
assert.deepEqual(affectedTestProjects(['docs/release-notes/v-next.md', 'src/Front-end/projects/nh-ai-chat/x.ts'], graph, tests), []);
// Shared build files reach every project below them.
assert.deepEqual(affectedTestProjects(['src/Back-end/Directory.Packages.props'], graph, tests), [sampleTests, commonTests, chatTests, mediaTests].sort());

// Angular libraries follow their own folder.
assert.deepEqual(
  affectedFrontEndLibraries(['src/Front-end/projects/nh-ai-chat/src/lib/x.ts', 'src/Front-end/package.json'], ['nh-common', 'nh-ai-chat']),
  ['nh-ai-chat']);
// Only libraries with a test target and specs below their root are run; Karma refuses one without specs.
assert.deepEqual(
  testableFrontEndLibraries(
    {
      'nh-common': { root: 'projects/nh-common', architect: { test: {} } },
      'nh-toastr': { root: 'projects/nh-toastr', architect: { test: {} } },
      'nh-ai-chat': { root: 'projects/nh-ai-chat/', architect: { test: {} } },
      'nh-ai-chat-extra': { root: 'projects/nh-ai-chat-extra', architect: { test: {} } },
      management: { root: 'projects/management', architect: { build: {} } }
    },
    ['projects/nh-common/two-factor/src/x.spec.ts', String.raw`projects\nh-ai-chat\src\y.spec.ts`, 'projects/management/z.spec.ts']),
  { testable: ['nh-common', 'nh-ai-chat'], withoutSpecs: ['nh-toastr', 'nh-ai-chat-extra'] });
// Angular libraries run once, in headless Chrome, so a hidden browser window cannot fail them, and
// report through dots, whose output keeps the failed specs where progress output would bury them.
assert.deepEqual(
  angularTestArguments('nh-ai-chat'),
  ['ng', 'test', 'nh-ai-chat', '--watch=false', '--browsers=ChromeHeadless', '--reporters=dots']);

// Persistence changes run both providers; other changes one.
assert.ok(needsAllProviders(['src/Back-end/Libraries/NewHeap.Platform.AI.Chat.SqlServer/Migrations/20261019_X.cs']));
assert.ok(needsAllProviders(['src/Back-end/Libraries/NewHeap.Platform.AI.Chat/Persistence/NhAssistantDbContext.cs']));
assert.ok(needsAllProviders(['src/Back-end/Tests/NewHeap.Platform.AspNet.Common.Tests/BulkUpsertProviderTests.cs']));
// So do the shared relational test servers and pools and the provider tests' own database setup.
assert.ok(needsAllProviders(['src/Back-end/Tests/NewHeap.Platform.Tests.Relational/RelationalTestServer.cs']));
assert.ok(needsAllProviders(['src/Back-end/Tests/NewHeap.Platform.AspNet.Common.Tests/ProviderTestDatabase.cs']));
assert.ok(needsAllProviders(['src/Back-end/Tests/NewHeap.Platform.AI.Chat.Tests/Infrastructure/AssistantSchemaPool.cs']));
assert.ok(!needsAllProviders(['src/Back-end/Libraries/NewHeap.Platform.Common/Extensions/NhConfigurationSecrets.cs']));
// One provider leaves the SQL Server variants out; both providers split every test over two processes.
assert.deepEqual(providerRuns(false), [{ label: 'PostgreSQL', filter: 'DisplayName!~SqlServer' }]);
assert.deepEqual(providerRuns(true), [
  { label: 'SQL Server', filter: 'DisplayName~SqlServer' },
  { label: 'PostgreSQL and other tests', filter: 'DisplayName!~SqlServer' }
]);
// Processes that run side by side test one build and print each failed test with its message; the
// Windows shell needs the logger and the filter quoted.
assert.deepEqual(
  dotnetTestArguments(chatTests, 'DisplayName~SqlServer', { noBuild: true }),
  ['test', chatTests, '-v', 'q', '-nologo', '--no-build', '--logger', 'console;verbosity=minimal', '--filter', 'DisplayName~SqlServer']);
assert.deepEqual(
  dotnetTestArguments(chatTests, 'DisplayName!~SqlServer', { windowsShell: true }),
  ['test', chatTests, '-v', 'q', '-nologo', '--logger', '"console;verbosity=minimal"', '--filter', '"DisplayName!~SqlServer"']);
// With a hang timeout the test platform stops a test process whose test runs longer, without a dump.
assert.deepEqual(
  dotnetTestArguments(chatTests, 'DisplayName!~SqlServer', { noBuild: true, hangTimeoutSeconds: 600 }).slice(-6),
  ['DisplayName!~SqlServer', '--blame-hang', '--blame-hang-dump-type', 'none', '--blame-hang-timeout', '600s']);
assert.ok(!dotnetTestArguments(chatTests, 'DisplayName!~SqlServer', { hangTimeoutSeconds: 0 }).includes('--blame-hang'));

// --hang-timeout and NH_TEST_HANG_TIMEOUT take a whole number with s, m or h (minutes without a unit); 0 is off.
assert.equal(parseHangTimeout(undefined), defaultHangTimeoutSeconds);
assert.equal(defaultHangTimeoutSeconds, 600);
assert.equal(parseHangTimeout('30s'), 30);
assert.equal(parseHangTimeout('15'), 900);
assert.equal(parseHangTimeout('15m'), 900);
assert.equal(parseHangTimeout('1h'), 3600);
assert.equal(parseHangTimeout('0'), 0);
for (const invalid of ['', '-1', '1.5m', '10 min', 'never', '5d']) {
  assert.equal(parseHangTimeout(invalid), null, invalid);
}

// --max-parallel takes a whole number of at least 1; without it three test projects run at a time.
assert.equal(parseMaxParallel(undefined), defaultMaxParallel);
assert.equal(defaultMaxParallel, 3);
assert.equal(parseMaxParallel('1'), 1);
assert.equal(parseMaxParallel('12'), 12);
for (const invalid of ['', '0', '-2', '1.5', 'many']) {
  assert.equal(parseMaxParallel(invalid), null, invalid);
}

// The slowest projects start first: the previous run's seconds, else the typical seconds, else path order.
const pathOf = name => `src/Back-end/Tests/${name}/${name}.csproj`;
const [typicallySlowest] = Object.entries(typicalTestSeconds).sort((first, second) => second[1] - first[1])[0] ?? [];
assert.ok(typicallySlowest, 'typicalTestSeconds names the slow test projects');
const typicallySlow = pathOf(typicallySlowest);
const [quickA, quickB, quickC] = ['QuickA', 'QuickB', 'QuickC'].map(name => pathOf(`NewHeap.Platform.${name}.Tests`));
assert.deepEqual(slowestFirst([quickC, quickA, typicallySlow, quickB]), [typicallySlow, quickA, quickB, quickC]);
assert.deepEqual(
  slowestFirst([quickC, quickA, typicallySlow, quickB], { [quickC]: 100_000, [quickB]: 0 }),
  [quickC, typicallySlow, quickA, quickB]);

const manyTests = ['A', 'B', 'C', 'D', 'E', 'F', 'G'].map(letter => pathOf(`NewHeap.Platform.${letter}.Tests`));
const planOptions = {
  repositoryRoot: 'C:\\repo\\',
  testProjects: manyTests,
  runs: providerRuns(true),
  angularLibraries: ['nh-common', 'nh-ai-chat'],
  sampleChecks: true,
  maxParallel: 3,
  solutionPath: '/tmp/affected-tests.slnx',
  previousSeconds: { [manyTests[4]]: 300, [manyTests[2]]: 200, [manyTests[0]]: 1 }
};
const plan = testPlan({ ...planOptions, hangTimeoutSeconds: 600 });

// The test projects are built once, by the first job, through a generated solution; every test process uses that build.
assert.equal(plan.jobs[0], plan.build);
assert.deepEqual(plan.build.processes.map(step => step.args), [['build', '/tmp/affected-tests.slnx', '-v', 'q', '-nologo']]);
assert.equal(plan.build.solution.path, '/tmp/affected-tests.slnx');
for (const project of manyTests) {
  assert.equal(plan.build.solution.content.split(`<Project Path="C:/repo/${project}" />`).length, 2, project);
}
const processes = plan.jobs.flatMap(job => job.processes);
assert.equal(processes.filter(step => step.args[0] === 'build').length, 1);
const testProcesses = processes.filter(step => step.command === 'dotnet' && step.args[0] !== 'build');
assert.equal(testProcesses.length, manyTests.length * 2);
assert.ok(testProcesses.every(step => step.args[0] === 'test' && step.args.includes('--no-build')));
// Every test process carries the hang timeout.
assert.ok(testProcesses.every(step => step.args.at(-1) === '600s' && step.args.includes('--blame-hang')));
assert.ok(plan.jobs.filter(job => job.lane === 'dotnet').every(job => job.needs === plan.build.name));
assert.ok(plan.jobs.filter(job => job.lane !== 'dotnet').every(job => job.needs === undefined));
// One test project builds without a generated solution; nothing to test builds nothing.
const singlePlan = testPlan({ ...planOptions, testProjects: [chatTests], windowsShell: true });
assert.deepEqual(singlePlan.build.processes[0].args, ['build', `"${chatTests}"`, '-v', 'q', '-nologo']);
assert.equal(singlePlan.build.solution, null);
const angularOnly = testPlan({ ...planOptions, testProjects: [] });
assert.equal(angularOnly.build, null);
assert.deepEqual(angularOnly.jobs.map(job => job.lane), ['angular', 'angular', 'sample']);

// Jobs start with the build, then the test projects slowest first, then the Angular libraries and the sample checks.
assert.deepEqual(plan.jobs.map(job => job.name), [
  'build (7 test projects)',
  manyTests[4], manyTests[2], manyTests[0], manyTests[1], manyTests[3], manyTests[5], manyTests[6],
  'ng test nh-common', 'ng test nh-ai-chat', 'sample verify:samples'
]);
assert.deepEqual(plan.jobs[1].processes.map(step => step.label), [`${manyTests[4]} [SQL Server]`, `${manyTests[4]} [PostgreSQL and other tests]`]);

/** Runs jobs with fake processes that take `ticks(job)` turns of the event loop, and records what overlapped. */
async function simulate(jobs, limits, ticks, { fails = () => false } = {}) {
  const running = new Map();
  const peak = new Map();
  const started = [];
  const overlaps = new Set();
  let total = 0;
  let peakTotal = 0;
  const results = await runJobs(jobs, limits, async job => {
    started.push(job.name);
    running.set(job.lane, (running.get(job.lane) ?? 0) + 1);
    total++;
    peak.set(job.lane, Math.max(peak.get(job.lane) ?? 0, running.get(job.lane)));
    peakTotal = Math.max(peakTotal, total);
    for (const [lane, count] of running) {
      if (lane !== job.lane && count > 0) {
        overlaps.add(`${job.lane} beside ${lane}`);
      }
    }
    for (let tick = 0; tick < ticks(job); tick++) {
      await new Promise(next => setImmediate(next));
    }
    running.set(job.lane, running.get(job.lane) - 1);
    total--;
    return { name: job.name, passed: !fails(job) };
  }, { stopAfter: result => !result.passed });
  return { results, started, peak: Object.fromEntries(peak), peakTotal, overlaps };
}
const ticksOf = job => job.lane === 'dotnet' ? 2 + (job.name.charCodeAt(job.name.lastIndexOf('.Tests') - 1) % 5) : job.lane === 'build' ? 4 : 3;

// At most three test projects at a time, after the build; one Angular library at a time, from the start.
const parallel = await simulate(plan.jobs, plan.limits, ticksOf);
assert.deepEqual(parallel.peak, { build: 1, dotnet: 3, angular: 1, sample: 1 });
assert.ok(parallel.overlaps.has('angular beside build'), 'Angular libraries start next to the build');
assert.ok(parallel.overlaps.has('angular beside dotnet') || parallel.overlaps.has('dotnet beside angular'), 'Angular libraries run alongside the .NET tests');
assert.ok(!parallel.overlaps.has('dotnet beside build'), 'no test project starts before the build ended');
assert.deepEqual(parallel.started.filter(name => name.endsWith('.csproj')), plan.jobs.filter(job => job.lane === 'dotnet').map(job => job.name));
assert.deepEqual(parallel.results.map(result => result.name), plan.jobs.map(job => job.name));
// --max-parallel 1 runs one job after the other in plan order: the build, the test projects, Angular, the sample checks.
const sequentialPlan = testPlan({ ...planOptions, maxParallel: 1 });
const sequential = await simulate(sequentialPlan.jobs, sequentialPlan.limits, ticksOf);
assert.equal(sequential.peakTotal, 1);
assert.deepEqual(sequential.started, sequentialPlan.jobs.map(job => job.name));
// A higher bound runs every job once, still building once.
const widePlan = testPlan({ ...planOptions, maxParallel: 10 });
const wide = await simulate(widePlan.jobs, widePlan.limits, ticksOf);
assert.deepEqual(wide.peak, { build: 1, dotnet: 7, angular: 1, sample: 1 });
assert.equal(wide.started.length, plan.jobs.length);
assert.equal(wide.started.filter(name => name.startsWith('build')).length, 1);
// A failed build stops the run: no test project starts and nothing new starts, but a running job ends.
const brokenBuild = await simulate(plan.jobs, plan.limits, job => job.lane === 'build' ? 1 : ticksOf(job), { fails: job => job.lane === 'build' });
assert.deepEqual(brokenBuild.started, ['build (7 test projects)', 'ng test nh-common', 'sample verify:samples']);
assert.deepEqual(brokenBuild.results.map(result => result?.name), [
  'build (7 test projects)', ...manyTests.map(() => undefined), 'ng test nh-common', undefined, 'sample verify:samples'
]);
assert.deepEqual(await runJobs([], plan.limits, () => assert.fail('no job')), []);
await assert.rejects(runJobs([{ name: 'test', lane: 'dotnet', needs: 'build' }], plan.limits, async () => 'ran'), /need a job that is not planned/);

// Each process reports one line, with the count line of its test run; a failure adds the end of its output.
assert.equal(outcomeSummary('build\n\x1b[32mPassed!\x1b[0m  - Failed: 0, Passed: 12\nok'), 'Passed!  - Failed: 0, Passed: 12');
assert.equal(resultLine({ label: chatTests, passed: true, seconds: 12, summary: 'Passed! - Failed: 0' }), `PASS ${chatTests} (12s) - Passed! - Failed: 0`);
assert.equal(resultLine({ label: 'ng test nh-common', passed: false, seconds: 3 }), 'FAIL ng test nh-common (3s)');
const failing = { label: 'ng test nh-common', passed: false, seconds: 3, output: Array.from({ length: 80 }, (_, line) => `line ${line}`).join('\n') };
const passing = { label: chatTests, passed: true, seconds: 12, summary: 'Passed!', output: 'quiet' };
const report = jobReport([passing, failing]).split('\n');
assert.deepEqual(report.slice(0, 2), [`PASS ${chatTests} (12s) - Passed!`, 'FAIL ng test nh-common (3s)']);
assert.equal(report.length, 2 + 60);
assert.equal(report.at(-1), 'line 79');
// The summary lists every process with its duration, then the failures; without failures it says so.
assert.deepEqual(summaryLines([{ label: 'build (2 test projects)', passed: true, seconds: 41 }, passing, failing], 130), [
  'Summary, 130s in total:',
  '  PASS 41s  build (2 test projects)',
  `  PASS 12s  ${chatTests}`,
  '  FAIL  3s  ng test nh-common',
  'Affected tests failed: 1 of 3.',
  '  ng test nh-common'
]);
assert.equal(summaryLines([passing], 12).at(-1), 'All affected tests passed. Run the validators AGENTS.md lists for the files you changed.');
// A failed Karma run names each failed spec, with its message and stack in the job block however
// much output precedes or follows, and under its job in the summary.
const browser = 'Chrome Headless 141.0.0.0 (Windows 10)';
const karmaOutput = [
  ...Array.from({ length: 70 }, (_, line) => `build ${line}`),
  '.'.repeat(80),
  `\x1b[31m${browser} NhDatePipe formats (a) the day FAILED\x1b[39m`,
  "\tError: Expected '1' to equal '01'.",
  '\t    at <Jasmine>',
  '\t    at UserContext.apply (projects/nh-common/src/date.pipe.spec.ts:12:20)',
  '....',
  `${browser} NhToast closes FAILED`,
  ...Array.from({ length: 70 }, (_, line) => `\t    at frame ${line}`),
  '..',
  `${browser}: Executed 86 of 86 (2 FAILED) (0.4 secs / 0.3 secs)`,
  '\x1b[31mTOTAL: 2 FAILED, 84 SUCCESS\x1b[39m'
].join('\n');
assert.deepEqual(failedSpecs(karmaOutput), ['NhDatePipe formats (a) the day', 'NhToast closes']);
assert.deepEqual(failedSpecs(`${browser}: Executed 3 of 3 SUCCESS (0.1 secs / 0.1 secs)\nTOTAL: 3 SUCCESS`), []);
assert.equal(outcomeSummary(karmaOutput), 'TOTAL: 2 FAILED, 84 SUCCESS');
const karmaFailing = { label: 'ng test nh-common', passed: false, seconds: 9, output: karmaOutput };
const karmaReport = jobReport([karmaFailing]).split('\n');
assert.deepEqual(karmaReport.slice(0, 3), [
  'FAIL ng test nh-common (9s)',
  `${browser} NhDatePipe formats (a) the day FAILED`,
  "\tError: Expected '1' to equal '01'."
]);
assert.ok(karmaReport.includes('\t    at frame 69'));
assert.equal(karmaReport.at(-1), 'TOTAL: 2 FAILED, 84 SUCCESS');
assert.ok(!karmaReport.some(line => line.includes('\x1b')));
assert.ok(!karmaReport.includes('build 69'));
assert.deepEqual(summaryLines([passing, karmaFailing], 20).slice(-4), [
  'Affected tests failed: 1 of 2.',
  '  ng test nh-common',
  '    NhDatePipe formats (a) the day',
  '    NhToast closes'
]);
// A failed .NET test run shows every failed test with its error message and stack trace in the job
// block, however long the stacks, and names the failed tests under the process in the summary.
const dotnetOutput = [
  `Test run for C:\\repo\\${chatTests}.dll (.NETCoreApp,Version=v10.0)`,
  'A total of 1 test files matched the specified pattern.',
  '[xUnit.net 00:02:01.62]     NewHeap.Tests.Leases.A_lease_expires(provider: SqlServer) [FAIL]',
  '  Failed NewHeap.Tests.Leases.A_lease_expires(provider: SqlServer) [1 s]',
  '  Error Message:',
  '   Expected operation.Status to be Cancelled, but found RetryScheduled.',
  '  Stack Trace:',
  ...Array.from({ length: 70 }, (_, line) => `     at frame ${line}`),
  '  Failed NewHeap.Tests.Steps.A_step_fails(provider: SqlServer, level: Root) [12 ms]',
  '  Error Message:',
  '   System.TimeoutException : The operation has timed out.',
  '',
  'Failed!  - Failed:     2, Passed:    50, Skipped:     0, Total:    52, Duration: 4 m 22 s - Tests.dll (net10.0)'
].join('\n');
assert.deepEqual(failedSpecs(dotnetOutput), [
  'NewHeap.Tests.Leases.A_lease_expires(provider: SqlServer)',
  'NewHeap.Tests.Steps.A_step_fails(provider: SqlServer, level: Root)'
]);
assert.deepEqual(failedSpecs('Passed!  - Failed:     0, Passed:    52, Skipped:     0, Total:    52'), []);
const dotnetFailing = { label: `${chatTests} [SQL Server]`, passed: false, seconds: 265, output: dotnetOutput };
const dotnetReport = jobReport([dotnetFailing]).split('\n');
assert.deepEqual(dotnetReport.slice(0, 4), [
  `FAIL ${chatTests} [SQL Server] (265s)`,
  '  Failed NewHeap.Tests.Leases.A_lease_expires(provider: SqlServer) [1 s]',
  '  Error Message:',
  '   Expected operation.Status to be Cancelled, but found RetryScheduled.'
]);
assert.ok(dotnetReport.includes('   System.TimeoutException : The operation has timed out.'));
assert.deepEqual(summaryLines([dotnetFailing], 300).slice(-3), [
  `  ${chatTests} [SQL Server]`,
  '    NewHeap.Tests.Leases.A_lease_expires(provider: SqlServer)',
  '    NewHeap.Tests.Steps.A_step_fails(provider: SqlServer, level: Root)'
]);

// A test process the hang detection stopped names the test that still ran, in its job block and in the
// summary; a process that crashed for another reason names no hang.
const hungOutput = [
  'A total of 1 test files matched the specified pattern.',
  'The active test run was aborted. Reason: Test host process crashed',
  "Data collector 'Blame' message: The specified inactivity time of 600 seconds has elapsed. Collecting hang dumps from testhost and its child processes.",
  'Passed!  - Failed:     0, Passed:    67, Skipped:     0, Total:    67, Duration: 4 sTest Run Aborted.',
  'The active Test Run was aborted because the host process exited unexpectedly.',
  'The test running when the crash occurred: ',
  'NewHeap.Tests.Leases.A_lease_waits_forever(provider: PostgreSql)',
  '',
  'This test may, or may not be the source of the crash.'
].join('\r\n');
assert.deepEqual(hungTests(hungOutput), ['NewHeap.Tests.Leases.A_lease_waits_forever(provider: PostgreSql)']);
assert.deepEqual(hungTests(hungOutput.replace('The specified inactivity time of 600 seconds has elapsed.', 'The test host exited.')), []);
assert.deepEqual(
  hungTests(hungOutput
    .replace('The test running', 'The tests running')
    .replace('(provider: PostgreSql)\r\n', '(provider: PostgreSql)\r\nNewHeap.Tests.Steps.A_step_waits\r\n')),
  ['NewHeap.Tests.Leases.A_lease_waits_forever(provider: PostgreSql)', 'NewHeap.Tests.Steps.A_step_waits']);
assert.equal(outcomeSummary('The active test run was aborted.\nTest Run Aborted.'), 'Test Run Aborted.');
const hungFailing = { label: `${chatTests} [PostgreSQL]`, passed: false, seconds: 610, output: hungOutput };
assert.ok(jobReport([hungFailing]).includes('NewHeap.Tests.Leases.A_lease_waits_forever(provider: PostgreSql)'));
assert.deepEqual(summaryLines([hungFailing], 615).slice(-2), [
  `  ${chatTests} [PostgreSQL]`,
  '    NewHeap.Tests.Leases.A_lease_waits_forever(provider: PostgreSql) (hung: stopped by the hang detection)'
]);

// A provider flag named after SQL Server would send both of its cases to the SQL Server process.
assert.deepEqual(sqlServerFlagParameters('public void Translates(bool sqlServer)'), ['sqlServer']);
assert.deepEqual(sqlServerFlagParameters('public void Translates(string query,\n        bool useSqlServer = false)'), ['useSqlServer']);
assert.deepEqual(
  sqlServerFlagParameters('public void Translates(RelationalTestProvider provider) { bool sqlServer = provider == RelationalTestProvider.SqlServer; }'),
  []);
const repositoryRoot = resolve(fileURLToPath(new URL('.', import.meta.url)), '..', '..');
const flagged = [];
function scanTests(folder) {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const path = join(folder, entry.name);
    if (entry.isDirectory() && !['bin', 'obj'].includes(entry.name)) {
      scanTests(path);
    } else if (entry.isFile() && entry.name.endsWith('.cs')) {
      for (const name of sqlServerFlagParameters(readFileSync(path, 'utf8'))) {
        flagged.push(`${normalizePath(relative(repositoryRoot, path))}: bool ${name}`);
      }
    }
  }
}
scanTests(join(repositoryRoot, 'src/Back-end/Tests'));
scanTests(join(repositoryRoot, 'examples/SampleProjectManagement/src/Back-end/Tests'));
assert.deepEqual(flagged, [], 'Name the provider of these tests instead of a SQL Server flag; see "Test project ownership" in AGENTS.md.');

// The durations live in the user's own cache folder, never in the shared temp folder; a variable that
// is missing or relative falls back to the home folder.
const windowsKey = checkoutKey('C:\\Repo');
assert.equal(windowsKey, checkoutKey('c:\\repo'));
assert.equal(
  durationsFilePath({ platform: 'win32', env: { LOCALAPPDATA: 'D:\\Profiles\\dev\\Local' }, home: 'C:\\Users\\dev', repositoryRoot: 'C:\\Repo' }),
  `D:\\Profiles\\dev\\Local\\newheap\\test-affected\\${windowsKey}\\durations.json`);
assert.equal(
  durationsFilePath({ platform: 'win32', env: {}, home: 'C:\\Users\\dev', repositoryRoot: 'C:\\Repo' }),
  `C:\\Users\\dev\\AppData\\Local\\newheap\\test-affected\\${windowsKey}\\durations.json`);
const posixKey = checkoutKey('/work/repo');
assert.equal(
  durationsFilePath({ platform: 'linux', env: { XDG_CACHE_HOME: '/home/dev/.cache-xdg' }, home: '/home/dev', repositoryRoot: '/work/repo' }),
  `/home/dev/.cache-xdg/newheap/test-affected/${posixKey}/durations.json`);
for (const env of [{}, { XDG_CACHE_HOME: 'relative/cache' }, { TMPDIR: '/tmp' }]) {
  assert.equal(
    durationsFilePath({ platform: 'linux', env, home: '/home/dev', repositoryRoot: '/work/repo' }),
    `/home/dev/.cache/newheap/test-affected/${posixKey}/durations.json`);
}
assert.equal(
  durationsFilePath({ platform: 'darwin', env: {}, home: '/Users/dev', repositoryRoot: '/work/repo' }),
  `/Users/dev/.cache/newheap/test-affected/${posixKey}/durations.json`);
// Only seconds of test projects count; anything else in the file is left out.
assert.deepEqual(
  validDurations({ [commonTests]: 12, [chatTests]: -1, [mediaTests]: '9', 'not-a-project': 5, [`${commonTests}x`]: 3 }),
  { [commonTests]: 12 });
assert.deepEqual(validDurations([12]), {});
assert.deepEqual(validDurations(null), {});
assert.equal(buildsThroughSolution([commonTests]), false);
assert.equal(buildsThroughSolution([commonTests, chatTests]), true);

// Concurrent runs and other local accounts cannot interfere: earlier versions wrote the solution and
// the durations into a folder with a predictable name in the shared temp folder, which another account
// could create first and fill with links to files it wants overwritten. Each run now writes its
// solution into a new private folder of its own, as a new file, and leaves such a folder alone.
const sandbox = await mkdtemp(join(tmpdir(), 'newheap-test-affected-lib-'));
try {
  const planted = join(sandbox, 'newheap-test-affected', checkoutKey(repositoryRoot));
  await mkdir(planted, { recursive: true });
  const victims = { [solutionFileName]: join(sandbox, 'victim-solution.txt'), 'durations.json': join(sandbox, 'victim-durations.txt') };
  for (const [name, victim] of Object.entries(victims)) {
    await writeFile(victim, `original ${name}`, 'utf8');
    try {
      await symlink(victim, join(planted, name), 'file');
    } catch {
      // Windows without the symbolic-link privilege: a planted file stands in for the link.
      await writeFile(join(planted, name), 'planted', 'utf8');
    }
  }

  const firstRun = await createRunFolder(sandbox);
  const secondRun = await createRunFolder(sandbox);
  assert.notEqual(firstRun, secondRun);
  for (const runFolder of [firstRun, secondRun]) {
    assert.equal(dirname(runFolder), sandbox);
    assert.match(basename(runFolder), /^newheap-test-affected-.{6}$/);
    assert.deepEqual(await readdir(runFolder), []);
    if (process.platform !== 'win32') {
      assert.equal((await stat(runFolder)).mode & 0o777, 0o700);
    }
  }

  const solution = join(firstRun, solutionFileName);
  await writeSolution(solution, solutionOf(['/work/repo/A.Tests.csproj']));
  assert.equal(await readFile(solution, 'utf8'), solutionOf(['/work/repo/A.Tests.csproj']));
  if (process.platform !== 'win32') {
    assert.equal((await stat(solution)).mode & 0o777, 0o600);
  }
  // The write never follows or replaces what is already there, a planted link included.
  await assert.rejects(writeSolution(solution, 'replaced'), { code: 'EEXIST' });
  await assert.rejects(writeSolution(join(planted, solutionFileName), 'replaced'), { code: 'EEXIST' });

  const durations = join(sandbox, 'cache', 'newheap', 'test-affected', 'checkout', 'durations.json');
  assert.deepEqual(await readDurations(durations), {});
  await writeDurations(durations, { [commonTests]: 3 });
  await writeDurations(durations, { [commonTests]: 4, [chatTests]: 40 });
  assert.deepEqual(await readDurations(durations), { [commonTests]: 4, [chatTests]: 40 });
  assert.deepEqual(await readdir(dirname(durations)), ['durations.json'], 'No partly written durations stay behind.');
  if (process.platform !== 'win32') {
    assert.equal((await stat(dirname(durations))).mode & 0o777, 0o700);
  }

  await removeRunFolder(firstRun);
  await removeRunFolder(secondRun);
  assert.deepEqual((await readdir(sandbox)).sort(), ['cache', 'newheap-test-affected', 'victim-durations.txt', 'victim-solution.txt']);
  for (const [name, victim] of Object.entries(victims)) {
    assert.equal(await readFile(victim, 'utf8'), `original ${name}`);
  }
} finally {
  await rm(sandbox, { recursive: true, force: true });
}

console.log('Verified affected test selection: project graph, shared build files, front-end libraries, their headless test run, providers, their test processes, provider names in tests, the single build, the bounded slowest-first schedule, the hang detection, its reports, and run state kept in private run folders and the user\'s cache.');
