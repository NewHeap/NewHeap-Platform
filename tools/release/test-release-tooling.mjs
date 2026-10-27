import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import {
  addLocalNugetSource,
  assertPluginReleaseBaseline,
  bumpVersion,
  isSingleVersionBump,
  linkedPackageLockDrift,
  linkedPackageLocks,
  linkedReleasePackages,
  loadReleaseManifest,
  missingTargetFrameworks,
  projectTargetFrameworks,
  prepareReleaseNotes,
  publicationEnvironments,
  publisherOidcFailures,
  releaseNoteHeadingPackages,
  releasePackages,
  releaseSelection,
  releaseTag,
  repositoryRoot,
  syncLinkedPackageLock,
  validateReleaseNoteSections,
  workflowJobs
} from './lib.mjs';
import { validateNpmArtifactEntries, validateNugetArtifactEntries } from './validate-package-artifacts.mjs';

const validNuspec = Buffer.from(`<?xml version="1.0"?><package><metadata>
  <id>NewHeap.Platform.Example</id><version>1.2.3</version>
  <authors>NewHeap contributors</authors><description>Example package.</description>
  <tags>newheap example</tags><license type="expression">Apache-2.0</license>
  <icon>NH_logo.png</icon><readme>README.md</readme>
  <repository type="git" url="https://github.com/NewHeap/NewHeap-Platform" commit="0123456789abcdef0123456789abcdef01234567" />
</metadata></package>`);
assert.deepEqual(validateNugetArtifactEntries({
  fileName: 'NewHeap.Platform.Example.1.2.3.nupkg',
  packageId: 'NewHeap.Platform.Example',
  version: '1.2.3',
  symbolPackage: false,
  entries: [
    { name: 'NewHeap.Platform.Example.nuspec', data: validNuspec },
    { name: 'lib/net10.0/NewHeap.Platform.Example.dll', data: Buffer.from('clean binary') }
  ]
}), []);
assert.match(validateNugetArtifactEntries({
  fileName: 'NewHeap.Platform.Example.1.2.3.snupkg',
  packageId: 'NewHeap.Platform.Example',
  version: '1.2.3',
  symbolPackage: true,
  entries: [{ name: 'NewHeap.Platform.Example.nuspec', data: validNuspec }]
}).join('\n'), /contains no PDB files/);
assert.deepEqual(validateNugetArtifactEntries({
  fileName: 'NewHeap.Platform.Example.1.2.3.snupkg',
  packageId: 'NewHeap.Platform.Example',
  version: '1.2.3',
  symbolPackage: true,
  entries: [
    { name: 'NewHeap.Platform.Example.nuspec', data: validNuspec },
    { name: 'lib/net10.0/NewHeap.Platform.Example.pdb', data: Buffer.from('BSJB portable symbols') }
  ]
}), []);
assert.match(validateNugetArtifactEntries({
  fileName: 'NewHeap.Platform.Example.1.2.3.snupkg',
  packageId: 'NewHeap.Platform.Example',
  version: '1.2.3',
  symbolPackage: true,
  entries: [
    { name: 'NewHeap.Platform.Example.nuspec', data: validNuspec },
    { name: 'lib/net10.0/NewHeap.Platform.Example.pdb', data: Buffer.from('Microsoft C/C++ symbols') }
  ]
}).join('\n'), /not a managed Portable PDB/);
assert.match(validateNugetArtifactEntries({
  fileName: 'NewHeap.Platform.Example.1.2.3.nupkg',
  packageId: 'NewHeap.Platform.Example',
  version: '1.2.3',
  symbolPackage: false,
  entries: [
    { name: 'NewHeap.Platform.Example.nuspec', data: validNuspec },
    { name: 'lib/net10.0/NewHeap.Platform.Example.dll', data: Buffer.from('Sentry.ProjectDirectory C:\\Users\\maintainer\\source') }
  ]
}).join('\n'), /Sentry project-directory metadata/);
assert.match(validateNugetArtifactEntries({
  fileName: 'NewHeap.Platform.Example.1.2.3.nupkg',
  packageId: 'NewHeap.Platform.Example',
  version: '1.2.3',
  symbolPackage: false,
  entries: [
    { name: 'NewHeap.Platform.Example.nuspec', data: validNuspec },
    { name: 'lib/net10.0/NewHeap.Platform.Example.dll', data: Buffer.from('C:\\Users\\maintainer\\source\\private.cs') }
  ]
}).join('\n'), /Windows user-profile path/);
assert.deepEqual(validateNpmArtifactEntries({
  fileName: 'newheap-example-1.2.3.tgz',
  packageName: '@newheap/example',
  version: '1.2.3',
  entries: [{
    name: 'package/package.json',
    data: Buffer.from(JSON.stringify({ name: '@newheap/example', version: '1.2.3' }))
  }]
}), []);
assert.match(validateNpmArtifactEntries({
  fileName: 'newheap-example-1.2.3.tgz',
  packageName: '@newheap/example',
  version: '1.2.3',
  entries: [
    {
      name: 'package/package.json',
      data: Buffer.from(JSON.stringify({ name: '@newheap/example', version: '1.2.3' }))
    },
    { name: 'package/newheap-example-1.2.2.tgz', data: Buffer.from('stale package') }
  ]
}).join('\n'), /contains a nested package archive/);

