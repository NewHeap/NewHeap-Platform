import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import {
  consumerSkillNames,
  consumerSkillRoots,
  groupSkillRulesByReference,
  loadRegistry,
  loadRules,
  repositoryRoot,
  unlinkedSkillReferenceFailures
} from './lib.mjs';

const environmentWithoutPath = Object.fromEntries(
  Object.entries(process.env).filter(([key]) => key.toLowerCase() !== 'path')
);
environmentWithoutPath.PATH = '';

const validator = resolve(repositoryRoot, 'tools', 'guidance', 'validate-guidance.mjs');
const [registry, rules] = await Promise.all([loadRegistry(), loadRules()]);

const exampleRouter = [
  '---',
  'name: example-module',
  '---',
  '',
  'Read [alpha](references/alpha.md) for one task and [beta](./references/beta.md#section) for another.',
  'The file `references/gamma.md` is only mentioned, and [delta](https://example.com/references/delta.md) is external.'
].join('\n');
assert.deepEqual(unlinkedSkillReferenceFailures('skills/example-module/SKILL.md', exampleRouter, ['delta', 'beta', 'alpha', 'gamma']), [
  'skills/example-module/SKILL.md does not link its generated reference references/delta.md; link it with a short note on when to read it',
  'skills/example-module/SKILL.md does not link its generated reference references/gamma.md; link it with a short note on when to read it'
]);

const backendRouterPath = resolve(consumerSkillRoots.get('newheap-backend-development'), 'SKILL.md');
const backendRouter = await readFile(backendRouterPath, 'utf8');
const backendReferences = [...groupSkillRulesByReference(rules, 'newheap-backend-development').keys()];
assert.ok(backendReferences.includes('ai-assets'), 'The backend module is expected to receive the ai-assets reference.');
assert.deepEqual(unlinkedSkillReferenceFailures('backend', backendRouter, backendReferences), []);

const routerWithoutAssets = backendRouter.replace(/^.*\(references\/ai-assets\.md\).*\r?\n/m, '');
assert.notEqual(routerWithoutAssets, backendRouter, 'The backend router is expected to link references/ai-assets.md on a line of its own.');
assert.deepEqual(unlinkedSkillReferenceFailures('backend', routerWithoutAssets, backendReferences), [
  'backend does not link its generated reference references/ai-assets.md; link it with a short note on when to read it'
]);
console.log('Verified that a module router must link every reference generated for its module.');

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
