import { access, readFile, readdir } from 'node:fs/promises';
import { relative, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import {
  consumerSkillBundleName,
  consumerSkillBundleRoot,
  consumerSkillNames,
  consumerSkillRoots,
  loadRegistry,
  loadRules,
  maintenanceSkillRoot,
  repositoryRoot,
  sampleRoot,
  validateRegistry,
  validateRules
} from './lib.mjs';

const registry = await loadRegistry();
const rules = await loadRules();
const failures = [...validateRegistry(registry), ...validateRules(rules, registry)];
const sourceCache = new Map();
const ignoredSourceDirectories = new Set([
  '.codegraph',
  '.git',
  '.idea',
  '.vs',
  'bin',
  'coverage',
  'dist',
  'node_modules',
  'obj',
  'tmp'
]);

const dutchNarrativePattern = /\b(?:voorkeursmethode|vermijd|verificatie|deze|hiermee|wordt|worden|moet|moeten|gebruik|gebruiken|houd|alleen|zonder|daarna|controleer|voorbeeld|voorbeelden|vertaling|vertalingen|aanmaken|verwijderen|wijzigen|gebruiker|gebruikers|autorisatie|authenticatie|inloggen|onderhoud|uitvoerbaar|gedrag|huidige|bestaande|fout|fouten|rechten|bewerking|bewerkingen|geconstateerde|vervolg|toetsbaar|besluit|gaten|relaties|meerdere|transactionele|bulkopties|bulkresultaat|deduplicatie|rollen|expliciete|actieve|auditvelden|configuratieproviders)\b/giu;

function validateEnglishNarrative(value, label) {
  const matches = [...value.matchAll(dutchNarrativePattern)];
  for (const match of matches) {
    const line = value.slice(0, match.index).split(/\r?\n/).length;
    failures.push(`${label}:${line}: documentation and AI guidance must be English; found "${match[0]}"`);
  }
}

async function validateMarkdownLanguage(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.isSymbolicLink() || ignoredSourceDirectories.has(entry.name)) continue;
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      await validateMarkdownLanguage(path);
    } else if (entry.isFile() && entry.name.endsWith('.md')) {
      validateEnglishNarrative(await readFile(path, 'utf8'), relative(repositoryRoot, path));
    }
  }
}

const impactExceptions = JSON.parse(await readFile(resolve(repositoryRoot, 'guidance', 'impact-exceptions.json'), 'utf8'));
if (impactExceptions.schemaVersion !== 1 || !Array.isArray(impactExceptions.exceptions)) failures.push('Invalid impact exception schema.');
for (const item of impactExceptions.exceptions ?? []) {
  if (!item.base || !item.owner || !item.rationale || !/^\d{4}-\d{2}-\d{2}$/.test(item.expiresOn ?? '')) {
    failures.push('Every impact exception requires base, owner, rationale and an ISO expiresOn date.');
  }
  if (!Array.isArray(item.pathPrefixes) || item.pathPrefixes.length === 0) {
    failures.push('Every impact exception requires one or more scoped pathPrefixes.');
  }
}

await validateMarkdownLanguage(repositoryRoot);
for (const category of registry.categories) {
  validateEnglishNarrative(category.title, `sample-case-registry category ${category.id}`);
}
for (const item of registry.cases) {
  validateEnglishNarrative(item.title, `${item.id} title`);
  validateEnglishNarrative(item.surface, `${item.id} surface`);
  validateEnglishNarrative(item.outcome, `${item.id} outcome`);
  if (item.statusReason) validateEnglishNarrative(item.statusReason, `${item.id} statusReason`);
}

const skillEvals = JSON.parse(await readFile(resolve(repositoryRoot, 'skill-evals', 'evals.json'), 'utf8'));
for (const item of skillEvals.evals ?? []) {
  validateEnglishNarrative(item.prompt ?? '', `skill-evals ${item.id} prompt`);
  validateEnglishNarrative(item.expectedOutcome ?? '', `skill-evals ${item.id} expectedOutcome`);
}
validateEnglishNarrative(
  await readFile(resolve(repositoryRoot, 'docs', 'consumer-guide', 'llms.txt'), 'utf8'),
  'docs/consumer-guide/llms.txt'
);

for (const metadataPath of [
  resolve(consumerSkillBundleRoot, 'agents', 'openai.yaml'),
  ...consumerSkillNames.map(name => resolve(consumerSkillRoots.get(name), 'agents', 'openai.yaml')),
  resolve(maintenanceSkillRoot, 'agents', 'openai.yaml'),
  resolve(sampleRoot, 'skills', 'sample-project-management-development', 'agents', 'openai.yaml')
]) {
  validateEnglishNarrative(await readFile(metadataPath, 'utf8'), relative(repositoryRoot, metadataPath));
}

