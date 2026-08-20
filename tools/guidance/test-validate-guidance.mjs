import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { consumerSkillNames, loadRegistry, loadRules, repositoryRoot } from './lib.mjs';

const environmentWithoutPath = Object.fromEntries(
  Object.entries(process.env).filter(([key]) => key.toLowerCase() !== 'path')
);
environmentWithoutPath.PATH = '';

const validator = resolve(repositoryRoot, 'tools', 'guidance', 'validate-guidance.mjs');
const [registry, rules] = await Promise.all([loadRegistry(), loadRules()]);
const expectedOutput = `Validated ${registry.cases.length} sample cases, ${rules.length} guidance rules and ${consumerSkillNames.length + 2} skills.`;
const result = spawnSync(process.execPath, [validator], {
  cwd: repositoryRoot,
  encoding: 'utf8',
  env: environmentWithoutPath
});

if (result.status !== 0) {
  throw new Error(result.stderr || result.stdout || `Guidance validator exited with ${result.status} without ripgrep.`);
}
if (!result.stdout.includes(expectedOutput)) {
  throw new Error(`Guidance validator returned unexpected output without ripgrep: ${result.stdout}`);
}

console.log('Verified guidance validation without ripgrep on PATH.');