// Only the publisher job may request an OIDC token, and only inside its protected environment, so
// a registry policy that requires the environment rejects every other job and every rejected run.
const stableEnvironment = publicationEnvironments.stable;
assert.notEqual(stableEnvironment, publicationEnvironments.preview, 'Stable and preview publication need separate registry policies.');
for (const environment of Object.values(publicationEnvironments)) {
  assert.doesNotMatch(environment, /production|staging/, 'Application-deployment environment names must not publish packages.');
}
const reusablePublisher = `name: Publish
on:
  workflow_call:
    inputs:
      release_sha:
        required: true
        type: string

permissions:
  contents: read

jobs:
  publish:
    runs-on: ubuntu-latest
    environment: ${stableEnvironment}
    permissions:
      contents: write
      id-token: write
    steps:
      - run: echo publish

  finalize:
    needs: publish
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      - run: echo finalize
`;
const stableJob = { job: 'publish', environment: stableEnvironment };
assert.deepEqual([...workflowJobs(reusablePublisher).jobs.keys()], ['publish', 'finalize']);
assert.deepEqual(publisherOidcFailures(reusablePublisher, stableJob), []);
assert.deepEqual(publisherOidcFailures(reusablePublisher.replaceAll('\n', '\r\n'), stableJob), []);
assert.deepEqual(publisherOidcFailures(reusablePublisher.replace(
  `    environment: ${stableEnvironment}\n`,
  `    environment:\n      name: '${stableEnvironment}'\n`), stableJob), [], 'The environment may use the name form.');
assert.match(
  publisherOidcFailures(reusablePublisher.replace(`    environment: ${stableEnvironment}\n`, ''), stableJob).join('\n'),
  /publish job must run in the public-package-release environment/);
assert.match(
  publisherOidcFailures(reusablePublisher.replace(`environment: ${stableEnvironment}`, `environment: ${publicationEnvironments.preview}`), stableJob).join('\n'),
  /runs in the public-package-preview environment; the registry policies require public-package-release/);
assert.match(
  publisherOidcFailures(reusablePublisher.replace(`environment: ${stableEnvironment}`, 'environment: ${{ inputs.environment }}'), stableJob).join('\n'),
  /runs in the \$\{\{ inputs\.environment \}\} environment/,
  'A computed environment name cannot prove the environment that the registry policies require.');
assert.match(
  publisherOidcFailures(reusablePublisher.replace('permissions:\n  contents: read\n', 'permissions:\n  contents: write\n  id-token: write\n'), stableJob).join('\n'),
  /workflow-level permissions must not grant id-token: write/);
