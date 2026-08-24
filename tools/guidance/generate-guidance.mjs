import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, relative, resolve } from 'node:path';
import {
  consumerGuideRoot,
  consumerPluginSkillBundleRoot,
  consumerPluginSkillRoots,
  consumerPluginSkillsRoot,
  consumerSkillBundleName,
  consumerSkillBundleRoot,
  canonicalConsumerSkillEvidenceCatalogLink,
  consumerSkillEvidenceCatalogPath,
  consumerSkillModuleDirectories,
  consumerSkillNames,
  consumerSkillRoots,
  groupRulesByReference,
  loadRegistry,
  loadRules,
  maintenanceSkillRoot,
  packageVersions,
  planPath,
  planTemplatePath,
  renderGuideIndex,
  renderBundledConsumerSkillFile,
  renderImmutableEvidenceCatalog,
  renderLlmsIndex,
  renderPlan,
  renderRuleCollection,
  renderStatus,
  repositoryRoot,
  statusPath,
  validateRegistry,
  validateRules,
  walkFiles
} from './lib.mjs';

const checkOnly = process.argv.includes('--check');
const registry = await loadRegistry();
const rules = await loadRules();
const failures = [...validateRegistry(registry), ...validateRules(rules, registry)];
if (failures.length > 0) throw new Error(failures.join('\n'));
const groups = groupRulesByReference(rules);
const versions = await packageVersions();
const guidanceVersion = JSON.parse(await readFile(resolve(repositoryRoot, 'guidance', 'version.json'), 'utf8'));
const pluginReleaseRef = `newheap-platform-plugin-v${guidanceVersion.guidanceVersion}`;
const template = await readFile(planTemplatePath, 'utf8');
const outputs = new Map([
  [planPath, renderPlan(registry, template)],
  [statusPath, renderStatus(registry)],
  [resolve(consumerGuideRoot, 'index.md'), renderGuideIndex(groups, rules)],
  [resolve(consumerGuideRoot, 'llms.txt'), renderLlmsIndex(groups)],
  [consumerSkillEvidenceCatalogPath, renderImmutableEvidenceCatalog(groups, pluginReleaseRef)]
]);

for (const [reference, matchingRules] of [...groups.entries()].sort(([left], [right]) => left.localeCompare(right))) {
  const title = reference.split('-').map(word => word[0].toUpperCase() + word.slice(1)).join(' ');
  outputs.set(
    resolve(consumerGuideRoot, `${reference}.md`),
    renderRuleCollection(title, 'Human-readable reference generated from the same rules as the NewHeap consumer skills.', matchingRules, registry, 'links')
  );
}

const referencesBySkill = new Map();
for (const skillName of consumerSkillNames) {
  const skillGroups = groupRulesByReference(rules.filter(rule => rule.skills.includes(skillName)));
  referencesBySkill.set(skillName, skillGroups);
  for (const [reference, matchingRules] of [...skillGroups.entries()].sort(([left], [right]) => left.localeCompare(right))) {
    const title = reference.split('-').map(word => word[0].toUpperCase() + word.slice(1)).join(' ');
    outputs.set(
      resolve(consumerSkillRoots.get(skillName), 'references', `${reference}.md`),
      renderRuleCollection(
        title,
        'Use only the rules that apply to the current consumer task.',
        matchingRules,
        registry,
        'compact-catalog',
        canonicalConsumerSkillEvidenceCatalogLink
      )
    );
  }
}

const consumerSkillFiles = [];
const consumerSkillBundlePaths = new Set(await walkFiles(consumerSkillBundleRoot));
for (const path of outputs.keys()) {
  const outputRelative = relative(consumerSkillBundleRoot, path);
  if (outputRelative && !outputRelative.startsWith('..')) consumerSkillBundlePaths.add(path);
}
for (const path of consumerSkillBundlePaths) {
  const name = relative(consumerSkillBundleRoot, path).replaceAll('\\', '/');
  consumerSkillFiles.push({
    path,
    name,
    distributionPath: resolve(consumerPluginSkillBundleRoot, name)
  });
}
for (const skillName of consumerSkillNames) {
  const skillRoot = consumerSkillRoots.get(skillName);
  const skillPaths = new Set(await walkFiles(skillRoot));
  for (const path of outputs.keys()) {
    const outputRelative = relative(skillRoot, path);
    if (outputRelative && !outputRelative.startsWith('..')) skillPaths.add(path);
  }
  for (const path of skillPaths) {
    consumerSkillFiles.push({
      path,
      name: `skills/${consumerSkillModuleDirectories.get(skillName)}/${relative(skillRoot, path).replaceAll('\\', '/')}`,
      distributionPath: resolve(consumerPluginSkillRoots.get(skillName), relative(skillRoot, path))
    });
  }
}
consumerSkillFiles.sort((left, right) => left.name.localeCompare(right.name));
const consumerSkillContents = new Map();
for (const { path, name, distributionPath } of consumerSkillFiles) {
  const sourceContent = outputs.get(path) ?? await readFile(path, 'utf8');
  const distributedContent = renderBundledConsumerSkillFile(name, sourceContent);
  consumerSkillContents.set(name, distributedContent);
  outputs.set(distributionPath, distributedContent);
}

