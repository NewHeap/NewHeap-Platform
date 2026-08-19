import { spawnSync } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const result = spawnSync(process.execPath, [resolve(root, 'tools', 'guidance', 'verify-change-impact.mjs'), ...process.argv.slice(2)], {
  cwd: root,
  stdio: 'inherit'
});
process.exit(result.status ?? 1);
