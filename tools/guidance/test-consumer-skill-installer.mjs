import { access, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { consumerSkillNames, repositoryRoot } from './lib.mjs';

const temporaryRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-'));
const migrationRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skills-migration-'));
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

try {
  run(temporaryRoot, []);
  run(temporaryRoot, ['--check']);
  for (const skillName of consumerSkillNames) {
    await assertExists(resolve(temporaryRoot, '.agents', 'skills', skillName, 'SKILL.md'), `Installer omitted ${skillName}.`);
  }

  const consumerOwnedSkill = resolve(temporaryRoot, '.agents', 'skills', 'consumer-owned-skill');
  await mkdir(consumerOwnedSkill, { recursive: true });
  await writeFile(resolve(consumerOwnedSkill, 'SKILL.md'), 'consumer-owned change');
  await writeFile(resolve(temporaryRoot, '.agents', 'skills', 'newheap-media-development', 'local-change.txt'), 'local change');
  const protectedUpdate = run(temporaryRoot, [], 1);
  if (!protectedUpdate.stderr.includes('Refusing to overwrite locally changed installed skill files')) {
    throw new Error('Installer failed for an unexpected reason while testing local-change protection.');
  }
  run(temporaryRoot, ['--force']);
  run(temporaryRoot, ['--check']);
  await assertExists(resolve(consumerOwnedSkill, 'SKILL.md'), 'Forced NewHeap replacement removed a consumer-owned skill.');

  run(migrationRoot, []);
  const skillsRoot = resolve(migrationRoot, '.agents', 'skills');
  const suiteLockPath = resolve(skillsRoot, '.newheap-platform-install.json');
  const suiteLock = JSON.parse(await readFile(suiteLockPath, 'utf8'));
  const legacyPrefix = 'newheap-consumer-development/';
  const legacyFiles = Object.fromEntries(Object.entries(suiteLock.files)
    .filter(([name]) => name.startsWith(legacyPrefix))
    .map(([name, hash]) => [name.slice(legacyPrefix.length), hash]));
  for (const skillName of await readdir(skillsRoot)) {
    if (skillName.startsWith('newheap-') && skillName !== 'newheap-consumer-development') {
      await rm(resolve(skillsRoot, skillName), { recursive: true, force: true });
    }
  }
  await rm(suiteLockPath);
  await writeFile(resolve(skillsRoot, 'newheap-consumer-development', '.newheap-skill-install.json'), `${JSON.stringify({
    schemaVersion: 1,
    pluginVersion: suiteLock.pluginVersion,
    guidanceVersion: suiteLock.guidanceVersion,
    skillContentHash: suiteLock.skillContentHash,
    files: legacyFiles
  }, null, 2)}\n`);
  run(migrationRoot, []);
  run(migrationRoot, ['--check']);

  console.log('Verified skill-suite install, drift check, v1 migration, local-change protection and scoped forced replacement.');
} finally {
  await Promise.all([
    rm(temporaryRoot, { recursive: true, force: true }),
    rm(migrationRoot, { recursive: true, force: true })
  ]);
}
