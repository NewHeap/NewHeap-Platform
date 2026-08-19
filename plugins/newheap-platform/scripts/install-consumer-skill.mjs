import { createHash } from 'node:crypto';
import { access, lstat, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const sourceSkillRoot = resolve(pluginRoot, 'skills', 'newheap-consumer-development');
const argumentsList = process.argv.slice(2);
const valueAfter = name => {
  const index = argumentsList.indexOf(name);
  return index >= 0 ? argumentsList[index + 1] : undefined;
};
const consumerArgument = valueAfter('--consumer') ?? argumentsList.find(value => !value.startsWith('-'));
const checkOnly = argumentsList.includes('--check');
const force = argumentsList.includes('--force');
if (!consumerArgument) {
  throw new Error('Usage: node install-consumer-skill.mjs --consumer <consumer-root> [--check] [--force]');
}

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

const consumerRoot = resolve(consumerArgument);
const consumerInfo = await stat(consumerRoot).catch(() => undefined);
if (!consumerInfo?.isDirectory()) throw new Error(`Consumer root does not exist: ${consumerRoot}`);
const destinationRoot = resolve(consumerRoot, '.agents', 'skills', 'newheap-consumer-development');
const expectedRelativeTarget = '.agents/skills/newheap-consumer-development';
if (relative(consumerRoot, destinationRoot).replaceAll('\\', '/') !== expectedRelativeTarget) {
  throw new Error(`Refusing unexpected skill destination: ${destinationRoot}`);
}
for (const path of [resolve(consumerRoot, '.agents'), resolve(consumerRoot, '.agents', 'skills'), destinationRoot]) {
  const info = await lstat(path).catch(() => undefined);
  if (info?.isSymbolicLink()) throw new Error(`Refusing symbolic link in consumer skill destination: ${path}`);
}

const lockPath = resolve(destinationRoot, '.newheap-skill-install.json');
const plugin = JSON.parse(await readFile(resolve(pluginRoot, '.codex-plugin', 'plugin.json'), 'utf8'));
const distribution = JSON.parse(await readFile(resolve(pluginRoot, 'distribution.json'), 'utf8'));
const sourcePaths = (await walkFiles(sourceSkillRoot)).sort();
const sourceFiles = new Map();
const digest = value => createHash('sha256').update(value).digest('hex');
for (const path of sourcePaths) {
  const name = relative(sourceSkillRoot, path).replaceAll('\\', '/');
  const content = await readFile(path);
  sourceFiles.set(name, { content, hash: digest(content) });
}

async function exists(path) {
  try { await access(path); return true; } catch { return false; }
}

async function loadPreviousLock() {
  try { return JSON.parse(await readFile(lockPath, 'utf8')); } catch { return undefined; }
}

async function destinationFiles() {
  if (!(await exists(destinationRoot))) return new Map();
  return new Map((await walkFiles(destinationRoot))
    .filter(path => path !== lockPath)
    .map(path => [relative(destinationRoot, path).replaceAll('\\', '/'), path]));
}

const previousLock = await loadPreviousLock();
const currentFiles = await destinationFiles();
const drift = [];
for (const [name, source] of sourceFiles) {
  const currentPath = currentFiles.get(name);
  if (!currentPath) {
    drift.push(`missing ${name}`);
    continue;
  }
  if (digest(await readFile(currentPath)) !== source.hash) drift.push(`changed ${name}`);
}
for (const name of currentFiles.keys()) if (!sourceFiles.has(name)) drift.push(`unexpected ${name}`);
if (!previousLock) drift.push('missing .newheap-skill-install.json');
else {
  if (previousLock.pluginVersion !== plugin.version) drift.push(`plugin version ${previousLock.pluginVersion} != ${plugin.version}`);
  if (previousLock.guidanceVersion !== distribution.guidanceVersion) drift.push('guidance version is stale');
  if (previousLock.skillContentHash !== distribution.skillContentHash) drift.push('skill content is stale');
  for (const [name, source] of sourceFiles) if (previousLock.files?.[name] !== source.hash) drift.push(`lock hash is stale for ${name}`);
  for (const name of Object.keys(previousLock.files ?? {})) if (!sourceFiles.has(name)) drift.push(`lock contains stale file ${name}`);
}

if (checkOnly) {
  if (drift.length > 0) throw new Error(`Consumer skill is not synchronized:\n- ${drift.join('\n- ')}`);
  console.log(`Consumer skill is synchronized at ${destinationRoot} (plugin ${plugin.version}).`);
  process.exit(0);
}

if (currentFiles.size > 0 && !previousLock && !force) {
  throw new Error(`An unmanaged skill already exists at ${destinationRoot}. Re-run with --force only if replacing it is intentional.`);
}
if (previousLock && !force) {
  const previousFiles = previousLock.files ?? {};
  const locallyChanged = [];
  for (const [name, currentPath] of currentFiles) {
    if (!previousFiles[name] || digest(await readFile(currentPath)) !== previousFiles[name]) locallyChanged.push(name);
  }
  if (locallyChanged.length > 0) {
    throw new Error(`Refusing to overwrite locally changed installed skill files:\n- ${locallyChanged.join('\n- ')}\nUse --force only after reviewing them.`);
  }
}

if (force && await exists(destinationRoot)) await rm(destinationRoot, { recursive: true, force: true });
if (!force && previousLock) {
  for (const name of Object.keys(previousLock.files ?? {})) {
    if (!sourceFiles.has(name)) await rm(resolve(destinationRoot, name), { force: true });
  }
}

for (const [name, source] of sourceFiles) {
  const destination = resolve(destinationRoot, name);
  await mkdir(dirname(destination), { recursive: true });
  await writeFile(destination, source.content);
}

const lock = {
  schemaVersion: 1,
  skill: 'newheap-consumer-development',
  pluginVersion: plugin.version,
  guidanceVersion: distribution.guidanceVersion,
  skillContentHash: distribution.skillContentHash,
  compatiblePackages: distribution.compatiblePackages,
  source: 'newheap-platform-plugin',
  files: Object.fromEntries([...sourceFiles].map(([name, value]) => [name, value.hash]))
};
await writeFile(lockPath, `${JSON.stringify(lock, null, 2)}\n`, 'utf8');
console.log(`Installed NewHeap consumer skill plugin ${plugin.version} into ${destinationRoot}. Commit .agents/skills so every agent uses the pinned version.`);
