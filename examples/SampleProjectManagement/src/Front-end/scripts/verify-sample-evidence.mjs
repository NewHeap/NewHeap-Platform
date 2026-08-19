import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { extname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = resolve(fileURLToPath(new URL('.', import.meta.url)));
const verifierPath = fileURLToPath(import.meta.url);
const sampleRoot = resolve(scriptDirectory, '..', '..', '..');
const registry = JSON.parse(readFileSync(resolve(sampleRoot, 'docs/cases/sample-case-registry.json'), 'utf8'));
const knownIds = new Set(registry.cases.map(item => item.id));
const failures = [];
const ignoredDirectories = new Set(['node_modules', 'dist', 'bin', 'obj', '.git']);
const checkedExtensions = new Set(['.cs', '.html', '.json', '.md', '.mjs', '.scss', '.ts']);
const forbiddenNames = [
  ['dock', 'ly'].join(''),
  ['o', 'pg'].join(''),
  ['o', 'pg-platform'].join('')
];
const escapedNames = forbiddenNames.map(value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
const forbiddenSampleReference = new RegExp(
  `\\b(?:${escapedNames.join('|')})\\b|${['t', 'mp'].join('')}[\\\\/]`,
  'i'
);

function verifyNoExternalSampleReferences(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ignoredDirectories.has(entry.name)) continue;
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      verifyNoExternalSampleReferences(path);
      continue;
    }
    if (entry.name.endsWith('.orig')) {
      failures.push('Unresolved merge artifact: ' + relative(sampleRoot, path));
      continue;
    }
    if (!checkedExtensions.has(extname(entry.name))) continue;
    if (path === verifierPath) continue;
    if (forbiddenSampleReference.test(readFileSync(path, 'utf8'))) {
      failures.push('External sample-project reference: ' + relative(sampleRoot, path));
    }
  }
}

const implemented = registry.cases.filter(item => item.implementation === 'implemented');
for (const item of implemented) {
  const { id, evidence = [] } = item;
  if (evidence.length === 0) failures.push(id + ' has no evidence path');
  for (const path of evidence) {
    if (!existsSync(resolve(sampleRoot, path))) failures.push(id + ' references missing evidence: ' + path);
  }
}

if (knownIds.size !== registry.cases.length) failures.push('Duplicate case ids in the canonical registry.');

verifyNoExternalSampleReferences(sampleRoot);

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log('Verified evidence for ' + implemented.length + ' implemented cases.');