for (const item of registry.cases) {
  for (const evidence of item.evidence) {
    try { await access(resolve(sampleRoot, evidence)); }
    catch { failures.push(`${item.id}: missing evidence ${evidence}`); }
  }
}

async function searchableSourceFallback(directory) {
  const source = [];

  async function visit(currentDirectory) {
    const entries = await readdir(currentDirectory, { withFileTypes: true });
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue;
      const path = resolve(currentDirectory, entry.name);
      if (entry.isDirectory()) {
        if (!ignoredSourceDirectories.has(entry.name)) await visit(path);
        continue;
      }
      if (entry.isFile() && (entry.name.endsWith('.cs') || entry.name.endsWith('.ts'))) {
        source.push(await readFile(path, 'utf8'));
      }
    }
  }

  await visit(resolve(repositoryRoot, directory));
  return source.join('\n');
}

async function searchableSource(directory) {
  if (sourceCache.has(directory)) return sourceCache.get(directory);
  const result = spawnSync('rg', ['--no-heading', '--color', 'never', '--glob', '*.cs', '--glob', '*.ts', '.', directory], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    maxBuffer: 20 * 1024 * 1024
  });
  let source;
  if (result.error?.code === 'ENOENT') {
    source = await searchableSourceFallback(directory);
  } else {
    if (result.status !== 0 && result.status !== 1) {
      throw new Error(result.error?.message || result.stderr || 'rg failed while validating public symbols.');
    }
    source = result.stdout ?? '';
  }
  sourceCache.set(directory, source);
  return source;
}

const librarySource = await searchableSource('src');
for (const rule of rules) {
  for (const symbol of rule['public-symbols']) {
    if (!librarySource.includes(symbol)) failures.push(`${relative(repositoryRoot, rule.sourcePath)}: public symbol not found in src: ${symbol}`);
  }
}

async function validateSkill(skillRoot, expectedName) {
  const skillPath = resolve(skillRoot, 'SKILL.md');
  const source = await readFile(skillPath, 'utf8');
  const normalizedSource = source.replaceAll('\r\n', '\n');
  const lines = source.split(/\r?\n/);
  if (lines.length > 500) failures.push(`${relative(repositoryRoot, skillPath)} exceeds 500 lines`);
  if (!normalizedSource.startsWith(`---\nname: ${expectedName}\n`)) failures.push(`${relative(repositoryRoot, skillPath)} has invalid name/frontmatter`);
  if (/\[TODO|TODO:/.test(source)) failures.push(`${relative(repositoryRoot, skillPath)} contains TODO placeholders`);
  const references = [...source.matchAll(/\]\((?!https?:)([^)#]+\.md)(?:#[^)]+)?\)/g)].map(match => match[1]);
  for (const reference of references) {
    try { await access(resolve(skillRoot, reference)); }
    catch { failures.push(`${relative(repositoryRoot, skillPath)} references missing file ${reference}`); }
  }
  const metadata = await readFile(resolve(skillRoot, 'agents', 'openai.yaml'), 'utf8');
  if (!metadata.includes(`$${expectedName}`)) failures.push(`${expectedName}: openai.yaml default_prompt must mention the skill`);
}

for (const skillName of consumerSkillNames) await validateSkill(consumerSkillRoots.get(skillName), skillName);
await validateSkill(consumerSkillBundleRoot, consumerSkillBundleName);
await validateSkill(maintenanceSkillRoot, 'newheap-library-maintenance');

const generation = spawnSync(process.execPath, [resolve(repositoryRoot, 'tools', 'guidance', 'generate-guidance.mjs'), '--check'], {
  cwd: repositoryRoot,
  encoding: 'utf8'
});
if (generation.status !== 0) failures.push(generation.stderr || generation.stdout || 'Generated guidance check failed.');

const snapshot = spawnSync(process.execPath, [resolve(repositoryRoot, 'tools', 'guidance', 'snapshot-public-api.mjs'), '--check'], {
  cwd: repositoryRoot,
  encoding: 'utf8'
});
if (snapshot.status !== 0) failures.push(snapshot.stderr || snapshot.stdout || 'Public API snapshot check failed.');

const plugin = spawnSync(process.execPath, [resolve(repositoryRoot, 'tools', 'guidance', 'validate-plugin-distribution.mjs')], {
  cwd: repositoryRoot,
  encoding: 'utf8'
});
if (plugin.status !== 0) failures.push(plugin.stderr || plugin.stdout || 'Plugin distribution check failed.');

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log(`Validated ${registry.cases.length} sample cases, ${rules.length} guidance rules and ${consumerSkillNames.length + 2} skills.`);
