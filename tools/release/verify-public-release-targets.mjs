import { loadReleaseManifest, parseArguments, releasePackages } from './lib.mjs';

const options = parseArguments(process.argv.slice(2));
if (!options.component) {
  throw new Error('Usage: node tools/release/verify-public-release-targets.mjs --component <id|all> [--version <SemVer>] [--allow-missing] [--attempts <count>] [--retry-delay-ms <milliseconds>]');
}

function numericOption(name, defaultValue, minimum, maximum) {
  const value = options[name] === undefined ? defaultValue : Number(options[name]);
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new Error(`--${name} must be an integer between ${minimum} and ${maximum}.`);
  }
  return value;
}

const attempts = numericOption('attempts', 8, 1, 20);
const retryDelayMs = numericOption('retry-delay-ms', 2000, 0, 60000);
if (options.version && options.component === 'all') {
  throw new Error('--version cannot override multiple independently versioned release units.');
}
if (options['allow-missing'] && options['require-missing']) {
  throw new Error('--allow-missing and --require-missing cannot be combined.');
}

const manifest = await loadReleaseManifest();
const npmRegistry = (process.env.NPM_REGISTRY_URL ?? manifest.registries.npm).replace(/\/+$/, '');
const nugetFlatContainer = (process.env.NUGET_FLAT_CONTAINER_URL ?? 'https://api.nuget.org/v3-flatcontainer').replace(/\/+$/, '');

function packageUrl(releasePackage) {
  if (releasePackage.packageType === 'npm') {
    return `${npmRegistry}/${encodeURIComponent(releasePackage.packageName)}`;
  }
  const id = releasePackage.packageName.toLowerCase();
  return `${nugetFlatContainer}/${encodeURIComponent(id)}/index.json`;
}

async function registryJson(releasePackage) {
  const url = packageUrl(releasePackage);
  const response = await fetch(url, {
    headers: { Accept: 'application/json', 'User-Agent': 'NewHeap-public-release-check' }
  });
  if (response.status === 404) return null;
  if (!response.ok) {
    throw new Error(`Anonymous ${releasePackage.packageType} registry request for ${releasePackage.packageName} returned ${response.status}.`);
  }
  return response.json();
}

function publishedVersions(releasePackage, metadata) {
  if (releasePackage.packageType === 'npm') return Object.keys(metadata.versions ?? {});
  return Array.isArray(metadata.versions) ? metadata.versions : [];
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

const packageTargets = releasePackages(manifest, options.component);
let checked = 0;
for (const releasePackage of packageTargets) {
  const expectedVersion = options.version ?? releasePackage.version;
  if (options['require-missing']) {
    const metadata = await registryJson(releasePackage);
    if (metadata !== null) {
      throw new Error(`Public ${releasePackage.packageType} package ${releasePackage.packageName} already exists; bootstrap publication requires an unused package name.`);
    }
    checked += 1;
    continue;
  }
  if (options['allow-missing']) {
    const metadata = await registryJson(releasePackage);
    if (metadata === null) continue;
    checked += 1;
    continue;
  }

  let verified = false;
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    const metadata = await registryJson(releasePackage);
    verified = publishedVersions(releasePackage, metadata ?? {})
      .some(version => version.toLowerCase() === expectedVersion.toLowerCase());
    if (verified) break;
    if (attempt < attempts) {
      const wait = Math.min(retryDelayMs * (2 ** (attempt - 1)), 30000);
      console.log(`Waiting ${wait}ms for public ${releasePackage.packageType} package ${releasePackage.packageName} ${expectedVersion} to become visible (attempt ${attempt}/${attempts}).`);
      await delay(wait);
    }
  }

  if (!verified) {
    throw new Error(`Public ${releasePackage.packageType} package ${releasePackage.packageName} version ${expectedVersion} was not found after ${attempts} attempt(s).`);
  }
  checked += 1;
}

const description = options['require-missing']
  ? 'unused public package name(s)'
  : options['allow-missing']
    ? 'existing anonymously readable package target(s)'
    : 'exact public package version(s)';
console.log(`Verified ${checked} ${description} for ${options.component}.`);
