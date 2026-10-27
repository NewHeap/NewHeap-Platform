import { readFile, writeFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const toolDirectory = dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = resolve(toolDirectory, '..', '..');
export const releaseManifestPath = resolve(repositoryRoot, 'release', 'manifest.json');

/**
 * Package locks in this repository that install npm release packages from their source folder
 * (`file:` dependencies). npm records the linked package's version in the lock, so a version bump
 * leaves the lock stale until the lock is refreshed with it.
 */
export const linkedPackageLocks = [
  'examples/SampleProjectManagement/src/Front-end/package-lock.json'
];

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

function releaseUnitPackageNames(unit) {
  return unit.kind === 'nuget' ? unit.projects.map(project => project.packageId) : [unit.packageName];
}

/**
 * Returns the packages that a release-note section heading names. A heading starts with one
 * or more package names ("A", "A and B" or "A, B and C"), optionally followed by
 * "(new package)" or "(new packages)" and by ": topic".
 */
export function releaseNoteHeadingPackages(heading) {
  const topicStart = heading.indexOf(': ');
  const subject = (topicStart >= 0 ? heading.slice(0, topicStart) : heading)
    .replace(/\s*\(new packages?\)$/i, '')
    .trim();
  return subject.split(/,\s*|\s+and\s+/).map(name => name.trim()).filter(Boolean);
}

function releaseNoteSections(source, path) {
  // ponytail: release notes use package-level ## headings and short tables, not arbitrary Markdown.
  const [heading, ...sections] = source.replaceAll('\r\n', '\n').trim().split(/^## /m);
  if (heading.trim() !== '# v-next') {
    throw new Error(`${path}: expected # v-next followed by package sections.`);
  }
  return sections;
}

/**
 * Checks that every section of v-next.md names known packages of exactly one release unit,
 * so release preparation can archive it with that unit's version. Returns the failures.
 */
export function validateReleaseNoteSections(manifest, source, path = 'docs/release-notes/v-next.md') {
  const packageUnits = new Map();
  for (const [id, unit] of Object.entries(manifest.units)) {
    for (const name of releaseUnitPackageNames(unit)) {
      packageUnits.set(name, id);
    }
  }

  const failures = [];
  for (const section of releaseNoteSections(source, path)) {
    const heading = section.split('\n', 1)[0].trim();
    const packages = releaseNoteHeadingPackages(heading);
    const unknown = packages.filter(name => !packageUnits.has(name));
    const units = new Set(packages.map(name => packageUnits.get(name)).filter(Boolean));
    if (packages.length === 0 || unknown.length > 0) {
      failures.push(`${path}: '## ${heading}' must start with release package names, optionally followed by ': topic'; unknown: ${unknown.join(', ') || 'none'}.`);
    } else if (units.size > 1) {
      failures.push(`${path}: '## ${heading}' mixes the release units ${[...units].join(' and ')}; write one section per unit.`);
    }
  }
  return failures;
}

export async function prepareReleaseNotes(releases, directory = resolve(repositoryRoot, 'docs', 'release-notes')) {
  const nextPath = resolve(directory, 'v-next.md');
  const source = await readFile(nextPath, 'utf8');
  const sections = releaseNoteSections(source, nextPath);

  const packageVersions = new Map();
  for (const { unit, version } of releases) {
    for (const name of releaseUnitPackageNames(unit)) {
      packageVersions.set(name, version);
    }
  }

  const pending = [];
  const archived = new Map();
  for (const section of sections) {
    const heading = section.split('\n', 1)[0].trim();
    const versions = new Set(releaseNoteHeadingPackages(heading).map(name => packageVersions.get(name)));
    const text = `## ${section.trim()}`;
    if (versions.size > 1) {
      throw new Error(`${nextPath}: '## ${heading}' mixes packages of different releases; write one section per unit.`);
    }
    const [version] = versions;
    if (!version) {
      pending.push(text);
      continue;
    }

    if (!archived.has(version)) {
      archived.set(version, []);
    }
    archived.get(version).push(text);
  }

  const writes = [];
  for (const [version, packageSections] of archived) {
    const path = resolve(directory, `v${version}.md`);
    let existing;
    try {
      existing = await readFile(path, 'utf8');
    } catch (error) {
      if (error.code !== 'ENOENT') {
        throw error;
      }
      existing = `# v${version}`;
    }
    if (existing.split(/\r?\n/, 1)[0].trim() !== `# v${version}`) {
      throw new Error(`${path}: expected # v${version}.`);
    }
    writes.push([path, `${[existing.replaceAll('\r\n', '\n').trimEnd(), ...packageSections].join('\n\n')}\n`]);
  }
  if (writes.length > 0) {
    writes.push([nextPath, `${['# v-next', ...pending].join('\n\n')}\n`]);
  }
  return writes;
}

/**
 * The npm release packages a lock may link, keyed by their package directory relative to the
 * lock's directory as npm writes it (forward slashes), with the version each package has.
 * `versions` maps a release unit id to the version to use; units it does not name keep the
 * manifest's version.
 */
export function linkedReleasePackages(manifest, lockPath, versions = new Map()) {
  const lockDirectory = dirname(lockPath);
  const packages = new Map();
  for (const [id, unit] of Object.entries(manifest.units)) {
    if (unit.kind !== 'npm') {
      continue;
    }

    const packageDirectory = dirname(resolve(repositoryRoot, unit.packageJson));
    const key = relative(lockDirectory, packageDirectory).replaceAll('\\', '/');
    packages.set(key, { name: unit.packageName, version: versions.get(id) ?? unit.version });
  }
  return packages;
}

/**
 * Compares the linked packages recorded in a parsed package lock with `packages` (from
 * `linkedReleasePackages`) and returns each difference as `{ key, name, locked, version }`.
 * Only packages the lock links (`"link": true`) count; other entries never change.
 */
export function linkedPackageLockDrift(lock, packages) {
  const drift = [];
  const linkedKeys = new Set(Object.values(lock?.packages ?? {})
    .filter(entry => entry?.link === true && typeof entry.resolved === 'string')
    .map(entry => entry.resolved));
  for (const key of linkedKeys) {
    const expected = packages.get(key);
    const entry = lock.packages[key];
    if (!expected || !entry || entry.name !== expected.name) {
      continue;
    }
    if (entry.version !== expected.version) {
      drift.push({ key, name: expected.name, locked: entry.version, version: expected.version });
    }
  }
  return drift;
}

/**
 * Returns a copy of a parsed package lock whose linked release packages carry the versions in
 * `packages`, and the differences it corrected. The lock keeps its key order, so it serializes as
 * npm writes it.
 */
export function syncLinkedPackageLock(lock, packages) {
  const drift = linkedPackageLockDrift(lock, packages);
  if (drift.length === 0) {
    return { lock, drift };
  }

  const updated = structuredClone(lock);
  for (const { key, version } of drift) {
    updated.packages[key].version = version;
  }
  return { lock: updated, drift };
}

/**
 * The GitHub deployment environments that the registry publishers run in. The nuget.org and npm
 * trusted-publisher policies require these names in the OIDC token's environment claim, and
 * GitHub only issues a token with that claim to a job its environment's deployment branch policy
 * and required reviewers allow. Neither name may contain production or staging, which remain
 * application-deployment names.
 */
export const publicationEnvironments = {
  preview: 'public-package-preview',
  stable: 'public-package-release'
};

/**
 * Splits a GitHub workflow into its top-level settings (`header`, the text before `jobs:`) and
 * its jobs, keyed by job id, each with the text below its id line. It relies on the two-space
 * indentation that this repository's workflows use.
 */
export function workflowJobs(workflow) {
  const lines = workflow.replaceAll('\r\n', '\n').split('\n');
  const jobsLine = lines.indexOf('jobs:');
  if (jobsLine < 0) {
    return { header: lines.join('\n'), jobs: new Map() };
  }

  const jobs = new Map();
  let current;
  for (const line of lines.slice(jobsLine + 1)) {
    if (/^[^\s#]/.test(line)) {
      break;
    }

    const jobStart = line.match(/^ {2}([A-Za-z0-9_-]+):\s*$/);
    if (jobStart) {
      current = [];
      jobs.set(jobStart[1], current);
    } else if (current) {
      current.push(line);
    }
  }

  return {
    header: lines.slice(0, jobsLine).join('\n'),
    jobs: new Map([...jobs].map(([id, body]) => [id, body.join('\n')]))
  };
}

/**
 * Returns the `permissions` block at the given indentation, `undefined` when there is none, or
 * the inline value (for example `write-all`).
 */
function permissionsAt(text, indentation) {
  const lines = text.split('\n');
  const start = lines.findIndex(line => new RegExp(`^ {${indentation}}permissions:`).test(line));
  if (start < 0) {
    return undefined;
  }

  const inline = lines[start].slice(indentation + 'permissions:'.length).trim();
  if (inline) {
    return inline;
  }

  const block = [];
  for (const line of lines.slice(start + 1)) {
    if (line.trim() && !line.startsWith(' '.repeat(indentation + 2))) {
      break;
    }
    block.push(line);
  }
  return block.join('\n');
}

function grantsIdToken(permissions) {
  return permissions === 'write-all' || /^\s+id-token:\s*write\s*$/m.test(permissions ?? '');
}

function jobEnvironment(job) {
  const inline = job.match(/^ {4}environment:[ \t]*(\S[^\n]*?)\s*$/m);
  if (inline) {
    return inline[1].replace(/^(['"])(.*)\1$/, '$2');
  }

  const named = job.match(/^ {4}environment:\s*\n(?: {6}[^\n]*\n)*? {6}name:[ \t]*(\S[^\n]*?)\s*$/m);
  return named?.[1].replace(/^(['"])(.*)\1$/, '$2');
}

/**
 * Checks that only `job` of a publisher workflow may request a GitHub OIDC token, and only inside
 * `environment`, so a registry policy that requires that environment cannot be satisfied by any
 * other job or by a run that the environment's protection rules reject. Returns the failures.
 */
export function publisherOidcFailures(workflow, { job, environment }) {
  const failures = [];
  const { header, jobs } = workflowJobs(workflow);
  const workflowPermissions = permissionsAt(header, 0);
  const reusable = /^ {2}workflow_call:/m.test(header);

  if (grantsIdToken(workflowPermissions)) {
    failures.push(`workflow-level permissions must not grant id-token: write; grant it only to the ${job} job.`);
  }
  if (reusable && workflowPermissions === undefined) {
    failures.push('a reusable publisher must declare workflow-level permissions, or its jobs inherit the caller\'s id-token: write.');
  }

  const publisher = jobs.get(job);
  if (publisher === undefined) {
    failures.push(`the ${job} job is missing.`);
  } else {
    const declared = jobEnvironment(publisher);
    if (declared === undefined) {
      failures.push(`the ${job} job must run in the ${environment} environment so its OIDC token carries the environment claim the registry policies require.`);
    } else if (declared !== environment) {
      failures.push(`the ${job} job runs in the ${declared} environment; the registry policies require ${environment}.`);
    }
    if (!grantsIdToken(permissionsAt(publisher, 4))) {
      failures.push(`the ${job} job must grant id-token: write in its own permissions.`);
    }
  }

  for (const [id, body] of jobs) {
    if (id === job) {
      continue;
    }

    const permissions = permissionsAt(body, 4);
    const inheritsCallerToken = permissions === undefined && workflowPermissions === undefined && reusable;
    if (grantsIdToken(permissions ?? workflowPermissions) || inheritsCallerToken) {
      failures.push(`the ${id} job may request an OIDC token; only the ${job} job may, inside the ${environment} environment.`);
    }
  }
  return failures;
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