assert.match(
  publisherOidcFailures(reusablePublisher.replace('permissions:\n  contents: read\n\n', ''), stableJob).join('\n'),
  /must declare workflow-level permissions/);
assert.match(
  publisherOidcFailures(reusablePublisher.replace('      contents: write\n      id-token: write\n', '      contents: write\n'), stableJob).join('\n'),
  /publish job must grant id-token: write in its own permissions/);
assert.match(
  publisherOidcFailures(reusablePublisher.replace('    permissions:\n      contents: write\n    steps:\n      - run: echo finalize', '    permissions: write-all\n    steps:\n      - run: echo finalize'), stableJob).join('\n'),
  /finalize job may request an OIDC token/);
assert.match(publisherOidcFailures(reusablePublisher, { job: 'release', environment: stableEnvironment }).join('\n'), /release job is missing/);
for (const [path, environment] of [
  ['.github/workflows/publish-release.yml', publicationEnvironments.stable],
  ['.github/workflows/publish-preview.yml', publicationEnvironments.preview]
]) {
  assert.deepEqual(
    publisherOidcFailures(await readFile(resolve(repositoryRoot, path), 'utf8'), { job: 'publish', environment }),
    [],
    `${path} must request OIDC tokens only from its publish job in ${environment}.`);
}

const manifest = await loadReleaseManifest();

/**
 * A package lock as npm writes it for the sample front end: its own package, the linked release
 * packages from source and a registry package with the same name pattern.
 */
function linkedLockFixture(aiChatVersion, commonVersion) {
  return {
    name: 'sample-front-end',
    version: '0.1.0',
    lockfileVersion: 3,
    requires: true,
    packages: {
      '': {
        name: 'sample-front-end',
        version: '0.1.0',
        dependencies: {
          '@newheap/platform-ai-chat': 'file:../../../../src/Front-end/projects/nh-ai-chat',
          '@newheap/platform-common': 'file:../../../../src/Front-end/projects/nh-common'
        }
      },
      '../../../../src/Front-end/projects/nh-ai-chat': { name: '@newheap/platform-ai-chat', version: aiChatVersion, license: 'Apache-2.0' },
      '../../../../src/Front-end/projects/nh-common': { name: '@newheap/platform-common', version: commonVersion, license: 'Apache-2.0' },
      'node_modules/@newheap/platform-ai-chat': { resolved: '../../../../src/Front-end/projects/nh-ai-chat', link: true },
      'node_modules/@newheap/platform-common': { resolved: '../../../../src/Front-end/projects/nh-common', link: true },
      'node_modules/@newheap/registry-only': { version: '0.0.1', resolved: 'https://registry.npmjs.org/@newheap/registry-only/-/registry-only-0.0.1.tgz' }
    }
  };
}

// The sample's lock records the versions of the release packages it links from source.
assert.ok(linkedPackageLocks.includes('examples/SampleProjectManagement/src/Front-end/package-lock.json'));
const sampleLockPath = resolve(repositoryRoot, linkedPackageLocks[0]);
const linkedPackages = linkedReleasePackages(manifest, sampleLockPath);
assert.deepEqual(linkedPackages.get('../../../../src/Front-end/projects/nh-ai-chat'),
  { name: '@newheap/platform-ai-chat', version: manifest.units['npm-ai-chat'].version });
assert.deepEqual(linkedReleasePackages(manifest, sampleLockPath, new Map([['npm-ai-chat', '9.9.9']]))
  .get('../../../../src/Front-end/projects/nh-ai-chat').version, '9.9.9');
