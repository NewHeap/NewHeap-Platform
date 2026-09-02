import { readFile, writeFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const toolDirectory = dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = resolve(toolDirectory, '..', '..');
export const releaseManifestPath = resolve(repositoryRoot, 'release', 'manifest.json');

const stableSemverPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const packageSemverPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

export async function readJson(path) {
  return JSON.parse(await readFile(path, 'utf8'));
}

export async function writeJson(path, value) {
  await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

export function parseArguments(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (!argument.startsWith('--')) throw new Error(`Unexpected argument: ${argument}`);
    const name = argument.slice(2);
    const next = argv[index + 1];
    if (!next || next.startsWith('--')) values[name] = true;
    else {
      values[name] = next;
      index += 1;
    }
  }
  return values;
}

export function isStableSemver(value) {
  return stableSemverPattern.test(value);
}

export function isPackageSemver(value) {
  return packageSemverPattern.test(value);
}

export function projectTargetFrameworks(source, projectName = 'project') {
  const match = source.match(/<(TargetFrameworks?)>([^<]+)<\/\1>/);
  if (!match) throw new Error(`${projectName}: missing TargetFramework or TargetFrameworks.`);
  const frameworks = match[2].split(';').map(value => value.trim()).filter(Boolean);
  if (frameworks.length === 0) throw new Error(`${projectName}: target framework list is empty.`);
  return frameworks;
}

export function missingTargetFrameworks(availableFrameworks, requiredFrameworks) {
  const available = new Set(availableFrameworks);
  return [...new Set(requiredFrameworks)].filter(framework => !available.has(framework));
}

export function bumpVersion(version, bump) {
  const match = version.match(stableSemverPattern);
  if (!match) throw new Error(`Cannot bump non-stable semantic version: ${version}`);
  let [, major, minor, patch] = match.map(Number);
  if (bump === 'major') [major, minor, patch] = [major + 1, 0, 0];
  else if (bump === 'minor') [minor, patch] = [minor + 1, 0];
  else if (bump === 'patch') patch += 1;
  else throw new Error(`Unsupported bump '${bump}'. Use major, minor or patch.`);
  return `${major}.${minor}.${patch}`;
}

export function isSingleVersionBump(previousVersion, version) {
  return ['patch', 'minor', 'major'].some(bump => bumpVersion(previousVersion, bump) === version);
}

export function assertPluginReleaseBaseline(manifestVersion, guidanceVersion, pluginVersion) {
  if (guidanceVersion === manifestVersion && pluginVersion === manifestVersion) return;
  throw new Error(
    `Plugin and guidance must both remain at released version ${manifestVersion} until Prepare release runs; `
    + `found plugin ${pluginVersion} and guidance ${guidanceVersion}.`
  );
}

export function resolveRepositoryPath(path) {
  const resolved = resolve(repositoryRoot, path);
  const display = relative(repositoryRoot, resolved);
  if (!display || display.startsWith('..') || display.includes(`..${process.platform === 'win32' ? '\\' : '/'}`)) {
    throw new Error(`Path escapes the repository: ${path}`);
  }
  return resolved;
}

export async function loadReleaseManifest() {
  const manifest = await readJson(releaseManifestPath);
  validateReleaseManifest(manifest);
  return manifest;
}

export function validateReleaseManifest(manifest) {
  const failures = [];
  if (manifest.schemaVersion !== 1) failures.push('Unsupported release manifest schema.');
  if (!/^[A-Za-z0-9-]+$/.test(manifest.packageOwner ?? '')) failures.push('packageOwner is invalid.');
  if (manifest.packageVisibility !== 'public') failures.push('All registry release packages must declare public visibility.');
  if (manifest.registries?.nuget !== 'https://api.nuget.org/v3/index.json') failures.push('NuGet releases must target nuget.org.');
  if (manifest.registries?.npm !== 'https://registry.npmjs.org/') failures.push('npm releases must target npmjs.org.');
  if (manifest.branches?.preview !== 'main' || manifest.branches?.stable !== 'main'
    || Object.keys(manifest.branches ?? {}).length !== 2) {
    failures.push('Preview and stable package releases must both use main as their only long-lived branch.');
  }
  if (!manifest.units || typeof manifest.units !== 'object') failures.push('Release manifest has no units.');
  const tags = new Set();
  const packageIds = new Set();
  for (const [id, unit] of Object.entries(manifest.units ?? {})) {
    if (!/^[a-z0-9]+(?:-[a-z0-9]+)+$/.test(id)) failures.push(`${id}: invalid release unit id.`);
    if (!['nuget', 'npm', 'plugin'].includes(unit.kind)) failures.push(`${id}: unsupported kind ${unit.kind}.`);
    if (!isStableSemver(unit.version ?? '')) failures.push(`${id}: version must be stable SemVer.`);
    const tag = `${unit.tagPrefix ?? ''}${unit.version ?? ''}`;
    if (!unit.tagPrefix?.endsWith('-v')) failures.push(`${id}: tagPrefix must end in -v.`);
    if (tags.has(tag)) failures.push(`${id}: duplicate release tag ${tag}.`);
    tags.add(tag);
    if (unit.kind === 'nuget') {
      if (!Array.isArray(unit.projects) || unit.projects.length === 0) failures.push(`${id}: NuGet unit has no projects.`);
      if (typeof unit.includeSymbols !== 'boolean') failures.push(`${id}: NuGet unit must declare includeSymbols.`);
      for (const project of unit.projects ?? []) {
        if (!project.packageId || !project.path) failures.push(`${id}: every NuGet project needs packageId and path.`);
        if (packageIds.has(project.packageId)) failures.push(`${id}: package ${project.packageId} occurs in multiple units.`);
        packageIds.add(project.packageId);
      }
    }
    if (unit.kind === 'npm' && (!unit.packageName || !unit.packageJson || !unit.workspace || !unit.buildScript || !unit.distDirectory)) {
      failures.push(`${id}: incomplete npm release configuration.`);
    }
    if (unit.kind === 'plugin' && (!unit.directory || !unit.packageName)) failures.push(`${id}: incomplete plugin release configuration.`);
  }
  if (failures.length > 0) throw new Error(failures.join('\n'));
}

export function releaseUnit(manifest, component) {
  const unit = manifest.units[component];
  if (!unit) throw new Error(`Unknown release component '${component}'. Choose: ${Object.keys(manifest.units).join(', ')}`);
  return unit;
}

export function releaseSelection(manifest, component) {
  if (component === 'all') {
    return Object.entries(manifest.units).map(([id, unit]) => ({ id, unit }));
  }
  return [{ id: component, unit: releaseUnit(manifest, component) }];
}

export function releasePackages(manifest, component) {
  return releaseSelection(manifest, component).flatMap(({ id, unit }) => {
    if (unit.kind === 'nuget') {
      return unit.projects.map(project => ({
        component: id,
        packageType: 'nuget',
        packageName: project.packageId,
        version: unit.version
      }));
    }
    if (unit.kind === 'npm') {
      return [{ component: id, packageType: 'npm', packageName: unit.packageName, version: unit.version }];
    }
    return [];
  });
}

export function releaseTag(unit, version = unit.version) {
  if (!isPackageSemver(version)) throw new Error(`Invalid package version: ${version}`);
  return `${unit.tagPrefix}${version}`;
}

export function addLocalNugetSource(configuration, source, name = 'newheap-release-local') {
  if (!/^[A-Za-z0-9._-]+$/.test(name)) throw new Error(`Invalid NuGet source name: ${name}`);
  if (configuration.includes(`<add key="${name}"`) || configuration.includes(`<packageSource key="${name}"`)) {
    throw new Error(`NuGet source '${name}' already exists.`);
  }
  const escapedSource = String(source)
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
  if (!configuration.includes('</packageSources>') || !configuration.includes('</packageSourceMapping>')) {
    throw new Error('NuGet configuration must contain packageSources and packageSourceMapping.');
  }
  return configuration
    .replace(
      '</packageSources>',
      `    <add key="${name}" value="${escapedSource}" />\n  </packageSources>`
    )
    .replace(
      '</packageSourceMapping>',
      `    <packageSource key="${name}">\n      <package pattern="NewHeap.*" />\n    </packageSource>\n  </packageSourceMapping>`
    );
}
