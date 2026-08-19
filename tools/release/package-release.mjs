import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, rm } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { basename, relative, resolve } from 'node:path';
import {
  isPackageSemver,
  loadReleaseManifest,
  parseArguments,
  readJson,
  releaseUnit,
  repositoryRoot,
  resolveRepositoryPath,
  writeJson
} from './lib.mjs';
import { validatePackageArtifacts } from './validate-package-artifacts.mjs';

const options = parseArguments(process.argv.slice(2));
if (!options.component) {
  throw new Error('Usage: node tools/release/package-release.mjs --component <id> [--version <semver>] [--output <directory>] [--repository-url <url>] [--commit <sha>] [--dry-run]');
}

const manifest = await loadReleaseManifest();
const unit = releaseUnit(manifest, options.component);
const version = options.version ?? unit.version;
if (!isPackageSemver(version)) throw new Error(`Invalid package version: ${version}`);
const outputRelative = options.output ?? 'release-artifacts';
const outputDirectory = resolveRepositoryPath(outputRelative);
if (outputDirectory === repositoryRoot) throw new Error('The repository root cannot be used as release output.');
const repositoryUrl = options['repository-url'] ?? 'https://github.com/OWNER/REPOSITORY';
const repositoryCommit = options.commit ?? 'local';
const dryRun = Boolean(options['dry-run']);
const commands = [];
const npmCommand = process.platform === 'win32' ? 'npm.cmd' : 'npm';

function run(command, argumentsList, runOptions = {}) {
  commands.push({ command, arguments: argumentsList, cwd: relative(repositoryRoot, runOptions.cwd ?? repositoryRoot) || '.' });
  if (dryRun) return;
  const result = spawnSync(command, argumentsList, {
    cwd: runOptions.cwd ?? repositoryRoot,
    stdio: 'inherit',
    shell: false
  });
  if (result.status !== 0) throw new Error(`${command} failed with exit code ${result.status}.`);
}

if (!dryRun) {
  await rm(outputDirectory, { recursive: true, force: true });
  await mkdir(outputDirectory, { recursive: true });
}

if (unit.kind === 'nuget') {
  for (const project of unit.projects) {
    const properties = {
      PackageVersion: version,
      Version: version,
      RepositoryUrl: repositoryUrl,
      RepositoryCommit: repositoryCommit,
      PackageProjectUrl: repositoryUrl,
      ContinuousIntegrationBuild: 'true',
      UseLocalNewHeapProjects: 'false',
      ...(unit.includeSymbols ? { SymbolPackageFormat: 'snupkg' } : {}),
      ...(project.properties ?? {})
    };
    const propertyArguments = Object.entries(properties).map(([name, value]) => `/p:${name}=${String(value).replaceAll('{version}', version)}`);
    if (options['nuget-config']) {
      run('dotnet', [
        'restore',
        resolveRepositoryPath(project.path),
        '--configfile', resolve(options['nuget-config']),
        ...propertyArguments
      ]);
    }
    run('dotnet', [
      'pack',
      resolveRepositoryPath(project.path),
      '--configuration', 'Release',
      '--output', outputDirectory,
      ...(unit.includeSymbols ? ['--include-symbols'] : []),
      ...(options['nuget-config'] ? ['--no-restore'] : []),
      ...propertyArguments
    ]);
  }
}

if (unit.kind === 'npm') {
  const workspace = resolveRepositoryPath(unit.workspace);
  run(npmCommand, ['run', unit.buildScript], { cwd: workspace });
  if (!dryRun) {
    const distPackagePath = resolve(resolveRepositoryPath(unit.distDirectory), 'package.json');
    const distPackage = await readJson(distPackagePath);
    distPackage.version = version;
    distPackage.repository = { type: 'git', url: `git+${repositoryUrl}.git` };
    distPackage.publishConfig = { registry: manifest.registries.npm, access: manifest.packageVisibility };
    await writeJson(distPackagePath, distPackage);
  }
  run(npmCommand, ['pack', resolveRepositoryPath(unit.distDirectory), '--pack-destination', outputDirectory]);
}

if (unit.kind === 'plugin') {
  const archive = resolve(outputDirectory, `${unit.packageName}-${version}.tar.gz`);
  run('tar', ['-czf', archive, '-C', resolveRepositoryPath('plugins'), basename(resolveRepositoryPath(unit.directory))]);
}

if (!dryRun) {
  await validatePackageArtifacts({
    component: options.component,
    unit,
    version,
    outputDirectory
  });
  const names = (await readdir(outputDirectory)).filter(name => name !== 'SHA256SUMS').sort();
  if (names.length === 0) throw new Error(`No artifacts were produced for ${options.component}.`);
  const lines = [];
  for (const name of names) {
    const content = await readFile(resolve(outputDirectory, name));
    lines.push(`${createHash('sha256').update(content).digest('hex')}  ${name}`);
  }
  await import('node:fs/promises').then(({ writeFile }) => writeFile(resolve(outputDirectory, 'SHA256SUMS'), `${lines.join('\n')}\n`, 'utf8'));
}

console.log(JSON.stringify({ component: options.component, kind: unit.kind, version, output: outputRelative, commands }));
