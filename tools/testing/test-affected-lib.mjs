// Tests of the helpers behind `npm run test:affected`.
import assert from 'node:assert/strict';
import {
  affectedFrontEndLibraries,
  affectedTestProjects,
  angularTestArguments,
  isTestProject,
  needsAllProviders,
  ownerProject,
  parseProjectReferences,
  providerFilter,
  resolveRelative,
  testableFrontEndLibraries
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
assert.ok(!needsAllProviders(['src/Back-end/Libraries/NewHeap.Platform.Common/Extensions/NhConfigurationSecrets.cs']));
assert.equal(providerFilter(true), null);
assert.equal(providerFilter(false), 'DisplayName!~SqlServer');

console.log('Verified affected test selection: project graph, shared build files, front-end libraries, their headless test run and providers.');
