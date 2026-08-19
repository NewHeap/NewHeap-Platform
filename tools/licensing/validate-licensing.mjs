import { spawnSync } from 'node:child_process';
import { readFile, stat } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, '..', '..');
const failures = [];

async function read(path) {
  return readFile(resolve(repositoryRoot, path), 'utf8');
}

async function exists(path) {
  try {
    await stat(resolve(repositoryRoot, path));
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT') return false;
    throw error;
  }
}

function requireText(source, expected, location) {
  if (!source.includes(expected)) {
    failures.push(`${location} must contain ${JSON.stringify(expected)}.`);
  }
}

const requiredCommunityFiles = [
  'CONTRIBUTING.md',
  'LICENSE',
  'NOTICE',
  'README.md',
  'SECURITY.md',
  'SUPPORT.md',
  'THIRD-PARTY-NOTICES.md',
  'TRADEMARKS.md'
];

for (const path of requiredCommunityFiles) {
  if (!await exists(path)) failures.push(`${path} is required for the public repository.`);
}

const license = await read('LICENSE');
requireText(license, 'Apache License', 'LICENSE');
requireText(license, 'Version 2.0, January 2004', 'LICENSE');
requireText(license, '3. Grant of Patent License.', 'LICENSE');
requireText(license, '6. Trademarks.', 'LICENSE');

for (const path of [
  'plugins/newheap-platform/LICENSE',
  'skills/newheap-consumer-development/LICENSE',
  'plugins/newheap-platform/skills/newheap-consumer-development/LICENSE',
  'src/Front-end/projects/nh-common/LICENSE',
  'src/Front-end/projects/nh-toastr/LICENSE'
]) {
  if (await read(path) !== license) failures.push(`${path} must match the root Apache-2.0 license.`);
}

for (const path of [
  'package.json',
  'src/Front-end/projects/nh-common/package.json',
  'src/Front-end/projects/nh-toastr/package.json'
]) {
  const packageMetadata = JSON.parse(await read(path));
  if (packageMetadata.license !== 'Apache-2.0') {
    failures.push(`${path} must declare the SPDX license expression Apache-2.0.`);
  }
}

const buildProps = await read('src/Back-end/Directory.Build.props');
requireText(
  buildProps,
  '<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>',
  'src/Back-end/Directory.Build.props'
);
requireText(buildProps, '..\\..\\LICENSE', 'src/Back-end/Directory.Build.props');
requireText(buildProps, '..\\..\\NOTICE', 'src/Back-end/Directory.Build.props');
requireText(buildProps, '..\\..\\THIRD-PARTY-NOTICES.md', 'src/Back-end/Directory.Build.props');

const buildTargets = await read('src/Back-end/Directory.Build.targets');
requireText(buildTargets, '<PackageReadmeFile>README.md</PackageReadmeFile>', 'src/Back-end/Directory.Build.targets');
requireText(buildTargets, '..\\..\\README.md', 'src/Back-end/Directory.Build.targets');

const packageAssets = new Map([
  ['src/Front-end/projects/nh-common/ng-package.json', ['LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md']],
  ['src/Front-end/projects/nh-toastr/ng-package.json', ['LICENSE', 'NOTICE']]
]);

for (const [path, requiredAssets] of packageAssets) {
  const configuration = JSON.parse(await read(path));
  const stringAssets = new Set((configuration.assets ?? []).filter(asset => typeof asset === 'string'));
  for (const asset of requiredAssets) {
    if (!stringAssets.has(asset)) failures.push(`${path} must package ${asset}.`);
  }
}

for (const path of [
  'src/Back-end/Applications/WebAPI',
  'src/Back-end/Applications/WebAPI.PostgreSql.Migrations'
]) {
  if (await exists(path)) failures.push(`Legacy non-public application path must be absent: ${path}.`);
}

const notice = await read('THIRD-PARTY-NOTICES.md');
for (const attribution of ['Phil Booth', 'Salesforce.com, Inc.']) {
  requireText(notice, attribution, 'THIRD-PARTY-NOTICES.md');
}

for (const path of [
  'plugins/newheap-platform/NOTICE',
  'skills/newheap-consumer-development/NOTICE',
  'plugins/newheap-platform/skills/newheap-consumer-development/NOTICE'
]) {
  if (!await exists(path)) failures.push(`${path} is required in the standalone distribution.`);
}

const git = spawnSync(
  'git',
  ['ls-files', '--cached', '--others', '--exclude-standard', '-z'],
  { cwd: repositoryRoot, encoding: 'utf8' }
);
if (git.status !== 0) throw new Error(git.stderr || 'Unable to enumerate repository files.');

for (const path of git.stdout.split('\0').filter(Boolean)) {
  if (path.endsWith('package-lock.json')) continue;
  if (path === 'tools/licensing/validate-licensing.mjs') continue;
  let source;
  try {
    source = await read(path);
  } catch (error) {
    if (error?.code === 'ENOENT') continue;
    continue;
  }

  if (/NewHeap Proprietary Library License|SEE LICENSE IN LICENSE/i.test(source)) {
    failures.push(`${path} still contains proprietary NewHeap license metadata.`);
  }
}

if (failures.length > 0) {
  console.error('Licensing validation failed:');
  for (const failure of failures.sort()) console.error(`- ${failure}`);
  process.exit(1);
}

console.log('Apache-2.0 licensing and public community metadata are consistent.');
