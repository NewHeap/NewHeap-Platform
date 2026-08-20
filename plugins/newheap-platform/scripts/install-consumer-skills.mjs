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

const plugin = JSON.parse(await readFile(resolve(pluginRoot, '.codex-plugin', 'plugin.json'), 'utf8'));
const distribution = JSON.parse(await readFile(resolve(pluginRoot, 'distribution.json'), 'utf8'));
if (distribution.schemaVersion !== 2 || !Array.isArray(distribution.skills) || distribution.skills.length === 0) {
  throw new Error('The plugin does not contain a valid NewHeap consumer skill-suite manifest.');
}
const skillNames = distribution.skills;
if (new Set(skillNames).size !== skillNames.length || skillNames.some(name => !/^newheap-[a-z0-9-]+$/.test(name))) {
  throw new Error('The plugin contains an invalid or duplicate consumer skill name.');
}

const consumerRoot = resolve(consumerArgument);
const consumerInfo = await stat(consumerRoot).catch(() => undefined);
if (!consumerInfo?.isDirectory()) throw new Error(`Consumer root does not exist: ${consumerRoot}`);

const digest = value => createHash('sha256').update(value).digest('hex');
const sourceFiles = new Map();
for (const skillName of skillNames) {
  const sourceRoot = resolve(pluginRoot, 'skills', skillName);
  const sourceInfo = await stat(sourceRoot).catch(() => undefined);
  if (!sourceInfo?.isDirectory()) throw new Error(`Plugin skill is missing: ${skillName}`);
  for (const path of (await walkFiles(sourceRoot)).sort()) {
    const name = `${skillName}/${relative(sourceRoot, path).replaceAll('\\', '/')}`;
    const content = await readFile(path);
    sourceFiles.set(name, { content, hash: digest(content) });
  }
}

