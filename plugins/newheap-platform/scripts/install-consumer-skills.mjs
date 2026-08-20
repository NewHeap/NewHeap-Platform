import { createHash } from 'node:crypto';
import { access, lstat, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const argumentsList = process.argv.slice(2);
const valueAfter = name => {
  const index = argumentsList.indexOf(name);
  return index >= 0 ? argumentsList[index + 1] : undefined;
};
const optionValues = new Set(['--consumer', '--target'].map(valueAfter).filter(Boolean));
const consumerArgument = valueAfter('--consumer')
  ?? argumentsList.find(value => !value.startsWith('-') && !optionValues.has(value));
const targetArgument = valueAfter('--target')?.toLowerCase() ?? 'codex';
const checkOnly = argumentsList.includes('--check');
const force = argumentsList.includes('--force');
const usage = 'Usage: node install-consumer-skills.mjs --consumer <consumer-root> [--target codex|claude|both] [--check] [--force]';
if (!consumerArgument) throw new Error(usage);
if (argumentsList.includes('--target') && !valueAfter('--target')) throw new Error(`--target requires codex, claude or both.\n${usage}`);
if (!['codex', 'claude', 'both'].includes(targetArgument)) throw new Error(`Unsupported target: ${targetArgument}. Expected codex, claude or both.`);

const targetDefinitions = new Map([
  ['codex', { directory: '.agents', relativeSkillsRoot: '.agents/skills', supportsLegacyLock: true }],
  ['claude', { directory: '.claude', relativeSkillsRoot: '.claude/skills', supportsLegacyLock: false }]
]);
const selectedTargets = targetArgument === 'both' ? ['codex', 'claude'] : [targetArgument];

async function walkFiles(directory) {
  const paths = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Refusing symbolic link in managed skill tree: ${path}`);
    if (entry.isDirectory()) paths.push(...await walkFiles(path));
    else paths.push(path);
  }
  return paths;
}

async function exists(path) {
  try { await access(path); return true; } catch { return false; }
}

async function readJsonIfPresent(path) {
  try { return JSON.parse(await readFile(path, 'utf8')); } catch { return undefined; }
}

const plugin = JSON.parse(await readFile(resolve(pluginRoot, '.codex-plugin', 'plugin.json'), 'utf8'));
const distribution = JSON.parse(await readFile(resolve(pluginRoot, 'distribution.json'), 'utf8'));
if (distribution.schemaVersion !== 3 || !Array.isArray(distribution.skills) || distribution.skills.length !== 1) {
  throw new Error('The plugin does not contain a valid grouped NewHeap consumer skill-suite manifest.');
}
const [suiteName] = distribution.skills;
const moduleNames = distribution.modules;
const moduleDirectories = distribution.moduleDirectories;
if (!/^newheap-[a-z0-9-]+$/.test(suiteName)
  || !Array.isArray(moduleNames) || moduleNames.length === 0
  || new Set(moduleNames).size !== moduleNames.length
  || moduleNames.some(name => !/^newheap-[a-z0-9-]+$/.test(name))
  || !moduleDirectories || Object.keys(moduleDirectories).length !== moduleNames.length
  || moduleNames.some(name => !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(moduleDirectories[name] ?? ''))
  || new Set(Object.values(moduleDirectories)).size !== moduleNames.length) {
  throw new Error('The plugin contains invalid or duplicate NewHeap module metadata.');
}

const sourceRoot = resolve(pluginRoot, 'skills', suiteName);
if (!(await stat(sourceRoot).catch(() => undefined))?.isDirectory()) {
  throw new Error(`Plugin skill is missing: ${suiteName}`);
}
const digest = value => createHash('sha256').update(value).digest('hex');
const sourceFiles = new Map();
for (const path of (await walkFiles(sourceRoot)).sort()) {
  const name = relative(sourceRoot, path).replaceAll('\\', '/');
  const content = await readFile(path);
  sourceFiles.set(name, { content, hash: digest(content) });
}

const consumerRoot = resolve(consumerArgument);
if (!(await stat(consumerRoot).catch(() => undefined))?.isDirectory()) {
  throw new Error(`Consumer root does not exist: ${consumerRoot}`);
}

function legacyFiles(lock) {
  if (!lock) return undefined;
  return Object.fromEntries(Object.entries(lock.files ?? {}).map(([name, hash]) => [
    `newheap-consumer-development/${name}`,
    hash
  ]));
}

async function prepareTarget(targetName) {
  const target = targetDefinitions.get(targetName);
  const destinationSkillsRoot = resolve(consumerRoot, target.directory, 'skills');
  const destinationRoot = resolve(destinationSkillsRoot, suiteName);
  const relativeDestinationRoot = `${target.relativeSkillsRoot}/${suiteName}`;
  if (relative(consumerRoot, destinationSkillsRoot).replaceAll('\\', '/') !== target.relativeSkillsRoot
    || relative(consumerRoot, destinationRoot).replaceAll('\\', '/') !== relativeDestinationRoot) {
    throw new Error(`Refusing unexpected ${targetName} skill destination: ${destinationRoot}`);
  }

  const flatDestinationRoots = new Map(moduleNames.map(name => [name, resolve(destinationSkillsRoot, name)]));
  const lockPath = resolve(destinationRoot, '.newheap-platform-install.json');
  const flatLockPath = resolve(destinationSkillsRoot, '.newheap-platform-install.json');
  const legacyLockPath = target.supportsLegacyLock
    ? resolve(destinationSkillsRoot, 'newheap-consumer-development', '.newheap-skill-install.json')
    : undefined;
  for (const path of [
    resolve(consumerRoot, target.directory),
    destinationSkillsRoot,
    destinationRoot,
    ...flatDestinationRoots.values(),
    lockPath,
    flatLockPath,
    legacyLockPath
  ].filter(Boolean)) {
    const info = await lstat(path).catch(() => undefined);
    if (info?.isSymbolicLink()) throw new Error(`Refusing symbolic link in ${targetName} skill destination: ${path}`);
  }

  function destinationFor(name) {
    if (!name || name.startsWith('../')) throw new Error(`Refusing unexpected managed skill path: ${name}`);
    const destination = resolve(destinationRoot, name);
    if (relative(destinationRoot, destination).replaceAll('\\', '/') !== name) {
      throw new Error(`Refusing unexpected managed skill destination: ${destination}`);
    }
    return destination;
  }

  async function groupedDestinationFiles() {
    const files = new Map();
    if (!(await exists(destinationRoot))) return files;
    for (const path of await walkFiles(destinationRoot)) {
      if (path === lockPath) continue;
      files.set(relative(destinationRoot, path).replaceAll('\\', '/'), path);
    }
    return files;
  }

  async function flatDestinationFiles() {
    const files = new Map();
    for (const [moduleName, moduleRoot] of flatDestinationRoots) {
      if (!(await exists(moduleRoot))) continue;
      for (const path of await walkFiles(moduleRoot)) {
        if (legacyLockPath && path === legacyLockPath) continue;
        files.set(`${moduleName}/${relative(moduleRoot, path).replaceAll('\\', '/')}`, path);
      }
    }
    return files;
  }

  const groupedLock = await readJsonIfPresent(lockPath);
  const flatLock = groupedLock ? undefined : await readJsonIfPresent(flatLockPath);
  const legacyLock = groupedLock || flatLock || !legacyLockPath ? undefined : await readJsonIfPresent(legacyLockPath);
  const layout = groupedLock ? 'grouped' : flatLock ? 'flat' : legacyLock ? 'legacy' : undefined;
  const previousFiles = groupedLock?.files ?? flatLock?.files ?? legacyFiles(legacyLock);
  const groupedFiles = await groupedDestinationFiles();
  const flatFiles = await flatDestinationFiles();
  const drift = [];

  if (layout !== 'grouped') drift.push(`installation layout ${layout ?? 'missing'} != grouped`);
  for (const [name, source] of sourceFiles) {
    const currentPath = groupedFiles.get(name);
    if (!currentPath) drift.push(`missing ${name}`);
    else if (digest(await readFile(currentPath)) !== source.hash) drift.push(`changed ${name}`);
  }
  for (const name of groupedFiles.keys()) if (!sourceFiles.has(name)) drift.push(`unexpected ${name}`);
  if (!groupedLock) drift.push(`missing ${relativeDestinationRoot}/.newheap-platform-install.json`);
  else {
    if (groupedLock.schemaVersion !== 4) drift.push(`install metadata schema ${groupedLock.schemaVersion ?? 'missing'} != 4`);
    if (groupedLock.target !== targetName) drift.push(`install target ${groupedLock.target ?? 'missing'} != ${targetName}`);
    if (groupedLock.repositoryTarget !== relativeDestinationRoot) drift.push('repository target is stale');
    if (groupedLock.skill !== suiteName) drift.push('installed skill name is stale');
    if (groupedLock.pluginVersion !== plugin.version) drift.push(`plugin version ${groupedLock.pluginVersion} != ${plugin.version}`);
    if (groupedLock.guidanceVersion !== distribution.guidanceVersion) drift.push('guidance version is stale');
    if (groupedLock.skillContentHash !== distribution.skillContentHash) drift.push('skill content is stale');
    if (JSON.stringify(groupedLock.modules) !== JSON.stringify(moduleNames)) drift.push('installed module list is stale');
    for (const [name, source] of sourceFiles) if (groupedLock.files?.[name] !== source.hash) drift.push(`lock hash is stale for ${name}`);
    for (const name of Object.keys(groupedLock.files ?? {})) if (!sourceFiles.has(name)) drift.push(`lock contains stale file ${name}`);
  }

  if (checkOnly && drift.length > 0) {
    throw new Error(`NewHeap Platform development skill is not synchronized for ${targetName}:\n- ${drift.join('\n- ')}`);
  }
  if (!checkOnly && !previousFiles && (groupedFiles.size > 0 || flatFiles.size > 0) && !force) {
    throw new Error(`An unmanaged NewHeap skill installation already exists for ${targetName}. Re-run with --force only if replacing it is intentional.`);
  }
  if (!checkOnly && previousFiles && !force) {
    const installedFiles = layout === 'grouped' ? groupedFiles : flatFiles;
    const locallyChanged = [];
    for (const [name, currentPath] of installedFiles) {
      if (!previousFiles[name] || digest(await readFile(currentPath)) !== previousFiles[name]) locallyChanged.push(name);
    }
    if (locallyChanged.length > 0) {
      throw new Error(`Refusing to overwrite locally changed installed ${targetName} skill files:\n- ${locallyChanged.join('\n- ')}\nUse --force only after reviewing them.`);
    }
  }

  return {
    targetName,
    destinationRoot,
    relativeDestinationRoot,
    flatDestinationRoots,
    destinationFor,
    lockPath,
    flatLockPath,
    legacyLockPath,
    layout
  };
}

async function applyTarget(state) {
  if (await exists(state.destinationRoot)) await rm(state.destinationRoot, { recursive: true, force: true });
  if (state.layout === 'flat' || state.layout === 'legacy' || force) {
    for (const moduleRoot of state.flatDestinationRoots.values()) {
      if (await exists(moduleRoot)) await rm(moduleRoot, { recursive: true, force: true });
    }
  }
  await rm(state.flatLockPath, { force: true });
  if (state.legacyLockPath) await rm(state.legacyLockPath, { force: true });

  for (const [name, source] of sourceFiles) {
    const destination = state.destinationFor(name);
    await mkdir(dirname(destination), { recursive: true });
    await writeFile(destination, source.content);
  }

  const lock = {
    schemaVersion: 4,
    target: state.targetName,
    repositoryTarget: state.relativeDestinationRoot,
    skill: suiteName,
    modules: moduleNames,
    pluginVersion: plugin.version,
    guidanceVersion: distribution.guidanceVersion,
    skillContentHash: distribution.skillContentHash,
    compatiblePackages: distribution.compatiblePackages,
    source: 'newheap-platform-plugin',
    files: Object.fromEntries([...sourceFiles].map(([name, value]) => [name, value.hash]))
  };
  await writeFile(state.lockPath, `${JSON.stringify(lock, null, 2)}\n`, 'utf8');
}

const preparedTargets = [];
for (const targetName of selectedTargets) preparedTargets.push(await prepareTarget(targetName));

if (checkOnly) {
  for (const state of preparedTargets) {
    console.log(`NewHeap Platform development skill is synchronized at ${state.destinationRoot} for ${state.targetName} (plugin ${plugin.version}).`);
  }
  process.exit(0);
}

for (const state of preparedTargets) {
  await applyTarget(state);
  console.log(`Installed NewHeap Platform development from plugin ${plugin.version} into ${state.destinationRoot} for ${state.targetName}. Commit that single managed directory, including .newheap-platform-install.json.`);
}
