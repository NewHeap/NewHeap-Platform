import { readdir, readFile } from 'node:fs/promises';
import { dirname, extname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const toolDirectory = dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = resolve(toolDirectory, '..', '..');
export const sampleRoot = resolve(repositoryRoot, 'examples', 'SampleProjectManagement');
export const sampleDocsRoot = resolve(sampleRoot, 'docs');
export const registryPath = resolve(sampleDocsRoot, 'cases', 'sample-case-registry.json');
export const planTemplatePath = resolve(sampleDocsRoot, 'cases', 'library-sample-plan.template.md');
export const planPath = resolve(sampleDocsRoot, 'library-sample-plan.md');
export const statusPath = resolve(sampleDocsRoot, 'sample-implementation-status.json');
export const rulesRoot = resolve(repositoryRoot, 'guidance', 'rules');
export const consumerSkillsRoot = resolve(repositoryRoot, 'skills');
export const consumerSkillRoot = resolve(repositoryRoot, 'skills', 'newheap-consumer-development');
export const consumerSkillNames = [
  'newheap-consumer-development',
  'newheap-authentication',
  'newheap-backend-development',
  'newheap-background-processing',
  'newheap-database-development',
  'newheap-frontend-development',
  'newheap-media-development',
  'newheap-runtime-configuration',
  'newheap-testing'
];
export const consumerSkillRoots = new Map(consumerSkillNames.map(name => [name, resolve(consumerSkillsRoot, name)]));
export const consumerSkillBundleName = 'newheap-platform-development';
export const consumerSkillBundleRoot = resolve(consumerSkillsRoot, consumerSkillBundleName);
export const consumerSkillEvidenceCatalogName = 'immutable-evidence.md';
export const consumerSkillEvidenceCatalogPath = resolve(consumerSkillBundleRoot, 'references', consumerSkillEvidenceCatalogName);
export const canonicalConsumerSkillEvidenceCatalogLink = `../../${consumerSkillBundleName}/references/${consumerSkillEvidenceCatalogName}`;
export const consumerSkillModuleDirectories = new Map([
  ['newheap-consumer-development', 'foundation'],
  ['newheap-authentication', 'authentication'],
  ['newheap-backend-development', 'backend'],
  ['newheap-background-processing', 'background-processing'],
  ['newheap-database-development', 'database'],
  ['newheap-frontend-development', 'frontend'],
  ['newheap-media-development', 'media'],
  ['newheap-runtime-configuration', 'runtime-configuration'],
  ['newheap-testing', 'testing']
]);
export const maintenanceSkillRoot = resolve(repositoryRoot, 'skills', 'newheap-library-maintenance');
export const consumerGuideRoot = resolve(repositoryRoot, 'docs', 'consumer-guide');
export const consumerPluginRoot = resolve(repositoryRoot, 'plugins', 'newheap-platform');
export const consumerPluginSkillsRoot = resolve(consumerPluginRoot, 'skills');
export const consumerPluginSkillBundleRoot = resolve(consumerPluginSkillsRoot, consumerSkillBundleName);
export const consumerPluginSkillRoot = resolve(consumerPluginSkillBundleRoot, 'skills', consumerSkillModuleDirectories.get('newheap-consumer-development'));
export const consumerPluginSkillRoots = new Map(consumerSkillNames.map(name => [
  name,
  resolve(consumerPluginSkillBundleRoot, 'skills', consumerSkillModuleDirectories.get(name))
]));

export function renderBundledConsumerSkillFile(relativePath, source) {
  let bundled = source;
  if (relativePath === 'SKILL.md') {
    for (const [skillName, moduleDirectory] of consumerSkillModuleDirectories) {
      bundled = bundled.replaceAll(`../${skillName}/SKILL.md`, `skills/${moduleDirectory}/SKILL.md`);
    }
  }
  if (/^skills\/[^/]+\/references\/[^/]+\.md$/.test(relativePath)) {
    bundled = bundled.replaceAll(
      canonicalConsumerSkillEvidenceCatalogLink,
      `../../../references/${consumerSkillEvidenceCatalogName}`
    );
  }
  return bundled;
}

export async function readJson(path) {
  return JSON.parse(await readFile(path, 'utf8'));
}

export async function walkFiles(directory) {
  const paths = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) paths.push(...await walkFiles(path));
    else paths.push(path);
  }
  return paths;
}