async function readJsonIfPresent(path) {
  try { return JSON.parse(await readFile(path, 'utf8')); } catch { return undefined; }
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
  if (relative(consumerRoot, destinationSkillsRoot).replaceAll('\\', '/') !== target.relativeSkillsRoot) {
    throw new Error(`Refusing unexpected ${targetName} skills destination: ${destinationSkillsRoot}`);
  }

  const destinationRoots = new Map(skillNames.map(name => [name, resolve(destinationSkillsRoot, name)]));
  for (const path of [resolve(consumerRoot, target.directory), destinationSkillsRoot, ...destinationRoots.values()]) {
    const info = await lstat(path).catch(() => undefined);
    if (info?.isSymbolicLink()) throw new Error(`Refusing symbolic link in ${targetName} skill destination: ${path}`);
  }

  const lockPath = resolve(destinationSkillsRoot, '.newheap-platform-install.json');
  const legacyLockPath = target.supportsLegacyLock
    ? resolve(destinationSkillsRoot, 'newheap-consumer-development', '.newheap-skill-install.json')
    : undefined;
  for (const path of [lockPath, legacyLockPath].filter(Boolean)) {
    const info = await lstat(path).catch(() => undefined);
    if (info?.isSymbolicLink()) throw new Error(`Refusing symbolic link for NewHeap install metadata: ${path}`);
  }

  function destinationFor(name) {
    const separator = name.indexOf('/');
    const skillName = separator > 0 ? name.slice(0, separator) : '';
    const fileName = separator > 0 ? name.slice(separator + 1) : '';
    if (!destinationRoots.has(skillName) || !fileName || fileName.startsWith('../')) {
      throw new Error(`Refusing unexpected managed skill path: ${name}`);
    }
    const destination = resolve(destinationRoots.get(skillName), fileName);
    const expected = `${skillName}/${fileName}`;
    if (relative(destinationSkillsRoot, destination).replaceAll('\\', '/') !== expected) {
      throw new Error(`Refusing unexpected managed skill destination: ${destination}`);
    }
    return destination;
  }

  async function destinationFiles() {
    const files = new Map();
    for (const [skillName, destinationRoot] of destinationRoots) {
      if (!(await exists(destinationRoot))) continue;
      for (const path of await walkFiles(destinationRoot)) {
        if (legacyLockPath && path === legacyLockPath) continue;
        files.set(`${skillName}/${relative(destinationRoot, path).replaceAll('\\', '/')}`, path);
      }
    }
    return files;
  }

  const suiteLock = await readJsonIfPresent(lockPath);
  const legacyLock = suiteLock || !legacyLockPath ? undefined : await readJsonIfPresent(legacyLockPath);
  const previousFiles = suiteLock?.files ?? legacyFiles(legacyLock);
  const currentFiles = await destinationFiles();
  const drift = [];

  for (const [name, source] of sourceFiles) {
    const currentPath = currentFiles.get(name);
    if (!currentPath) drift.push(`missing ${name}`);
    else if (digest(await readFile(currentPath)) !== source.hash) drift.push(`changed ${name}`);
  }
  for (const name of currentFiles.keys()) if (!sourceFiles.has(name)) drift.push(`unexpected ${name}`);
  if (!suiteLock) drift.push(`missing ${target.relativeSkillsRoot}/.newheap-platform-install.json`);
  else {
    if (suiteLock.schemaVersion !== 3) drift.push(`install metadata schema ${suiteLock.schemaVersion ?? 'missing'} != 3`);
    if (suiteLock.target !== targetName) drift.push(`install target ${suiteLock.target ?? 'missing'} != ${targetName}`);
    if (suiteLock.repositoryTarget !== target.relativeSkillsRoot) drift.push('repository target is stale');
    if (suiteLock.pluginVersion !== plugin.version) drift.push(`plugin version ${suiteLock.pluginVersion} != ${plugin.version}`);
    if (suiteLock.guidanceVersion !== distribution.guidanceVersion) drift.push('guidance version is stale');
    if (suiteLock.skillContentHash !== distribution.skillContentHash) drift.push('skill content is stale');
    if (JSON.stringify(suiteLock.skills) !== JSON.stringify(skillNames)) drift.push('installed skill list is stale');
    for (const [name, source] of sourceFiles) if (suiteLock.files?.[name] !== source.hash) drift.push(`lock hash is stale for ${name}`);
    for (const name of Object.keys(suiteLock.files ?? {})) if (!sourceFiles.has(name)) drift.push(`lock contains stale file ${name}`);
  }

  if (checkOnly && drift.length > 0) {
    throw new Error(`NewHeap consumer skills are not synchronized for ${targetName}:\n- ${drift.join('\n- ')}`);
  }
  if (!checkOnly && !previousFiles && currentFiles.size > 0 && !force) {
    throw new Error(`One or more unmanaged NewHeap skill directories already exist for ${targetName}. Re-run with --force only if replacing those directories is intentional.`);
  }
  if (!checkOnly && previousFiles && !force) {
    const locallyChanged = [];
    for (const [name, currentPath] of currentFiles) {
      if (!previousFiles[name] || digest(await readFile(currentPath)) !== previousFiles[name]) locallyChanged.push(name);
    }
    if (locallyChanged.length > 0) {
      throw new Error(`Refusing to overwrite locally changed installed ${targetName} skill files:\n- ${locallyChanged.join('\n- ')}\nUse --force only after reviewing them.`);
    }
  }

  return {
    targetName,
    target,
    destinationSkillsRoot,
    destinationRoots,
    destinationFor,
    lockPath,
    legacyLockPath,
    previousFiles
  };
}

async function applyTarget(state) {
  if (force) {
    for (const destinationRoot of state.destinationRoots.values()) {
      if (await exists(destinationRoot)) await rm(destinationRoot, { recursive: true, force: true });
    }
  } else if (state.previousFiles) {
    for (const name of Object.keys(state.previousFiles)) {
      if (!sourceFiles.has(name)) await rm(state.destinationFor(name), { force: true });
    }
  }

  for (const [name, source] of sourceFiles) {
    const destination = state.destinationFor(name);
    await mkdir(dirname(destination), { recursive: true });
    await writeFile(destination, source.content);
  }

  if (state.legacyLockPath) await rm(state.legacyLockPath, { force: true });
  await mkdir(state.destinationSkillsRoot, { recursive: true });
  const lock = {
    schemaVersion: 3,
    target: state.targetName,
    repositoryTarget: state.target.relativeSkillsRoot,
    skills: skillNames,
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
    console.log(`NewHeap consumer skills are synchronized at ${state.destinationSkillsRoot} for ${state.targetName} (plugin ${plugin.version}).`);
  }
  process.exit(0);
}

for (const state of preparedTargets) {
  await applyTarget(state);
  console.log(`Installed ${skillNames.length} NewHeap consumer skills from plugin ${plugin.version} into ${state.destinationSkillsRoot} for ${state.targetName}. Commit the managed newheap-* skill directories and .newheap-platform-install.json.`);
}
