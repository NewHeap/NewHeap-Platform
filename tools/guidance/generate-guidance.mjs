import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, relative, resolve } from 'node:path';
import {
  consumerGuideRoot,
  consumerPluginSkillRoot,
  consumerSkillRoot,
  groupRulesByReference,
  loadRegistry,
  loadRules,
  maintenanceSkillRoot,
  packageVersions,
  planPath,
  planTemplatePath,
  renderGuideIndex,
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
const template = await readFile(planTemplatePath, 'utf8');
const outputs = new Map([
  [planPath, renderPlan(registry, template)],
  [statusPath, renderStatus(registry)],
  [resolve(consumerGuideRoot, 'index.md'), renderGuideIndex(groups, rules)],
  [resolve(consumerGuideRoot, 'llms.txt'), renderLlmsIndex(groups)]
]);

for (const [reference, matchingRules] of [...groups.entries()].sort(([left], [right]) => left.localeCompare(right))) {
  const title = reference.split('-').map(word => word[0].toUpperCase() + word.slice(1)).join(' ');
  outputs.set(
    resolve(consumerSkillRoot, 'references', `${reference}.md`),
    renderRuleCollection(title, 'Use only the rules that apply to the current consumer task.', matchingRules, registry)
  );
  outputs.set(
    resolve(consumerGuideRoot, `${reference}.md`),
    renderRuleCollection(title, 'Human-readable reference generated from the same rules as the NewHeap consumer skill.', matchingRules, registry, true)
  );
}

const consumerSkillFiles = (await walkFiles(consumerSkillRoot)).map(path => ({
  path,
  name: relative(consumerSkillRoot, path).replaceAll('\\', '/')
})).sort((left, right) => left.name.localeCompare(right.name));
const consumerSkillContents = new Map();
for (const { path } of consumerSkillFiles) {
  const sourceContent = outputs.get(path) ?? await readFile(path, 'utf8');
  consumerSkillContents.set(path, sourceContent);
  const distributionPath = resolve(consumerPluginSkillRoot, relative(consumerSkillRoot, path));
  outputs.set(distributionPath, sourceContent);
}

const consumerSkillContentHash = createHash('sha256').update(consumerSkillFiles.map(({ path, name }) => {
  const content = consumerSkillContents.get(path);
  return `${name}\0${content.replaceAll('\r\n', '\n')}`;
}).join('\n')).digest('hex');

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
      name: 'newheap-consumer-development',
      path: 'skills/newheap-consumer-development',
      audience: 'consumer-applications',
      distribution: {
        plugin: 'plugins/newheap-platform',
        pluginVersion: guidanceVersion.guidanceVersion,
        portableInstaller: 'plugins/newheap-platform/scripts/install-consumer-skill.mjs',
        githubReleaseUnit: 'newheap-platform-plugin',
        releaseAssetPattern: 'newheap-platform-<version>.tar.gz',
        repositoryInstallCommand: 'node tools/guidance/install-consumer-skill.mjs --consumer <consumer-root>'
      },
      references: [
        ...[...groups.keys()].sort().map(reference => `references/${reference}.md`),
        'references/package-sources.md'
      ]
    },
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
  schemaVersion: 1,
  pluginVersion: guidanceVersion.guidanceVersion,
  guidanceVersion: guidanceVersion.guidanceVersion,
  skillContentHash: consumerSkillContentHash,
  compatiblePackages: versions,
  repositoryTarget: '.agents/skills/newheap-consumer-development'
}, null, 2)}\n`);

const stale = [];
const normalizeLineEndings = value => value?.replaceAll('\r\n', '\n');
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

if (stale.length > 0) {
  throw new Error(`Generated guidance is stale:\n${stale.map(path => `- ${path}`).join('\n')}\nRun npm run guidance:generate.`);
}
console.log(checkOnly ? `Verified ${outputs.size} generated guidance files.` : `Generated ${outputs.size} guidance files from ${rules.length} rules.`);