function parseValue(raw, path, key) {
  const value = raw.trim();
  if (value.startsWith('[')) {
    try { return JSON.parse(value); }
    catch { throw new Error(`${path}: ${key} must use a JSON-compatible inline array.`); }
  }
  if (value.startsWith('"')) {
    try { return JSON.parse(value); }
    catch { throw new Error(`${path}: ${key} contains an invalid quoted value.`); }
  }
  if (value === 'true') return true;
  if (value === 'false') return false;
  return value;
}

export function parseGuidanceRule(source, path) {
  const match = source.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/);
  if (!match) throw new Error(`${path}: expected YAML frontmatter.`);
  const metadata = {};
  for (const line of match[1].split(/\r?\n/)) {
    if (!line.trim()) continue;
    const separator = line.indexOf(':');
    if (separator < 1) throw new Error(`${path}: invalid frontmatter line: ${line}`);
    const key = line.slice(0, separator).trim();
    metadata[key] = parseValue(line.slice(separator + 1), path, key);
  }
  return { ...metadata, body: match[2].trim(), sourcePath: path };
}

export async function loadRegistry() {
  const registry = await readJson(registryPath);
  if (registry.schemaVersion !== 1) throw new Error('Unsupported sample case registry schema.');
  if (!Array.isArray(registry.categories) || !Array.isArray(registry.cases)) throw new Error('Invalid sample case registry.');
  return registry;
}

export async function loadRules() {
  const paths = (await walkFiles(rulesRoot)).filter(path => extname(path) === '.md').sort();
  return Promise.all(paths.map(async path => parseGuidanceRule(await readFile(path, 'utf8'), path)));
}

export function validateRegistry(registry) {
  const failures = [];
  const categoryIds = new Set(registry.categories.map(category => category.id));
  const caseIds = new Set();
  for (const item of registry.cases) {
    if (!/^SPM-\d{3}$/.test(item.id)) failures.push(`Invalid case id: ${item.id}`);
    if (caseIds.has(item.id)) failures.push(`Duplicate case id: ${item.id}`);
    caseIds.add(item.id);
    if (!categoryIds.has(item.categoryId)) failures.push(`${item.id}: unknown category ${item.categoryId}`);
    if (!['implemented', 'partial', 'planned', 'library-gap'].includes(item.implementation)) failures.push(`${item.id}: invalid implementation status`);
    if ((item.implementation === 'partial' || item.implementation === 'library-gap') && !item.statusReason) failures.push(`${item.id}: missing status reason`);
    if (!Array.isArray(item.evidence)) failures.push(`${item.id}: evidence must be an array`);
  }
  return failures;
}

export function validateRules(rules, registry) {
  const failures = [];
  const ruleIds = new Set();
  const caseById = new Map(registry.cases.map(item => [item.id, item]));
  const required = ['id', 'title', 'area', 'reference', 'summary', 'sample-cases', 'public-symbols', 'skills', 'providers', 'risk'];
  for (const rule of rules) {
    const displayPath = relative(repositoryRoot, rule.sourcePath);
    for (const key of required) if (!(key in rule)) failures.push(`${displayPath}: missing ${key}`);
    if (!/^nh-[a-z0-9]+(?:-[a-z0-9]+)+$/.test(rule.id ?? '')) failures.push(`${displayPath}: invalid rule id`);
    if (ruleIds.has(rule.id)) failures.push(`${displayPath}: duplicate rule id ${rule.id}`);
    ruleIds.add(rule.id);
    for (const key of ['sample-cases', 'public-symbols', 'skills', 'providers']) {
      if (!Array.isArray(rule[key]) || rule[key].length === 0) failures.push(`${displayPath}: ${key} must be a non-empty array`);
    }
    for (const skill of rule.skills ?? []) {
      if (!consumerSkillNames.includes(skill)) failures.push(`${displayPath}: unknown consumer skill ${skill}`);
    }
    for (const id of rule['sample-cases'] ?? []) {
      const sampleCase = caseById.get(id);
      if (!sampleCase) failures.push(`${displayPath}: unknown sample case ${id}`);
      else if (sampleCase.implementation !== 'implemented') failures.push(`${displayPath}: ${id} is ${sampleCase.implementation}; consumer guidance requires executable evidence`);
    }
    if (!rule.body?.includes('## Preferred approach')) failures.push(`${displayPath}: missing Preferred approach section`);
    if (!rule.body?.includes('## Avoid')) failures.push(`${displayPath}: missing Avoid section`);
    if (!rule.body?.includes('## Verification')) failures.push(`${displayPath}: missing Verification section`);
  }
  return failures;
}

