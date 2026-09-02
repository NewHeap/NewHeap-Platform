import { access, cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import {
  consumerSkillBundleName,
  consumerSkillModuleDirectories,
  consumerSkillNames,
  repositoryRoot
} from './lib.mjs';

const temporaryRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-'));
const claudeRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-claude-'));
const bothRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-both-'));
const schemaTwoMigrationRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-v2-'));
const schemaThreeMigrationRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-v3-'));
const legacyMigrationRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-v1-'));
const installer = resolve(repositoryRoot, 'tools', 'guidance', 'install-consumer-skills.mjs');

function run(root, args, expectedStatus = 0) {
  const result = spawnSync(process.execPath, [installer, '--consumer', root, ...args], {
    cwd: repositoryRoot,
    encoding: 'utf8'
  });
  if (result.status !== expectedStatus) {
    throw new Error(result.stderr || result.stdout || `Installer exited with ${result.status}; expected ${expectedStatus}.`);
  }
  return result;
}

async function assertExists(path, message) {
  try { await access(path); } catch { throw new Error(message); }
}

async function assertMissing(path, message) {
  try { await access(path); throw new Error(message); } catch (error) {
    if (error?.code !== 'ENOENT') throw error;
  }
}

function targetPaths(root, directory) {
  const skillsRoot = resolve(root, directory, 'skills');
  const bundleRoot = resolve(skillsRoot, consumerSkillBundleName);
  return {
    skillsRoot,
    bundleRoot,
    lockPath: resolve(bundleRoot, '.newheap-platform-install.json')
  };
}

async function convertGroupedToFlat(root, schemaVersion) {
  const { skillsRoot, bundleRoot, lockPath } = targetPaths(root, '.agents');
  const groupedLock = JSON.parse(await readFile(lockPath, 'utf8'));
  const flatFiles = {};
  for (const moduleName of consumerSkillNames) {
    const moduleDirectory = consumerSkillModuleDirectories.get(moduleName);
    const source = resolve(bundleRoot, 'skills', moduleDirectory);
    const destination = resolve(skillsRoot, moduleName);
    await cp(source, destination, { recursive: true });
    const prefix = `skills/${moduleDirectory}/`;
    for (const [name, hash] of Object.entries(groupedLock.files)) {
      if (name.startsWith(prefix)) flatFiles[`${moduleName}/${name.slice(prefix.length)}`] = hash;
    }
  }
  await rm(bundleRoot, { recursive: true, force: true });
  const flatLock = {
    schemaVersion,
    target: 'codex',
    repositoryTarget: '.agents/skills',
    skills: consumerSkillNames,
    pluginVersion: groupedLock.pluginVersion,
    guidanceVersion: groupedLock.guidanceVersion,
    skillContentHash: groupedLock.skillContentHash,
    compatiblePackages: groupedLock.compatiblePackages,
    evidence: groupedLock.evidence,
    source: groupedLock.source,
    files: flatFiles
  };
  if (schemaVersion === 2) {
    delete flatLock.target;
    delete flatLock.repositoryTarget;
  }
  await writeFile(resolve(skillsRoot, '.newheap-platform-install.json'), `${JSON.stringify(flatLock, null, 2)}\n`);
}

async function convertGroupedToLegacy(root) {
  const { skillsRoot, bundleRoot, lockPath } = targetPaths(root, '.agents');
  const groupedLock = JSON.parse(await readFile(lockPath, 'utf8'));
  const moduleName = 'newheap-consumer-development';
  const moduleDirectory = consumerSkillModuleDirectories.get(moduleName);
  const destination = resolve(skillsRoot, moduleName);
  await cp(resolve(bundleRoot, 'skills', moduleDirectory), destination, { recursive: true });
  const prefix = `skills/${moduleDirectory}/`;
  const files = Object.fromEntries(Object.entries(groupedLock.files)
    .filter(([name]) => name.startsWith(prefix))
    .map(([name, hash]) => [name.slice(prefix.length), hash]));
  await rm(bundleRoot, { recursive: true, force: true });
  await writeFile(resolve(destination, '.newheap-skill-install.json'), `${JSON.stringify({
    schemaVersion: 1,
    pluginVersion: groupedLock.pluginVersion,
    guidanceVersion: groupedLock.guidanceVersion,
    skillContentHash: groupedLock.skillContentHash,
    compatiblePackages: groupedLock.compatiblePackages,
    evidence: groupedLock.evidence,
    files
  }, null, 2)}\n`);
}

async function assertGroupedInstall(root, directory, targetName) {
  const { skillsRoot, bundleRoot, lockPath } = targetPaths(root, directory);
  await assertExists(resolve(bundleRoot, 'SKILL.md'), `Installer omitted ${targetName} router skill.`);
  await assertExists(resolve(bundleRoot, 'agents', 'openai.yaml'), `Installer omitted ${targetName} router metadata.`);
  for (const skillName of consumerSkillNames) {
    const moduleDirectory = consumerSkillModuleDirectories.get(skillName);
    await assertExists(resolve(bundleRoot, 'skills', moduleDirectory, 'SKILL.md'), `Installer omitted ${targetName} module ${skillName}.`);
  }
  const lock = JSON.parse(await readFile(lockPath, 'utf8'));
  if (lock.schemaVersion !== 4 || lock.target !== targetName
    || lock.repositoryTarget !== `${directory}/skills/${consumerSkillBundleName}`
    || lock.skill !== consumerSkillBundleName
    || lock.evidence?.catalog !== `skills/${consumerSkillBundleName}/references/immutable-evidence.md`
    || !lock.evidence?.sourceRef?.startsWith('newheap-platform-plugin-v')) {
    throw new Error(`${targetName} installation metadata does not identify the grouped discovery target.`);
  }
  await assertExists(
    resolve(bundleRoot, 'references', 'immutable-evidence.md'),
    `Installer omitted ${targetName} immutable-evidence catalog.`
  );
  await assertMissing(
    resolve(skillsRoot, '.newheap-platform-install.json'),
    `${targetName} installation left metadata outside the grouped skill directory.`
  );
  const topLevelNewHeapEntries = (await readdir(skillsRoot)).filter(name => name.startsWith('newheap-'));
  if (topLevelNewHeapEntries.join() !== consumerSkillBundleName) {
    throw new Error(`${targetName} installation leaked flat NewHeap skill directories: ${topLevelNewHeapEntries.join(', ')}`);
  }
}

try {
  run(temporaryRoot, []);
  run(temporaryRoot, ['--check']);
  await assertGroupedInstall(temporaryRoot, '.agents', 'codex');
  await assertMissing(resolve(temporaryRoot, '.claude'), 'Default Codex installation unexpectedly created a Claude skill root.');

  run(claudeRoot, ['--target', 'claude']);
  run(claudeRoot, ['--target', 'claude', '--check']);
  await assertGroupedInstall(claudeRoot, '.claude', 'claude');
  await assertMissing(resolve(claudeRoot, '.agents'), 'Claude-only installation unexpectedly created a Codex skill root.');

  run(bothRoot, ['--target', 'both']);
  run(bothRoot, ['--target', 'both', '--check']);
  await assertGroupedInstall(bothRoot, '.agents', 'codex');
  await assertGroupedInstall(bothRoot, '.claude', 'claude');
  const invalidTarget = run(bothRoot, ['--target', 'other'], 1);
  if (!invalidTarget.stderr.includes('Unsupported target: other')) throw new Error('Installer accepted an unsupported host target.');

  const consumerOwnedSkill = resolve(temporaryRoot, '.agents', 'skills', 'consumer-owned-skill');
  await mkdir(consumerOwnedSkill, { recursive: true });
  await writeFile(resolve(consumerOwnedSkill, 'SKILL.md'), 'consumer-owned change');
  await writeFile(resolve(
    temporaryRoot,
    '.agents',
    'skills',
    consumerSkillBundleName,
    'skills',
    consumerSkillModuleDirectories.get('newheap-media-development'),
    'local-change.txt'
  ), 'local change');
  const protectedUpdate = run(temporaryRoot, [], 1);
  if (!protectedUpdate.stderr.includes('Refusing to overwrite locally changed installed codex skill files')) {
    throw new Error('Installer failed for an unexpected reason while testing local-change protection.');
  }
  run(temporaryRoot, ['--force']);
  run(temporaryRoot, ['--check']);
  await assertExists(resolve(consumerOwnedSkill, 'SKILL.md'), 'Forced NewHeap replacement removed a consumer-owned skill.');

  run(schemaTwoMigrationRoot, []);
  await convertGroupedToFlat(schemaTwoMigrationRoot, 2);
  run(schemaTwoMigrationRoot, []);
  run(schemaTwoMigrationRoot, ['--check']);
  await assertGroupedInstall(schemaTwoMigrationRoot, '.agents', 'codex');

  run(schemaThreeMigrationRoot, []);
  await convertGroupedToFlat(schemaThreeMigrationRoot, 3);
  run(schemaThreeMigrationRoot, []);
  run(schemaThreeMigrationRoot, ['--check']);
  await assertGroupedInstall(schemaThreeMigrationRoot, '.agents', 'codex');

  run(legacyMigrationRoot, []);
  await convertGroupedToLegacy(legacyMigrationRoot);
  run(legacyMigrationRoot, []);
  run(legacyMigrationRoot, ['--check']);
  await assertGroupedInstall(legacyMigrationRoot, '.agents', 'codex');

  console.log('Verified grouped Codex, Claude and combined installs, scoped replacement, local-change protection and flat v1/v2/v3 migration.');
} finally {
  await Promise.all([
    rm(temporaryRoot, { recursive: true, force: true }),
    rm(claudeRoot, { recursive: true, force: true }),
    rm(bothRoot, { recursive: true, force: true }),
    rm(schemaTwoMigrationRoot, { recursive: true, force: true }),
    rm(schemaThreeMigrationRoot, { recursive: true, force: true }),
    rm(legacyMigrationRoot, { recursive: true, force: true })
  ]);
}