const consumerSkillContentHash = createHash('sha256').update(consumerSkillFiles.map(({ name }) => {
  const content = consumerSkillContents.get(name);
  return `${name}\0${content.replaceAll('\r\n', '\n')}`;
}).join('\n')).digest('hex');

const distribution = {
  plugin: 'plugins/newheap-platform',
  pluginVersion: guidanceVersion.guidanceVersion,
  portableInstaller: 'plugins/newheap-platform/scripts/install-consumer-skills.mjs',
  githubReleaseUnit: 'newheap-platform-plugin',
  releaseAssetPattern: 'newheap-platform-<version>.tar.gz',
  repositoryInstallCommand: 'node tools/guidance/install-consumer-skills.mjs --consumer <consumer-root>',
  repositoryInstallCommands: {
    codex: 'node tools/guidance/install-consumer-skills.mjs --consumer <consumer-root> --target codex',
    claude: 'node tools/guidance/install-consumer-skills.mjs --consumer <consumer-root> --target claude',
    both: 'node tools/guidance/install-consumer-skills.mjs --consumer <consumer-root> --target both'
  },
  repositoryTarget: `.agents/skills/${consumerSkillBundleName}`,
  repositoryTargets: {
    codex: `.agents/skills/${consumerSkillBundleName}`,
    claude: `.claude/skills/${consumerSkillBundleName}`
  }
};

const consumerSkillManifestEntries = consumerSkillNames.map(name => {
  const references = [...referencesBySkill.get(name).keys()].sort().map(reference => `references/${reference}.md`);
  if (name === 'newheap-consumer-development') references.push('references/package-sources.md');
  return {
    name,
    path: `skills/${name}`,
    bundledPath: `skills/${consumerSkillBundleName}/skills/${consumerSkillModuleDirectories.get(name)}`,
    audience: 'consumer-applications',
    distribution,
    references
  };
});

const manifest = {
  schemaVersion: 1,
  guidance: guidanceVersion,
  source: {
    caseRegistry: 'examples/SampleProjectManagement/docs/cases/sample-case-registry.json',
    guidanceRules: 'guidance/rules'
  },
  packages: versions,
  skills: [
    {
      name: consumerSkillBundleName,
      path: `skills/${consumerSkillBundleName}`,
      audience: 'consumer-applications',
      distribution,
      references: ['references/immutable-evidence.md'],
      modules: consumerSkillNames
    },
    ...consumerSkillManifestEntries,
    {
      name: 'newheap-library-maintenance',
      path: 'skills/newheap-library-maintenance',
      audience: 'platform-maintainers',
      references: ['references/sample-maintenance.md', 'references/skill-impact.md', 'references/release-checklist.md', 'references/github-releases.md']
    }
  ]
};
outputs.set(resolve(repositoryRoot, 'skills', 'skill-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
outputs.set(resolve(repositoryRoot, 'plugins', 'newheap-platform', 'distribution.json'), `${JSON.stringify({
  schemaVersion: 3,
  pluginVersion: guidanceVersion.guidanceVersion,
  guidanceVersion: guidanceVersion.guidanceVersion,
  skillContentHash: consumerSkillContentHash,
  skills: [consumerSkillBundleName],
  modules: consumerSkillNames,
  moduleDirectories: Object.fromEntries(consumerSkillModuleDirectories),
  compatiblePackages: versions,
  evidence: {
    sourceRef: pluginReleaseRef,
    catalog: `skills/${consumerSkillBundleName}/references/immutable-evidence.md`
  },
  repositoryTarget: `.agents/skills/${consumerSkillBundleName}`,
  repositoryTargets: {
    codex: `.agents/skills/${consumerSkillBundleName}`,
    claude: `.claude/skills/${consumerSkillBundleName}`
  }
}, null, 2)}\n`);

const stale = [];
const normalizeLineEndings = value => value?.replaceAll('\r\n', '\n');
const expectedPluginSkillFiles = new Set([...outputs.keys()].filter(path => {
  const outputRelative = relative(consumerPluginSkillsRoot, path);
  return outputRelative && !outputRelative.startsWith('..');
}));
if (!checkOnly) await rm(consumerPluginSkillsRoot, { recursive: true, force: true });
for (const [path, content] of outputs) {
  let current;
  try { current = await readFile(path, 'utf8'); } catch { current = undefined; }
  if (checkOnly) {
    if (normalizeLineEndings(current) !== normalizeLineEndings(content)) stale.push(path);
    continue;
  }
  if (normalizeLineEndings(current) === normalizeLineEndings(content)) continue;
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, content, 'utf8');
}

if (checkOnly) {
  const existingPluginSkillFiles = await walkFiles(consumerPluginSkillsRoot).catch(() => []);
  for (const path of existingPluginSkillFiles) {
    if (!expectedPluginSkillFiles.has(path)) stale.push(path);
  }
}

if (stale.length > 0) {
  throw new Error(`Generated guidance is stale:\n${stale.map(path => `- ${path}`).join('\n')}\nRun npm run guidance:generate.`);
}
console.log(checkOnly ? `Verified ${outputs.size} generated guidance files.` : `Generated ${outputs.size} guidance files from ${rules.length} rules.`);
