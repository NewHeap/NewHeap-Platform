import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const toolsDirectory = dirname(fileURLToPath(import.meta.url));
const sampleRoot = resolve(toolsDirectory, '..');
const requiredPaths = [
  'AGENTS.md',
  'CLAUDE.md',
  'CODEX.md',
  'docs/cases/sample-case-registry.json',
  'skills/sample-project-management-development/SKILL.md',
  'skills/sample-project-management-development/agents/openai.yaml',
  'src/Back-end/Directory.Build.props',
  'src/Back-end/Directory.Packages.props',
  'src/Back-end/SampleProjectManagement.slnx',
  'src/Back-end/Applications',
  'src/Back-end/Libraries',
  'src/Back-end/Orchestration',
  'src/Back-end/Tests',
  'src/Front-end'
];
const legacySourceDirectories = [
  'Applications',
  'Libraries',
  'Orchestration',
  'Tests',
  'Front-end'
];
const forbiddenRootFiles = [
  'Directory.Build.props',
  'Directory.Packages.props',
  'SampleProjectManagement.slnx',
  'angular.json',
  'proxy.conf.cjs',
  'tsconfig.json'
];
const ignoredSourceDirectories = new Set(['bin', 'dist', 'node_modules', 'obj']);
const narrativeSourceExtensions = new Set(['.cs', '.html', '.json', '.mjs', '.scss', '.ts']);
const dutchNarrativePattern = /\b(?:deze|hiermee|wordt|worden|moet|moeten|gebruik|gebruiken|houd|alleen|zonder|daarna|controleer|voorbeeld|voorbeelden|vertaling|vertalingen|aanmaken|verwijderen|wijzigen|gebruiker|gebruikers|autorisatie|authenticatie|inloggen|onderhoud|uitvoerbaar|gedrag|huidige|bestaande|fout|fouten|rechten|bewerking|bewerkingen|relaties|meerdere|transactionele|deduplicatie|rollen|expliciete|actieve|projecten|taken|omschrijving|afgerond|openstaand|mappen|bestanden|geladen|hernoemd|bijgewerkt|opgeslagen|verwijderd|kies|vul|voer|onverwacht|bevestigd|afwezig|bewuste|loopt|volgorde|waarom|bibliotheekvoorbeelden|sampleomgeving|ongelezen|vernieuwen|hernoem|verwijder|bestand|vertaalde|titel|zoek|gelezen|archiveren|archiveer|tekstoperator|volgende|vorige|dubbele|serverfout|geselecteerde|invalideer|invalidatie|inspecteer|navigatiemodel|paginastate|rollbackproef|projectsleutel|projectnaam|selecteer|deselecteer|geregistreerd|actief|veilig|uitgeschakeld|beschikbaar|configuratie|geldig|profiel|bewerken|navigatie|sluiten|openen)\b/giu;

function validateEnglishSampleSource(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isSymbolicLink() || ignoredSourceDirectories.has(entry.name)) continue;
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      validateEnglishSampleSource(path);
      continue;
    }

    const normalizedPath = path.replaceAll('\\', '/');
    const extension = entry.name.slice(entry.name.lastIndexOf('.'));
    if (!narrativeSourceExtensions.has(extension) ||
        entry.name === 'package-lock.json' ||
        normalizedPath.endsWith('/public/i18n/nl.json')) continue;

    const source = readFileSync(path, 'utf8');
    const matches = [...source.matchAll(dutchNarrativePattern)];
    for (const match of matches) {
      const line = source.slice(0, match.index).split(/\r?\n/).length;
      assert.fail(`${normalizedPath}:${line}: executable samples must provide English text; found "${match[0]}"`);
    }
  }
}

for (const path of requiredPaths) {
  assert.ok(existsSync(resolve(sampleRoot, path)), `Required sample path is missing: ${path}`);
}