export function renderPlan(registry, template) {
  const byCategory = Map.groupBy(registry.cases, item => item.categoryId);
  return template.replace(/<!-- SAMPLE_CASES:(\d+) -->/g, (_, categoryId) => {
    const items = byCategory.get(categoryId) ?? [];
    return items.map(item => `| ${item.id} | ${item.title} | ${item.surface} | ${item.outcome} |`).join('\n');
  });
}

export function renderStatus(registry) {
  const implemented = registry.cases.filter(item => item.implementation === 'implemented').map(item => item.id);
  const partial = Object.fromEntries(registry.cases.filter(item => item.implementation === 'partial').map(item => [item.id, item.statusReason]));
  const gaps = Object.fromEntries(registry.cases.filter(item => item.implementation === 'library-gap').map(item => [item.id, item.statusReason]));
  const evidence = Object.fromEntries(registry.cases.filter(item => item.evidence.length > 0).map(item => [item.id, item.evidence]));
  return `${JSON.stringify({ implemented, gaps, partial, evidence }, null, 2)}\n`;
}

function ruleHeading(rule) {
  return `## ${rule.title}\n\n${rule.summary}\n\n`;
}

function ruleAnchor(rule) {
  return rule.title.toLowerCase().replace(/[^\p{L}\p{N}\s-]/gu, '').trim().replace(/[\s-]+/g, '-');
}

function referenceTitle(reference) {
  return reference.split('-').map(word => word[0].toUpperCase() + word.slice(1)).join(' ');
}

export function renderImmutableEvidenceCatalog(groups, sourceRef) {
  const sections = [...groups.keys()].sort().map(reference => {
    return `## ${referenceTitle(reference)}\n\n- [Immutable guide and executable evidence](https://github.com/NewHeap/NewHeap-Platform/blob/${sourceRef}/docs/consumer-guide/${reference}.md)`;
  }).join('\n\n');
  return `<!-- Generated by tools/guidance/generate-guidance.mjs. Do not edit by hand. -->\n\n# Immutable Source Evidence\n\nThis catalog centralizes the release-pinned source links used by the bundled consumer skills. Feature changes keep these links on the last published plugin release; the protected Prepare release workflow updates the catalog exactly once when it creates the next immutable plugin release.\n\nSource release: \`${sourceRef}\`\n\n${sections}\n`;
}

function sampleEvidence(rule, registry, mode = 'paths', evidenceCatalogPath) {
  const caseById = new Map(registry.cases.map(item => [item.id, item]));
  if (mode === 'compact-catalog') {
    return [
      '## Optional source evidence',
      '',
      'The rule above is self-contained. Open the immutable public guide only when exact sample composition remains unclear.',
      `- [Immutable guide and executable evidence](${evidenceCatalogPath}#${rule.reference})`
    ].join('\n');
  }
  const lines = ['## Executable evidence', ''];
  for (const id of rule['sample-cases']) {
    const item = caseById.get(id);
    lines.push(`- ${id} — ${item.title}`);
    for (const evidence of item.evidence.slice(0, 4)) {
      lines.push(mode === 'links'
        ? `  - [${evidence}](../../examples/SampleProjectManagement/${evidence.replaceAll('\\', '/')})`
        : `  - \`${evidence.replaceAll('\\', '/')}\``);
    }
  }
  return lines.join('\n');
}

