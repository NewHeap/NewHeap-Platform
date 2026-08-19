import { readFile, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import {
  bumpVersion,
  loadReleaseManifest,
  parseArguments,
  readJson,
  releaseManifestPath,
  releaseSelection,
  releaseTag,
  repositoryRoot,
  resolveRepositoryPath,
  writeJson
} from './lib.mjs';

const options = parseArguments(process.argv.slice(2));
if (!options.component || !options.bump) {
  throw new Error('Usage: node tools/release/prepare-release.mjs --component <id> --bump <major|minor|patch> [--dry-run]');
}

function runGuidanceTool(script, args, failureMessage) {
  const result = spawnSync(process.execPath, [resolve(repositoryRoot, 'tools', 'guidance', script), ...args], {
    cwd: repositoryRoot,
    stdio: 'inherit'
  });
  if (result.status !== 0) throw new Error(failureMessage);
}

const manifest = await loadReleaseManifest();
const releases = releaseSelection(manifest, options.component).map(({ id, unit }) => {
  const version = bumpVersion(unit.version, options.bump);
  return {
    component: id,
    unit,
    previousVersion: unit.version,
    version,
    tag: releaseTag(unit, version)
  };
});
const result = options.component === 'all'
  ? {
      component: 'all',
      releases: releases.map(({ component, previousVersion, version, tag }) => ({ component, previousVersion, version, tag }))
    }
  : (() => {
      const [{ component, previousVersion, version, tag }] = releases;
      return { component, previousVersion, version, tag };
    })();

if (options['dry-run']) {
  console.log(JSON.stringify(result));
  process.exit(0);
}

// Refuse to hide pre-existing public API drift inside an automated version
// commit. The post-bump snapshot below may then safely record the expected
// guidance version change made by a plugin or all-unit release.
runGuidanceTool(
  'snapshot-public-api.mjs',
  ['--check'],
  'Public API snapshot validation failed before version preparation.'
);

const jsonWrites = [];
const textWrites = [];

for (const { component, unit, previousVersion, version } of releases) {
  unit.version = version;

  if (unit.kind === 'npm') {
    const packagePath = resolveRepositoryPath(unit.packageJson);
    const packageJson = await readJson(packagePath);
    if (packageJson.version !== previousVersion) {
      throw new Error(`${unit.packageJson}: expected ${previousVersion}, found ${packageJson.version}.`);
    }
    packageJson.version = version;
    jsonWrites.push([packagePath, packageJson]);
  }

  if (unit.kind === 'plugin') {
    const guidancePath = resolve(repositoryRoot, 'guidance', 'version.json');
    const pluginPath = resolve(repositoryRoot, 'plugins', 'newheap-platform', '.codex-plugin', 'plugin.json');
    const guidance = await readJson(guidancePath);
    const plugin = await readJson(pluginPath);
    if (guidance.guidanceVersion !== previousVersion || plugin.version !== previousVersion) {
      throw new Error(`Plugin, guidance and release manifest must all start at ${previousVersion}.`);
    }
    guidance.guidanceVersion = version;
    plugin.version = version;
    jsonWrites.push([guidancePath, guidance], [pluginPath, plugin]);
  }

  if (component === 'nuget-common') {
    const versionsPath = resolve(repositoryRoot, 'src', 'Back-end', 'Directory.Packages.props');
    const source = await readFile(versionsPath, 'utf8');
    const pattern = /(<PackageVersion Include="NewHeap\.Platform\.Common" Version=")[^"]+("\s*\/?>)/;
    if (!pattern.test(source)) throw new Error('Directory.Packages.props has no NewHeap.Platform.Common package version.');
    textWrites.push([versionsPath, source.replace(pattern, `$1${version}$2`)]);
  }
}

// Validate every coupled version source before writing any of them. This keeps a
// failed preparation from leaving a partially bumped release in the worktree.
await writeJson(releaseManifestPath, manifest);
for (const [path, value] of jsonWrites) await writeJson(path, value);
for (const [path, value] of textWrites) await writeFile(path, value, 'utf8');

runGuidanceTool(
  'generate-guidance.mjs',
  [],
  'Guidance generation failed after version preparation.'
);
runGuidanceTool(
  'snapshot-public-api.mjs',
  [],
  'Public API snapshot generation failed after version preparation.'
);

console.log(JSON.stringify(result));