for (const path of legacySourceDirectories) {
  assert.ok(!existsSync(resolve(sampleRoot, path)), `Legacy source directory must stay under src: ${path}`);
}

for (const path of forbiddenRootFiles) {
  assert.ok(!existsSync(resolve(sampleRoot, path)), `Workspace file must not be placed at the sample root: ${path}`);
}

const backendRoot = resolve(sampleRoot, 'src/Back-end');
const frontendRoot = resolve(sampleRoot, 'src/Front-end');
assert.ok(existsSync(resolve(frontendRoot, 'angular.json')), 'Angular workspace must live in src/Front-end.');
assert.ok(existsSync(resolve(frontendRoot, 'projects')), 'Angular projects must live in src/Front-end/projects.');

const directoryBuildProps = readFileSync(resolve(backendRoot, 'Directory.Build.props'), 'utf8');
assert.match(directoryBuildProps, /<TargetFramework>net10\.0<\/TargetFramework>/, 'Directory.Build.props must own the shared target framework.');
assert.match(directoryBuildProps, /<Nullable>enable<\/Nullable>/, 'Directory.Build.props must enable nullable reference types.');
assert.match(directoryBuildProps, /<IsPackable>false<\/IsPackable>/, 'Sample projects must be non-packable by default.');

const directoryPackagesProps = readFileSync(resolve(backendRoot, 'Directory.Packages.props'), 'utf8');
assert.match(
  directoryPackagesProps,
  /<ManagePackageVersionsCentrally>true<\/ManagePackageVersionsCentrally>/,
  'Directory.Packages.props must enable central package management.'
);
assert.match(directoryPackagesProps, /<PackageVersion\s+Include="[^"]+"\s+Version="[^"]+"\s*\/>/, 'Directory.Packages.props must declare package versions.');

validateEnglishSampleSource(resolve(sampleRoot, 'src'));

const solution = readFileSync(resolve(backendRoot, 'SampleProjectManagement.slnx'), 'utf8');
const projectPaths = [...solution.matchAll(/<Project Path="([^"]+)"/g)].map(match => match[1]);
assert.ok(projectPaths.length > 0, 'The sample solution does not contain projects.');
for (const path of projectPaths) {
  assert.ok(!path.startsWith('src/'), `Solution project paths must be relative to src/Back-end: ${path}`);
  assert.ok(existsSync(resolve(backendRoot, path)), `Solution project does not exist: ${path}`);
}

const appHostProgramPath = resolve(
  sampleRoot,
  'src/Back-end/Orchestration/SampleProjectManagement.AppHost/Program.cs'
);
const appHostProgram = readFileSync(appHostProgramPath, 'utf8');
const javaScriptAppPaths = [
  ...appHostProgram.matchAll(/\.AddJavaScriptApp\(\s*"[^"]+",\s*"([^"]+)"/g)
].map(match => match[1]);
assert.equal(javaScriptAppPaths.length, 2, 'The AppHost must register both Angular applications.');
for (const path of javaScriptAppPaths) {
  assert.ok(
    existsSync(resolve(dirname(appHostProgramPath), path)),
    `AppHost JavaScript application path does not exist: ${path}`
  );
}

const registry = JSON.parse(readFileSync(resolve(sampleRoot, 'docs/cases/sample-case-registry.json'), 'utf8'));
const allowedEvidencePrefixes = ['src/Back-end/', 'src/Front-end/', '../../src/'];
for (const sampleCase of registry.cases) {
  for (const evidence of sampleCase.evidence ?? []) {
    assert.ok(
      allowedEvidencePrefixes.some(prefix => evidence.startsWith(prefix)),
      `${sampleCase.id} has evidence outside the source layout: ${evidence}`
    );
    assert.ok(existsSync(resolve(sampleRoot, evidence)), `${sampleCase.id} references missing evidence: ${evidence}`);
  }
}

console.log(`Validated the SampleProjectManagement repository structure and ${registry.cases.length} sample cases.`);