const currentLock = linkedLockFixture(manifest.units['npm-ai-chat'].version, manifest.units['npm-platform-common'].version);
assert.deepEqual(linkedPackageLockDrift(currentLock, linkedPackages), []);
assert.equal(syncLinkedPackageLock(currentLock, linkedPackages).lock, currentLock, 'A lock without drift stays the same object.');
const staleLock = linkedLockFixture('0.8.1', '1.4.0');
assert.deepEqual(linkedPackageLockDrift(staleLock, linkedPackages), [
  { key: '../../../../src/Front-end/projects/nh-ai-chat', name: '@newheap/platform-ai-chat', locked: '0.8.1', version: manifest.units['npm-ai-chat'].version },
  { key: '../../../../src/Front-end/projects/nh-common', name: '@newheap/platform-common', locked: '1.4.0', version: manifest.units['npm-platform-common'].version }
]);
const synced = syncLinkedPackageLock(staleLock, linkedPackages);
assert.equal(synced.drift.length, 2);
assert.deepEqual(synced.lock, currentLock, 'Syncing corrects exactly the linked versions.');
assert.equal(staleLock.packages['../../../../src/Front-end/projects/nh-ai-chat'].version, '0.8.1', 'Syncing must not mutate its input.');
const renamedLock = linkedLockFixture('0.8.1', '1.4.0');
renamedLock.packages['../../../../src/Front-end/projects/nh-ai-chat'].name = '@newheap/other';
delete renamedLock.packages['node_modules/@newheap/platform-common'];
assert.deepEqual(linkedPackageLockDrift(renamedLock, linkedPackages), [],
  'Only linked entries of the same release package count.');
assert.deepEqual(
  linkedPackageLockDrift(JSON.parse(await readFile(sampleLockPath, 'utf8')), linkedPackages),
  [],
  'The sample lock must record the current versions of the packages it links.');

