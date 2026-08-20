import { gunzipSync, inflateRawSync } from 'node:zlib';
import { readFile, readdir } from 'node:fs/promises';
import { basename, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  loadReleaseManifest,
  parseArguments,
  releaseUnit,
  resolveRepositoryPath
} from './lib.mjs';

const localOrSensitivePatterns = [
  ['Sentry project-directory metadata', /Sentry\.ProjectDirectory/i],
  ['Windows user-profile path', /[A-Z]:\\Users\\[^\\\x00]+/i],
  ['Unix user-profile path', /\/(?:Users|home)\/[^/\x00]+\//i],
  ['Azure Artifacts feed', /pkgs\.dev\.azure\.com/i],
  ['GitHub token', /gh[pousr]_[A-Za-z0-9_]{20,}/],
  ['npm token', /npm_[A-Za-z0-9]{20,}/],
  ['AWS access-key id', /AKIA[0-9A-Z]{16}/],
  ['Sentry DSN', /https:\/\/[^\s/@:]+@[^\s/]+\.ingest(?:\.[^\s/]+)?\.sentry\.io\//i]
];

function findEndOfCentralDirectory(buffer) {
  const minimumOffset = Math.max(0, buffer.length - 65_557);
  for (let offset = buffer.length - 22; offset >= minimumOffset; offset -= 1) {
    if (buffer.readUInt32LE(offset) === 0x06054b50) return offset;
  }
  throw new Error('ZIP end-of-central-directory record was not found.');
}

export function readZipEntries(buffer) {
  const endOffset = findEndOfCentralDirectory(buffer);
  const entryCount = buffer.readUInt16LE(endOffset + 10);
  let centralOffset = buffer.readUInt32LE(endOffset + 16);
  const entries = [];

  for (let index = 0; index < entryCount; index += 1) {
    if (buffer.readUInt32LE(centralOffset) !== 0x02014b50) {
      throw new Error(`Invalid ZIP central-directory entry at offset ${centralOffset}.`);
    }
    const method = buffer.readUInt16LE(centralOffset + 10);
    const compressedSize = buffer.readUInt32LE(centralOffset + 20);
    const uncompressedSize = buffer.readUInt32LE(centralOffset + 24);
    const nameLength = buffer.readUInt16LE(centralOffset + 28);
    const extraLength = buffer.readUInt16LE(centralOffset + 30);
    const commentLength = buffer.readUInt16LE(centralOffset + 32);
    const localOffset = buffer.readUInt32LE(centralOffset + 42);
    const name = buffer.subarray(centralOffset + 46, centralOffset + 46 + nameLength).toString('utf8');

    if (buffer.readUInt32LE(localOffset) !== 0x04034b50) {
      throw new Error(`Invalid ZIP local-file header for ${name}.`);
    }
    const localNameLength = buffer.readUInt16LE(localOffset + 26);
    const localExtraLength = buffer.readUInt16LE(localOffset + 28);
    const dataOffset = localOffset + 30 + localNameLength + localExtraLength;
    const compressed = buffer.subarray(dataOffset, dataOffset + compressedSize);
    const data = method === 0
      ? Buffer.from(compressed)
      : method === 8
        ? inflateRawSync(compressed)
        : null;
    if (data === null) throw new Error(`${name}: unsupported ZIP compression method ${method}.`);
    if (data.length !== uncompressedSize) {
      throw new Error(`${name}: expected ${uncompressedSize} uncompressed bytes, found ${data.length}.`);
    }
    entries.push({ name, data });
    centralOffset += 46 + nameLength + extraLength + commentLength;
  }

  return entries;
}

export function readTarGzipEntries(buffer) {
  const archive = gunzipSync(buffer);
  const entries = [];
  let offset = 0;

  while (offset + 512 <= archive.length) {
    const header = archive.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) break;
    const name = header.subarray(0, 100).toString('utf8').replace(/\0.*$/s, '');
    const prefix = header.subarray(345, 500).toString('utf8').replace(/\0.*$/s, '');
    const path = prefix ? `${prefix}/${name}` : name;
    const sizeText = header.subarray(124, 136).toString('ascii').replace(/\0.*$/s, '').trim();
    const size = sizeText ? Number.parseInt(sizeText, 8) : 0;
    if (!Number.isFinite(size) || size < 0) throw new Error(`${path}: invalid TAR entry size.`);
    const dataOffset = offset + 512;
    if (dataOffset + size > archive.length) throw new Error(`${path}: TAR entry exceeds the archive length.`);
    entries.push({ name: path, data: Buffer.from(archive.subarray(dataOffset, dataOffset + size)) });
    offset = dataOffset + Math.ceil(size / 512) * 512;
  }

  return entries;
}

function xmlText(source, element) {
  const match = source.match(new RegExp(`<${element}(?:\\s[^>]*)?>([\\s\\S]*?)<\\/${element}>`, 'i'));
  return match?.[1]?.trim() ?? '';
}

function repositoryAttributes(source) {
  const match = source.match(/<repository\s+([^>]+?)\s*\/?\s*>/i);
  if (!match) return {};
  return Object.fromEntries([...match[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(([, key, value]) => [key, value]));
}

function scanPayload(fileName, entry, failures) {
  const text = entry.data.toString('latin1');
  for (const [label, pattern] of localOrSensitivePatterns) {
    if (pattern.test(text)) failures.push(`${fileName}:${entry.name}: contains ${label}.`);
  }
}

export function validateNugetArtifactEntries({ fileName, entries, packageId, version, symbolPackage }) {
  const failures = [];
  const names = entries.map(entry => entry.name);
  const normalizedNames = names.map(name => name.toLowerCase());
  if (new Set(normalizedNames).size !== normalizedNames.length) {
    failures.push(`${fileName}: contains duplicate case-insensitive archive paths.`);
  }
  if (names.some(name => /\.(?:cs|fs|vb)$/i.test(name))) {
    failures.push(`${fileName}: contains loose source files; public source must be resolved through Source Link.`);
  }
  for (const entry of entries) scanPayload(fileName, entry, failures);

  const nuspec = entries.find(entry => entry.name.toLowerCase().endsWith('.nuspec'));
  if (!nuspec) {
    failures.push(`${fileName}: has no .nuspec metadata.`);
  } else {
    const source = nuspec.data.toString('utf8');
    if (xmlText(source, 'id') !== packageId) failures.push(`${fileName}: package id does not match ${packageId}.`);
    if (xmlText(source, 'version') !== version) failures.push(`${fileName}: package version does not match ${version}.`);
    const repository = repositoryAttributes(source);
    if (repository.type !== 'git'
      || repository.url !== 'https://github.com/NewHeap/NewHeap-Platform'
      || !/^[0-9a-f]{40}$/i.test(repository.commit ?? '')) {
      failures.push(`${fileName}: repository URL and immutable commit metadata are required.`);
    }
    if (!symbolPackage) {
      const description = xmlText(source, 'description');
      if (!description || description === 'Package Description') failures.push(`${fileName}: package description is missing.`);
      if (!xmlText(source, 'tags')) failures.push(`${fileName}: package tags are missing.`);
      if (xmlText(source, 'icon') !== 'NH_logo.png') failures.push(`${fileName}: package icon metadata is missing.`);
      if (xmlText(source, 'readme') !== 'README.md') failures.push(`${fileName}: package README metadata is missing.`);
      if (xmlText(source, 'license') !== 'Apache-2.0') failures.push(`${fileName}: Apache-2.0 license metadata is missing.`);
    }
  }

  if (symbolPackage) {
    const pdbs = entries.filter(entry => entry.name.toLowerCase().endsWith('.pdb'));
    if (pdbs.length === 0) failures.push(`${fileName}: symbol package contains no PDB files.`);
    for (const pdb of pdbs) {
      if (pdb.data.subarray(0, 4).toString('ascii') !== 'BSJB') {
        failures.push(`${fileName}:${pdb.name}: symbol is not a managed Portable PDB.`);
      }
    }
  }

  return failures;
}

export function validateNpmArtifactEntries({ fileName, entries, packageName, version }) {
  const failures = [];
  const names = entries.map(entry => entry.name);
  if (names.some(name => /\.(?:tgz|tar\.gz)$/i.test(name))) {
    failures.push(`${fileName}: contains a nested package archive.`);
  }

  const packageJsonEntry = entries.find(entry => entry.name === 'package/package.json');
  if (!packageJsonEntry) {
    failures.push(`${fileName}: has no package/package.json metadata.`);
  } else {
    try {
      const packageJson = JSON.parse(packageJsonEntry.data.toString('utf8'));
      if (packageJson.name !== packageName) failures.push(`${fileName}: package name does not match ${packageName}.`);
      if (packageJson.version !== version) failures.push(`${fileName}: package version does not match ${version}.`);
    } catch (error) {
      failures.push(`${fileName}: package/package.json is invalid JSON: ${error.message}`);
    }
  }

  return failures;
}

export async function validatePackageArtifacts({ component, unit, version, outputDirectory }) {
  if (unit.kind === 'npm') {
    const failures = [];
    const names = (await readdir(outputDirectory)).filter(name => name !== 'SHA256SUMS').sort();
    const expectedName = `${unit.packageName.replace(/^@/, '').replaceAll('/', '-')}-${version}.tgz`;
    if (names.join('\n') !== expectedName) {
      failures.push(`${component}: expected artifact ${expectedName}, found ${names.join(', ')}.`);
    }
    try {
      const entries = readTarGzipEntries(await readFile(resolve(outputDirectory, expectedName)));
      failures.push(...validateNpmArtifactEntries({
        fileName: expectedName,
        entries,
        packageName: unit.packageName,
        version
      }));
    } catch (error) {
      failures.push(`${expectedName}: ${error.message}`);
    }
    if (failures.length > 0) throw new Error(failures.join('\n'));
    return;
  }
  if (unit.kind !== 'nuget') return;
  const failures = [];
  const names = (await readdir(outputDirectory)).filter(name => name !== 'SHA256SUMS').sort();
  const expectedNames = unit.projects.flatMap(project => [
    `${project.packageId}.${version}.nupkg`,
    ...(unit.includeSymbols ? [`${project.packageId}.${version}.snupkg`] : [])
  ]).sort();
  if (names.join('\n') !== expectedNames.join('\n')) {
    failures.push(`${component}: expected artifacts ${expectedNames.join(', ')}, found ${names.join(', ')}.`);
  }

  for (const project of unit.projects) {
    for (const symbolPackage of [false, ...(unit.includeSymbols ? [true] : [])]) {
      const fileName = `${project.packageId}.${version}.${symbolPackage ? 'snupkg' : 'nupkg'}`;
      try {
        const entries = readZipEntries(await readFile(resolve(outputDirectory, fileName)));
        failures.push(...validateNugetArtifactEntries({
          fileName,
          entries,
          packageId: project.packageId,
          version,
          symbolPackage
        }));
      } catch (error) {
        failures.push(`${fileName}: ${error.message}`);
      }
    }
  }

  if (failures.length > 0) throw new Error(failures.join('\n'));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const options = parseArguments(process.argv.slice(2));
  if (!options.component || !options.directory) {
    throw new Error('Usage: node tools/release/validate-package-artifacts.mjs --component <id> --directory <artifact-directory> [--version <semver>]');
  }
  const manifest = await loadReleaseManifest();
  const unit = releaseUnit(manifest, options.component);
  const version = options.version ?? unit.version;
  await validatePackageArtifacts({
    component: options.component,
    unit,
    version,
    outputDirectory: resolveRepositoryPath(options.directory)
  });
  console.log(`Validated ${basename(options.directory)} artifacts for ${options.component} ${version}.`);
}