export function renderRuleCollection(title, intro, rules, registry, evidenceMode = 'paths', evidenceCatalogPath) {
  const contents = rules.map(rule => {
    return `- [${rule.title}](#${ruleAnchor(rule)})`;
  }).join('\n');
  const sections = rules.map(rule => `${ruleHeading(rule)}${rule.body}\n\n${sampleEvidence(rule, registry, evidenceMode, evidenceCatalogPath)}`).join('\n\n---\n\n');
  return `<!-- Generated by tools/guidance/generate-guidance.mjs. Do not edit by hand. -->\n\n# ${title}\n\n${intro}\n\n## Contents\n\n${contents}\n\n${sections}\n`;
}

export function groupRulesByReference(rules) {
  return Map.groupBy(rules, rule => rule.reference);
}

export function renderGuideIndex(groups, rules) {
  const items = [...groups.keys()].sort().map(reference => {
    const matching = groups.get(reference);
    return `- [${reference}](./${reference}.md) — ${matching.length} ${matching.length === 1 ? 'rule' : 'rules'}`;
  }).join('\n');
  return `<!-- Generated by tools/guidance/generate-guidance.mjs. Do not edit by hand. -->\n\n# NewHeap consumer guide\n\nThis guide turns the public NewHeap surface and executable SampleProjectManagement cases into prescriptive rules for consumer applications. The same self-contained rules are bundled behind one automatically discoverable NewHeap Platform skill with focused internal modules.\n\n## Install in a consumer\n\nFor a reproducible, repository-pinned Codex installation, run this from the Platform repository:\n\n\`\`\`text\nnode tools/guidance/install-consumer-skills.mjs --consumer <consumer-root>\n\`\`\`\n\nThe default target is \`codex\`, which writes the complete suite under \`.agents/skills/newheap-platform-development\`. Use \`--target claude\` for \`.claude/skills/newheap-platform-development\`, or \`--target both\` for a repository used with both agents. Commit that single managed directory, including its \`.newheap-platform-install.json\`. Check for updates with the same target and the \`--check\` flag. The installer safely migrates the earlier flat \`newheap-*\` layout after confirming those managed files were not changed locally. The \`newheap-platform\` plugin publishes the same provider-neutral suite and a standalone installer, so a consumer does not need a Platform checkout. Anonymous npm/NuGet sources and upgrade cutover are documented in [Consume public packages](../how-to/consume-public-packages.md).\n\n## Topics\n\n${items}\n\n## Maintenance\n\nEdit the atomic files under \`guidance/rules\`, update the linked sample case, and run \`npm run guidance:generate\` and \`npm run guidance:validate\`. There are ${rules.length} validated rules.\n`;
}

export function renderLlmsIndex(groups) {
  return `# NewHeap consumer guidance\n\n${[...groups.keys()].sort().map(reference => `- ${reference}: ./${reference}.md`).join('\n')}\n- consumer router skill: ../../skills/${consumerSkillBundleName}/SKILL.md\n${consumerSkillNames.map(name => `- consumer module: ../../skills/${name}/SKILL.md`).join('\n')}\n- maintenance skill: ../../skills/newheap-library-maintenance/SKILL.md\n`;
}

export async function packageVersions() {
  const releaseManifest = await readJson(resolve(repositoryRoot, 'release', 'manifest.json'));
  const versions = {};
  for (const unit of Object.values(releaseManifest.units)) {
    if (unit.kind === 'npm') versions[unit.packageName] = unit.version;
    if (unit.kind === 'nuget') {
      for (const project of unit.projects) versions[project.packageId] = unit.version;
    }
  }
  return Object.fromEntries(Object.entries(versions).sort(([left], [right]) => left.localeCompare(right)));
}
