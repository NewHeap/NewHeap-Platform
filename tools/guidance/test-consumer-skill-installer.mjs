import { spawnSync } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { repositoryRoot } from './lib.mjs';

const temporaryRoot = await mkdtemp(join(tmpdir(), 'newheap-consumer-skill-'));
const installer = resolve(repositoryRoot, 'tools', 'guidance', 'install-consumer-skill.mjs');

function run(args, expectedStatus = 0) {
  const result = spawnSync(process.execPath, [installer, '--consumer', temporaryRoot, ...args], {
    cwd: repositoryRoot,
    encoding: 'utf8'
  });
  if (result.status !== expectedStatus) {
    throw new Error(result.stderr || result.stdout || `Installer exited with ${result.status}; expected ${expectedStatus}.`);
  }
  return result;
}

try {
  run([]);
  run(['--check']);
  await writeFile(resolve(temporaryRoot, '.agents', 'skills', 'newheap-consumer-development', 'local-change.txt'), 'consumer-owned change');
  const protectedUpdate = run([], 1);
  if (!protectedUpdate.stderr.includes('Refusing to overwrite locally changed installed skill files')) {
    throw new Error('Installer failed for an unexpected reason while testing local-change protection.');
  }
  run(['--force']);
  run(['--check']);
  console.log('Verified consumer-skill install, drift check, local-change protection and forced replacement.');
} finally {
  await rm(temporaryRoot, { recursive: true, force: true });
}
