import { createHash } from 'node:crypto';
import { readdir, readFile, writeFile } from 'node:fs/promises';
import { extname, relative, resolve } from 'node:path';
import { loadRules, readJson, repositoryRoot } from './lib.mjs';
import { backEndDeclarations, frontEndDeclarations } from './public-api-declarations.mjs';

const outputPath = resolve(repositoryRoot, 'guidance', 'public-api-snapshot.json');
const checkOnly = process.argv.includes('--check');
const ignored = new Set(['bin', 'obj', 'node_modules', 'dist']);

async function walk(directory) {
  const paths = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ignored.has(entry.name)) continue;
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) paths.push(...await walk(path));
    else paths.push(path);
  }
  return paths;
}

function normalizedPath(path) {
  return relative(repositoryRoot, path).replaceAll('\\', '/');
}

function normalizedText(content) {
  return content.replace(/\r\n?/g, '\n');
}

function hash(entries) {
  return createHash('sha256').update(entries.map(item => `${item.path}\0${item.declaration}`).join('\n')).digest('hex');
}

const backEndRoot = resolve(repositoryRoot, 'src', 'Back-end', 'Libraries');
const backEndEntries = [];
for (const path of (await walk(backEndRoot)).filter(path => extname(path) === '.cs').sort()) {
  for (const declaration of backEndDeclarations(await readFile(path, 'utf8'))) {
    backEndEntries.push({ path: normalizedPath(path), declaration });
  }
}

const frontEndProject = resolve(repositoryRoot, 'src', 'Front-end', 'projects', 'nh-common');
const frontEndRoots = [resolve(frontEndProject, 'src'), resolve(frontEndProject, 'two-factor', 'src')];
const frontEndEntries = [];
for (const path of (await Promise.all(frontEndRoots.map(root => walk(root)))).flat()
  .filter(path => extname(path) === '.ts' && !path.endsWith('.spec.ts'))
  .sort()) {
  for (const declaration of frontEndDeclarations(await readFile(path, 'utf8'))) {
    frontEndEntries.push({ path: normalizedPath(path), declaration });
  }
}

const rules = await loadRules();
const version = await readJson(resolve(repositoryRoot, 'guidance', 'version.json'));
const snapshot = {
  schemaVersion: 1,
  guidanceVersion: version.guidanceVersion,
  backEnd: { hash: hash(backEndEntries), declarationCount: backEndEntries.length, declarations: backEndEntries },
  frontEnd: { hash: hash(frontEndEntries), declarationCount: frontEndEntries.length, declarations: frontEndEntries },
  guidedSymbols: Object.fromEntries(rules.map(rule => [rule.id, rule['public-symbols']]))
};
const content = `${JSON.stringify(snapshot, null, 2)}\n`;

if (checkOnly) {
  let current;
  try { current = await readFile(outputPath, 'utf8'); } catch { current = undefined; }
  if (current === undefined || normalizedText(current) !== content) {
    throw new Error('Public API snapshot is stale. Run npm run guidance:snapshot and review the guidance impact.');
  }
  console.log(`Verified ${backEndEntries.length} backend and ${frontEndEntries.length} frontend public declarations.`);
} else {
  await writeFile(outputPath, content, 'utf8');
  console.log(`Snapshotted ${backEndEntries.length} backend and ${frontEndEntries.length} frontend public declarations.`);
}
