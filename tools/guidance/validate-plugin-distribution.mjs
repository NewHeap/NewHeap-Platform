import { access, readFile, readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { relative, resolve } from 'node:path';
import {
  consumerPluginRoot,
  consumerPluginSkillBundleRoot,
  consumerPluginSkillsRoot,
  consumerSkillBundleName,
  consumerSkillBundleRoot,
  consumerSkillModuleDirectories,
  consumerSkillNames,
  consumerSkillRoots,
  packageVersions,
  readJson,
  renderBundledConsumerSkillFile,
  repositoryRoot,
  walkFiles
} from './lib.mjs';

const failures = [];
const manifestPath = resolve(consumerPluginRoot, '.codex-plugin', 'plugin.json');
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
const distribution = JSON.parse(await readFile(resolve(consumerPluginRoot, 'distribution.json'), 'utf8'));
const installGuide = await readFile(resolve(consumerPluginRoot, 'INSTALL.md'), 'utf8');
const versions = await packageVersions();
const guidanceVersion = await readJson(resolve(repositoryRoot, 'guidance', 'version.json'));

if (manifest.name !== 'newheap-platform') failures.push('Plugin name must be newheap-platform.');
if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(manifest.version ?? '')) failures.push('Plugin version must use strict semver.');
if (manifest.version !== guidanceVersion.guidanceVersion) failures.push(`Plugin ${manifest.version} does not match guidance ${guidanceVersion.guidanceVersion}.`);
if (distribution.pluginVersion !== manifest.version) failures.push('Plugin distribution metadata has a stale pluginVersion.');
if (distribution.compatiblePackages?.['@newheap/platform-common'] !== versions['@newheap/platform-common']) failures.push('Plugin package compatibility metadata is stale.');
if (distribution.schemaVersion !== 3) failures.push('Plugin distribution metadata must use schema version 3.');
if (distribution.repositoryTarget !== `.agents/skills/${consumerSkillBundleName}`) failures.push('Plugin repository target is invalid.');
if (distribution.repositoryTargets?.codex !== `.agents/skills/${consumerSkillBundleName}`
  || distribution.repositoryTargets?.claude !== `.claude/skills/${consumerSkillBundleName}`) {
  failures.push('Plugin repository targets must identify the grouped Codex and Claude skill directories.');
}
if (JSON.stringify(distribution.skills) !== JSON.stringify([consumerSkillBundleName])) failures.push('Plugin distributed skill list is stale.');
if (JSON.stringify(distribution.modules) !== JSON.stringify(consumerSkillNames)) failures.push('Plugin module list is stale.');
if (JSON.stringify(distribution.moduleDirectories) !== JSON.stringify(Object.fromEntries(consumerSkillModuleDirectories))) {
  failures.push('Plugin module-directory mapping is stale.');
}
if (manifest.skills !== './skills/') failures.push('Plugin skills path must be ./skills/.');
for (const key of ['displayName', 'shortDescription', 'longDescription', 'developerName', 'category']) {
  if (!manifest.interface?.[key]) failures.push(`Plugin interface.${key} is required.`);
}
if (manifest.hooks || manifest.apps || manifest.mcpServers) failures.push('Plugin declares a companion surface that is not packaged.');
if (!installGuide.includes('newheap-platform-plugin-v<version>')) failures.push('Plugin install guide must identify the versioned GitHub Release tag.');
if (!installGuide.includes('newheap-platform-<version>.tar.gz')) failures.push('Plugin install guide must identify the generated archive name.');
if (!installGuide.includes('install-consumer-skills.mjs')) failures.push('Plugin install guide must use the focused skill-suite installer.');
if (!installGuide.includes('--target claude') || !installGuide.includes('--target both')) failures.push('Plugin install guide must document Claude and mixed-agent targets.');
if (!installGuide.includes('self-contained') || !installGuide.includes('immutable public source')) failures.push('Plugin install guide must explain the optional immutable sample evidence.');
if (!installGuide.includes('--profile management-portal --database postgresql')) failures.push('Plugin bootstrap example must select an explicit profile and persistence provider.');
if (!installGuide.includes('https://api.nuget.org/v3/index.json') || !installGuide.includes('https://registry.npmjs.org/')) {
  failures.push('Plugin install guide must use the public NuGet and npm registries.');
}
if (/configure machine-level credentials/i.test(installGuide)) failures.push('Plugin install guide must not request consumer credentials for public NewHeap packages.');

const canonicalFiles = new Map();
const distributedFiles = new Map();
for (const path of await walkFiles(consumerSkillBundleRoot)) {
  canonicalFiles.set(relative(consumerSkillBundleRoot, path).replaceAll('\\', '/'), path);
}
for (const skillName of consumerSkillNames) {
  const canonicalRoot = consumerSkillRoots.get(skillName);
  for (const path of await walkFiles(canonicalRoot)) {
    canonicalFiles.set(`skills/${consumerSkillModuleDirectories.get(skillName)}/${relative(canonicalRoot, path).replaceAll('\\', '/')}`, path);
  }
}
for (const path of await walkFiles(consumerPluginSkillBundleRoot)) {
  distributedFiles.set(relative(consumerPluginSkillBundleRoot, path).replaceAll('\\', '/'), path);
}
const pluginSkillEntries = await readdir(consumerPluginSkillsRoot);
if (pluginSkillEntries.length !== 1 || pluginSkillEntries[0] !== consumerSkillBundleName) {
  failures.push(`Plugin skills must be contained only in ${consumerSkillBundleName}.`);
}
const normalize = value => value.replaceAll('\r\n', '\n');
const canonicalContents = new Map();

for (const [path, sourcePath] of canonicalFiles) {
  const canonicalSource = await readFile(sourcePath, 'utf8');
  const bundleRelative = relative(consumerSkillBundleRoot, sourcePath);
  const source = bundleRelative && !bundleRelative.startsWith('..')
    ? renderBundledConsumerSkillFile(path, canonicalSource)
    : canonicalSource;
  canonicalContents.set(path, source);
  if (path.includes('/references/') && source.startsWith('<!-- Generated by tools/guidance/generate-guidance.mjs.')) {
    if (source.includes('## Executable evidence')) failures.push(`Shipped skill reference contains local-only evidence paths: ${path}.`);
    if (!source.includes(`blob/newheap-platform-plugin-v${manifest.version}/docs/consumer-guide/`)) {
      failures.push(`Shipped skill reference lacks immutable ${manifest.version} public evidence: ${path}.`);
    }
  }
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
try { await access(resolve(consumerPluginRoot, 'scripts', 'install-consumer-skills.mjs')); }
catch { failures.push('Plugin is missing its portable consumer-skills installer.'); }
try { await access(resolve(consumerPluginRoot, 'scripts', 'install-consumer-skill.mjs')); }
catch { failures.push('Plugin is missing its backward-compatible singular installer alias.'); }

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log(`Validated newheap-platform ${manifest.version} with one router skill, ${consumerSkillNames.length} modules and ${canonicalFiles.size} mirrored files.`);
