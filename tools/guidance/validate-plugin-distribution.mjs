import { access, readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { relative, resolve } from 'node:path';
import {
  consumerPluginRoot,
  consumerPluginSkillRoot,
  consumerSkillRoot,
  packageVersions,
  readJson,
  repositoryRoot,
  walkFiles
} from './lib.mjs';

const failures = [];
const manifestPath = resolve(consumerPluginRoot, '.codex-plugin', 'plugin.json');
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
const distribution = JSON.parse(await readFile(resolve(consumerPluginRoot, 'distribution.json'), 'utf8'));
const versions = await packageVersions();
const guidanceVersion = await readJson(resolve(repositoryRoot, 'guidance', 'version.json'));

if (manifest.name !== 'newheap-platform') failures.push('Plugin name must be newheap-platform.');
if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(manifest.version ?? '')) failures.push('Plugin version must use strict semver.');
if (manifest.version !== guidanceVersion.guidanceVersion) failures.push(`Plugin ${manifest.version} does not match guidance ${guidanceVersion.guidanceVersion}.`);
if (distribution.pluginVersion !== manifest.version) failures.push('Plugin distribution metadata has a stale pluginVersion.');
if (distribution.compatiblePackages?.['@newheap/platform-common'] !== versions['@newheap/platform-common']) failures.push('Plugin package compatibility metadata is stale.');
if (distribution.repositoryTarget !== '.agents/skills/newheap-consumer-development') failures.push('Plugin repository target is invalid.');
if (manifest.skills !== './skills/') failures.push('Plugin skills path must be ./skills/.');
for (const key of ['displayName', 'shortDescription', 'longDescription', 'developerName', 'category']) {
  if (!manifest.interface?.[key]) failures.push(`Plugin interface.${key} is required.`);
}
if (manifest.hooks || manifest.apps || manifest.mcpServers) failures.push('Plugin declares a companion surface that is not packaged.');

const canonicalFiles = new Map((await walkFiles(consumerSkillRoot)).map(path => [
  relative(consumerSkillRoot, path).replaceAll('\\', '/'),
  path
]));
const distributedFiles = new Map((await walkFiles(consumerPluginSkillRoot)).map(path => [
  relative(consumerPluginSkillRoot, path).replaceAll('\\', '/'),
  path
]));
const normalize = value => value.replaceAll('\r\n', '\n');
const canonicalContents = new Map();

for (const [path, sourcePath] of canonicalFiles) {
  const source = await readFile(sourcePath, 'utf8');
  canonicalContents.set(path, source);
  const distributedPath = distributedFiles.get(path);
  if (!distributedPath) {
    failures.push(`Plugin is missing consumer skill file ${path}.`);
    continue;
  }
  const distributed = await readFile(distributedPath, 'utf8');
  if (normalize(source) !== normalize(distributed)) failures.push(`Plugin consumer skill is stale: ${path}.`);
}
for (const path of distributedFiles.keys()) if (!canonicalFiles.has(path)) failures.push(`Plugin contains unexpected generated skill file ${path}.`);
const contentHash = createHash('sha256').update([...canonicalFiles].sort(([left], [right]) => left.localeCompare(right)).map(([path, sourcePath]) => {
  return `${path}\0${canonicalContents.get(path).replaceAll('\r\n', '\n')}`;
}).join('\n')).digest('hex');
if (distribution.skillContentHash !== contentHash) failures.push('Plugin skillContentHash is stale.');
try { await access(resolve(consumerPluginRoot, 'scripts', 'install-consumer-skill.mjs')); }
catch { failures.push('Plugin is missing its portable consumer-skill installer.'); }

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log(`Validated newheap-platform ${manifest.version} with ${canonicalFiles.size} mirrored consumer-skill files.`);
