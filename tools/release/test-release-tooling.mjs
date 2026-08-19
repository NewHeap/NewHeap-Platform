import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import {
  addLocalNugetSource,
  bumpVersion,
  loadReleaseManifest,
  missingTargetFrameworks,
  projectTargetFrameworks,
  releasePackages,
  releaseTag,
  repositoryRoot
} from './lib.mjs';

const manifest = await loadReleaseManifest();
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
  || allPackages.length !== manifest.units['nuget-common'].projects.length
    + manifest.units['nuget-caching'].projects.length
    + manifest.units['nuget-media'].projects.length
    + 2) {
  throw new Error('public release targets do not match the release manifest.');
}

console.log(`Exercised SemVer and dry-run packaging for ${Object.keys(manifest.units).length} release units.`);