const notesDirectory = await mkdtemp(resolve(tmpdir(), 'newheap-release-notes-'));
try {
  const nextPath = resolve(notesDirectory, 'v-next.md');
  const common = '## NewHeap.Platform.AspNet.Common\n\n| Breaking change | Required action |\n|---|---|\n| Storage moved | Add the provider package. |';
  const media = '## NewHeap.Platform.Media.Core\n\n| Breaking change | Required action |\n|---|---|\n| Method renamed | Use the new name. |';
  const npm = '## @newheap/platform-common\n\n| Breaking change | Required action |\n|---|---|\n| Option removed | Remove the option. |';
  const plugin = '## newheap-platform\n\n| Breaking change | Required action |\n|---|---|\n| Skill renamed | Use the new name. |';
  const releases = releaseSelection(manifest, 'all').map(({ unit }) => ({ unit, version: bumpVersion(unit.version, 'patch') }));
  const commonRelease = releases.find(release => release.unit === manifest.units['nuget-common']);
  const archivePath = resolve(notesDirectory, `v${commonRelease.version}.md`);
  const original = ['# v-next', common, media, npm, plugin].join('\n\n') + '\n';
  await writeFile(nextPath, original.replaceAll('\n', '\r\n'));

  const commonWrites = await prepareReleaseNotes([commonRelease], notesDirectory);
  assert.equal(commonWrites.length, 2);
  assert.equal(await readFile(nextPath, 'utf8'), original.replaceAll('\n', '\r\n'), 'Planning must not mutate release notes.');
  for (const [path, text] of commonWrites) {
    await writeFile(path, text);
  }
  assert.equal(await readFile(archivePath, 'utf8'), `# v${commonRelease.version}\n\n${common}\n`);
  assert.equal(await readFile(nextPath, 'utf8'), ['# v-next', media, npm, plugin].join('\n\n') + '\n');
  assert.deepEqual(await prepareReleaseNotes([commonRelease], notesDirectory), [], 'Repeating a selection must not duplicate archived notes.');

  // Two independent units can publish the same version without replacing each other's notes.
  const npmRelease = { unit: manifest.units['npm-platform-common'], version: commonRelease.version };
  for (const [path, text] of await prepareReleaseNotes([npmRelease], notesDirectory)) {
    await writeFile(path, text);
  }
  assert.equal(await readFile(archivePath, 'utf8'), `# v${commonRelease.version}\n\n${common}\n\n${npm}\n`);
  assert.equal(await readFile(nextPath, 'utf8'), ['# v-next', media, plugin].join('\n\n') + '\n');

  await writeFile(nextPath, original);
  await rm(archivePath);
  const allReleases = releases.map(release => release.unit === npmRelease.unit ? npmRelease : release);
  const allWrites = new Map(await prepareReleaseNotes(allReleases, notesDirectory));
  assert.equal(allWrites.get(nextPath), '# v-next\n');
  for (const [unitId, section] of [['nuget-media', media], ['newheap-platform-plugin', plugin]]) {
    const release = allReleases.find(item => item.unit === manifest.units[unitId]);
    assert.equal(allWrites.get(resolve(notesDirectory, `v${release.version}.md`)), `# v${release.version}\n\n${section}\n`);
  }
  assert.equal(allWrites.get(archivePath), `# v${commonRelease.version}\n\n${common}\n\n${npm}\n`, 'An all-unit release must group matching versions into one archive.');

  // Headings may add a topic, list packages of one unit and mark new packages.
  assert.deepEqual(releaseNoteHeadingPackages('NewHeap.Platform.AspNet.Common: two-factor authentication'), ['NewHeap.Platform.AspNet.Common']);
  assert.deepEqual(releaseNoteHeadingPackages('@newheap/platform-ai-chat (new package)'), ['@newheap/platform-ai-chat']);
  assert.deepEqual(
    releaseNoteHeadingPackages('NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.AspNet (new packages): assistant'),
    ['NewHeap.Platform.AI.Chat', 'NewHeap.Platform.AI.Chat.SqlServer', 'NewHeap.Platform.AI.Chat.AspNet']);

  const topicCommon = '## NewHeap.Platform.AspNet.Common and NewHeap.Platform.AI.Common: session validation\n\n| Adoption note | Required action |\n|---|---|\n| Validator moved | No action. |';
  const mixedUnits = '## NewHeap.Platform.AspNet.Common and @newheap/platform-common: two-factor authentication\n\nBoth packages changed.';
  const unknownPackage = '## Authentication: two-factor authentication\n\nNo package named.';
  assert.deepEqual(validateReleaseNoteSections(manifest, ['# v-next', topicCommon, media, npm, plugin].join('\n\n')), []);
  const failures = validateReleaseNoteSections(manifest, ['# v-next', mixedUnits, unknownPackage].join('\n\n'));
  assert.equal(failures.length, 2);
  assert.match(failures[0], /mixes the release units nuget-common and npm-platform-common/);
  assert.match(failures[1], /unknown: Authentication/);
  assert.deepEqual(
    validateReleaseNoteSections(manifest, await readFile(resolve(repositoryRoot, 'docs/release-notes/v-next.md'), 'utf8')),
    [],
    'The pending release notes must be archivable.');

  // A topic heading is archived with the unit's version instead of staying in v-next.md.
  await writeFile(nextPath, ['# v-next', topicCommon, media].join('\n\n') + '\n');
  await rm(archivePath, { force: true });
  const topicWrites = new Map(await prepareReleaseNotes([commonRelease], notesDirectory));
  assert.equal(topicWrites.get(archivePath), `# v${commonRelease.version}\n\n${topicCommon}\n`);
  assert.equal(topicWrites.get(nextPath), ['# v-next', media].join('\n\n') + '\n');
  await writeFile(nextPath, ['# v-next', mixedUnits].join('\n\n') + '\n');
  await assert.rejects(prepareReleaseNotes([commonRelease], notesDirectory), /mixes packages of different releases/);

  await writeFile(nextPath, '# v-next\n');
  assert.deepEqual(await prepareReleaseNotes(allReleases, notesDirectory), []);
  await writeFile(nextPath, 'Unassigned notes\n');
  await assert.rejects(prepareReleaseNotes(allReleases, notesDirectory), /expected # v-next/);
  await writeFile(nextPath, original);
  await writeFile(archivePath, '# Incorrect version\n');
  await assert.rejects(prepareReleaseNotes(allReleases, notesDirectory), /expected # v/);
  assert.equal(await readFile(nextPath, 'utf8'), original, 'Invalid archives must leave pending notes intact.');

  // Exercise the actual preparation entry point without changing repository versions.
  const fixture = resolve(notesDirectory, 'repository');
  for (const directory of ['tools/release', 'tools/guidance', 'release', 'src/Back-end', 'docs/release-notes']) {
    await mkdir(resolve(fixture, directory), { recursive: true });
  }
  for (const script of ['prepare-release.mjs', 'lib.mjs']) {
    await copyFile(resolve(repositoryRoot, 'tools/release', script), resolve(fixture, 'tools/release', script));
  }
  for (const script of ['snapshot-public-api.mjs', 'generate-guidance.mjs']) {
    await writeFile(resolve(fixture, 'tools/guidance', script), '// Guidance generation is outside this release-note regression.\n');
  }
  await writeFile(resolve(fixture, 'release/manifest.json'), JSON.stringify(manifest));
  await copyFile(resolve(repositoryRoot, 'src/Back-end/Directory.Packages.props'), resolve(fixture, 'src/Back-end/Directory.Packages.props'));
  const fixtureNext = resolve(fixture, 'docs/release-notes/v-next.md');
  await writeFile(fixtureNext, original);
  for (const dryRun of [true, false]) {
    const prepared = spawnSync(process.execPath, [resolve(fixture, 'tools/release/prepare-release.mjs'),
      '--component', 'nuget-common', '--bump', 'patch', ...(dryRun ? ['--dry-run'] : [])], { cwd: fixture, encoding: 'utf8' });
    assert.equal(prepared.status, 0, prepared.stderr);
    assert.equal(await readFile(fixtureNext, 'utf8'), dryRun ? original : ['# v-next', media, npm, plugin].join('\n\n') + '\n');
  }
  assert.equal(await readFile(resolve(fixture, 'docs/release-notes', `v${commonRelease.version}.md`), 'utf8'), `# v${commonRelease.version}\n\n${common}\n`);

  // Preparing an npm release refreshes the version that a sample's package lock records for the
  // package it links from source, and leaves the other linked packages alone.
  const aiChat = manifest.units['npm-ai-chat'];
  const platformCommon = manifest.units['npm-platform-common'];
  const fixtureLockPath = resolve(fixture, linkedPackageLocks[0]);
  await mkdir(resolve(fixture, aiChat.packageJson, '..'), { recursive: true });
  await writeFile(resolve(fixture, aiChat.packageJson), `${JSON.stringify({ name: aiChat.packageName, version: aiChat.version }, null, 2)}\n`);
  await mkdir(resolve(fixtureLockPath, '..'), { recursive: true });
  const fixtureLock = linkedLockFixture(aiChat.version, platformCommon.version);
  await writeFile(fixtureLockPath, `${JSON.stringify(fixtureLock, null, 2)}\n`);
  const preparedNpm = spawnSync(process.execPath, [resolve(fixture, 'tools/release/prepare-release.mjs'),
    '--component', 'npm-ai-chat', '--bump', 'minor'], { cwd: fixture, encoding: 'utf8' });
  assert.equal(preparedNpm.status, 0, preparedNpm.stderr);
  const refreshedLock = JSON.parse(await readFile(fixtureLockPath, 'utf8'));
  assert.equal(refreshedLock.packages['../../../../src/Front-end/projects/nh-ai-chat'].version, bumpVersion(aiChat.version, 'minor'),
    'Release preparation must refresh the linked package version in the sample lock.');
  assert.equal(refreshedLock.packages['../../../../src/Front-end/projects/nh-common'].version, platformCommon.version,
    'Release preparation must not change linked packages it does not release.');
  assert.deepEqual(Object.keys(refreshedLock.packages), Object.keys(fixtureLock.packages), 'The lock must keep npm\'s key order.');
} finally {
  await rm(notesDirectory, { recursive: true, force: true });
}

if (manifest.packageVisibility !== 'public'
  || manifest.registries.npm !== 'https://registry.npmjs.org/'
  || manifest.registries.nuget !== 'https://api.nuget.org/v3/index.json') {
  throw new Error('Release manifest does not target the public npm and NuGet registries.');
}
const expected = new Map([
  ['0.0.0:patch', '0.0.1'],
  ['0.9.9:minor', '0.10.0'],
  ['1.9.9:major', '2.0.0']
]);
for (const [input, output] of expected) {
  const [version, bump] = input.split(':');
  if (bumpVersion(version, bump) !== output) throw new Error(`Unexpected bump result for ${input}.`);
}
assert.equal(isSingleVersionBump('1.11.6', '1.11.7'), true);
assert.equal(isSingleVersionBump('1.11.6', '1.12.0'), true);
assert.equal(isSingleVersionBump('1.11.6', '2.0.0'), true);
assert.equal(isSingleVersionBump('1.11.6', '1.11.8'), false);
assert.equal(isSingleVersionBump('1.11.6', '1.11.6'), false);
assert.doesNotThrow(() => assertPluginReleaseBaseline('1.12.0', '1.12.0', '1.12.0'));
assert.throws(
  () => assertPluginReleaseBaseline('1.12.0', '1.12.1', '1.12.1'),
  /must both remain at released version 1\.12\.0 until Prepare release runs/
);
assert.throws(
  () => assertPluginReleaseBaseline('1.12.0', '1.12.0', '1.12.1'),
  /found plugin 1\.12\.1 and guidance 1\.12\.0/
);

if (projectTargetFrameworks('<TargetFramework>net10.0</TargetFramework>').join(';') !== 'net10.0'
  || projectTargetFrameworks('<TargetFrameworks>net9.0;net10.0</TargetFrameworks>').join(';') !== 'net9.0;net10.0'
  || missingTargetFrameworks(['net10.0'], ['net9.0', 'net10.0']).join(';') !== 'net9.0') {
  throw new Error('Project target-framework compatibility validation failed.');
}

for (const [component, unit] of Object.entries(manifest.units)) {
  if (releaseTag(unit) !== `${unit.tagPrefix}${unit.version}`) throw new Error(`${component}: tag mismatch.`);
  for (const bump of ['patch', 'minor', 'major']) {
    const prepare = spawnSync(process.execPath, [
      resolve(repositoryRoot, 'tools', 'release', 'prepare-release.mjs'),
      '--component', component,
      '--bump', bump,
      '--dry-run'
    ], { cwd: repositoryRoot, encoding: 'utf8' });
    if (prepare.status !== 0) throw new Error(prepare.stderr || `${component}: prepare dry-run failed.`);
    const prepared = JSON.parse(prepare.stdout.trim());
    if (prepared.previousVersion !== unit.version) throw new Error(`${component}: dry-run read the wrong version.`);
  }
  const pack = spawnSync(process.execPath, [
    resolve(repositoryRoot, 'tools', 'release', 'package-release.mjs'),
    '--component', component,
    '--dry-run'
  ], { cwd: repositoryRoot, encoding: 'utf8' });
  if (pack.status !== 0) throw new Error(pack.stderr || `${component}: package dry-run failed.`);
  const packaged = JSON.parse(pack.stdout.trim());
  if (packaged.commands.length === 0) throw new Error(`${component}: package dry-run produced no commands.`);
  if (unit.kind === 'nuget') {
    const packCommand = packaged.commands.find(command => command.command === 'dotnet' && command.arguments.includes('pack'));
    if (!packCommand) throw new Error(`${component}: package dry-run has no dotnet pack command.`);
    const includesSymbols = packCommand.arguments.includes('--include-symbols');
    const usesSnupkg = packCommand.arguments.some(argument => argument === '/p:SymbolPackageFormat=snupkg');
    if (includesSymbols !== unit.includeSymbols || usesSnupkg !== unit.includeSymbols) {
      throw new Error(`${component}: symbol-package behavior differs from the release manifest.`);
    }
  }
  if (unit.kind === 'plugin') {
    const archiveCommand = packaged.commands.find(command => command.command === 'tar');
    const expectedPluginParent = resolve(repositoryRoot, 'plugins');
    if (!archiveCommand
      || !archiveCommand.arguments.includes(expectedPluginParent)
      || !archiveCommand.arguments.includes('newheap-platform')) {
      throw new Error(`${component}: plugin packaging must archive the complete newheap-platform directory, including its skills.`);
    }
  }
}

for (const bump of ['patch', 'minor', 'major']) {
  const prepareAll = spawnSync(process.execPath, [
    resolve(repositoryRoot, 'tools', 'release', 'prepare-release.mjs'),
    '--component', 'all',
    '--bump', bump,
    '--dry-run'
  ], { cwd: repositoryRoot, encoding: 'utf8' });
  if (prepareAll.status !== 0) throw new Error(prepareAll.stderr || `all: ${bump} prepare dry-run failed.`);
  const preparedAll = JSON.parse(prepareAll.stdout.trim());
  if (preparedAll.component !== 'all' || preparedAll.releases.length !== Object.keys(manifest.units).length) {
    throw new Error(`all: ${bump} prepare dry-run did not select every release unit.`);
  }
  for (const release of preparedAll.releases) {
    const expectedVersion = bumpVersion(manifest.units[release.component].version, bump);
    if (release.version !== expectedVersion) throw new Error(`all: ${release.component} has unexpected ${bump} version.`);
  }
}

const releaseInfoAll = spawnSync(process.execPath, [
  resolve(repositoryRoot, 'tools', 'release', 'release-info.mjs'),
  '--component', 'all'
], { cwd: repositoryRoot, encoding: 'utf8' });
if (releaseInfoAll.status !== 0) throw new Error(releaseInfoAll.stderr || 'all: release info failed.');
const allInfo = JSON.parse(releaseInfoAll.stdout.trim());
if (allInfo.releases.length !== Object.keys(manifest.units).length || !allInfo.summary.includes('nuget-common=')) {
  throw new Error('all: release info does not describe every release unit.');
}
if (allInfo.components.join('\n') !== Object.keys(manifest.units).join('\n')
  || allInfo.nugetComponents.some(component => manifest.units[component].kind !== 'nuget')
  || allInfo.npmComponents.some(component => manifest.units[component].kind !== 'npm')) {
  throw new Error('all: workflow component selections have drifted from the release manifest.');
}

const nugetConfiguration = `
<configuration>
  <packageSources>
  </packageSources>
  <packageSourceMapping>
  </packageSourceMapping>
</configuration>`;
const mappedConfiguration = addLocalNugetSource(nugetConfiguration, '/tmp/common&packages');
if (!mappedConfiguration.includes('key="newheap-release-local" value="/tmp/common&amp;packages"')
  || !mappedConfiguration.includes('<package pattern="NewHeap.*" />')) {
  throw new Error('release-all: local Common artifacts are not mapped as a NewHeap NuGet source.');
}

const allPackages = releasePackages(manifest, 'all');
if (!allPackages.some(item => item.packageType === 'npm' && item.packageName === '@newheap/platform-common')
  || allPackages.filter(item => item.packageType === 'npm').some(item => !item.packageName.startsWith('@newheap/'))
  || allPackages.some(item => item.version !== manifest.units[item.component].version)
  || allPackages.length !== Object.values(manifest.units).reduce((count, unit) =>
    count + (unit.kind === 'nuget' ? unit.projects.length : unit.kind === 'npm' ? 1 : 0), 0)) {
  throw new Error('public release targets do not match the release manifest.');
}

console.log(`Exercised release-note rollover, SemVer and dry-run packaging for ${Object.keys(manifest.units).length} release units.`);
