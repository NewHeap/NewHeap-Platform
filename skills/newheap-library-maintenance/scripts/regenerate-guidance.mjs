import { spawnSync } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
for (const [script, args] of [
  ['generate-guidance.mjs', []],
  ['snapshot-public-api.mjs', []],
  ['validate-guidance.mjs', []]
]) {
  const result = spawnSync(process.execPath, [resolve(root, 'tools', 'guidance', script), ...args], { cwd: root, stdio: 'inherit' });
  if (result.status !== 0) process.exit(result.status ?? 1);
}
