// Tests of the helpers behind `npm run test:affected`, and a check that the repository's tests keep
// to the naming its SQL Server filter relies on.
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  affectedFrontEndLibraries,
  affectedTestProjects,
  angularTestArguments,
  defaultMaxParallel,
  dotnetTestArguments,
  isTestProject,
  jobReport,
  needsAllProviders,
  normalizePath,
  outcomeSummary,
  ownerProject,
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
  testPlan,
  typicalTestSeconds
} from './affected-lib.mjs';

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
// Angular libraries run once, in headless Chrome, so a hidden browser window cannot fail them.
assert.deepEqual(angularTestArguments('nh-ai-chat'), ['ng', 'test', 'nh-ai-chat', '--watch=false', '--browsers=ChromeHeadless']);

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
// Processes that run side by side test one build; the Windows shell needs the filter quoted.
assert.deepEqual(
  dotnetTestArguments(chatTests, 'DisplayName~SqlServer', { noBuild: true }),
  ['test', chatTests, '-v', 'q', '-nologo', '--no-build', '--filter', 'DisplayName~SqlServer']);
assert.deepEqual(
  dotnetTestArguments(chatTests, 'DisplayName!~SqlServer', { windowsShell: true }),
  ['test', chatTests, '-v', 'q', '-nologo', '--filter', '"DisplayName!~SqlServer"']);

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
const plan = testPlan(planOptions);

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

console.log('Verified affected test selection: project graph, shared build files, front-end libraries, their headless test run, providers, their test processes, provider names in tests, the single build, the bounded slowest-first schedule and its reports.');
