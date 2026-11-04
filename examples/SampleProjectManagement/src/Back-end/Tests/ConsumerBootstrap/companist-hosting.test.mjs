import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, dirname, basename } from 'node:path';
import { validateCompanistHosting, managedAuthConfiguration } from '../../../../../../skills/newheap-consumer-development/scripts/validate-companist-hosting.mjs';

const root = await mkdtemp(resolve(tmpdir(), 'newheap-hosted-contract-'));
try {
  assert.deepEqual(await validateCompanistHosting(root), []); // Other hosting providers are unaffected.
  await mkdir(resolve(root, '.companist'));
  const plan = { schemaVersion: 2, runtime: 'ubuntu-docker-ansible-v1', architecture: 'amd64', buildPack: 'dockercompose',
    baseDirectory: '/', composeFile: '/compose.json', publicService: 'web', requiredSecrets: ['COMPANIST_AUTH_CLIENT_SECRET'],
    requiredConfiguration: managedAuthConfiguration, companistAuth: { callbackPath: '/signin-oidc',
      permissions: [{ name: 'example.access', description: 'Open the application.' }] } };
  const compose = { services: { web: { healthcheck: { test: ['CMD', 'true'] }, environment: Object.fromEntries(
    [...managedAuthConfiguration, 'COMPANIST_AUTH_CLIENT_SECRET'].map((name, i) => ['Identity__Setting' + i, '${' + name + '}'])) } } };
  async function check(candidate, recipe = compose) {
    await writeFile(resolve(root, '.companist/hosting.json'), JSON.stringify(candidate));
    await writeFile(resolve(root, 'compose.json'), JSON.stringify(recipe));
    return validateCompanistHosting(root);
  }
  assert.deepEqual(await check(plan), []);
  for (const callbackPath of ['//other.example.test/callback', '/callback?x=1', '/a/../callback']) {
    assert.ok((await check({ ...plan, companistAuth: { ...plan.companistAuth, callbackPath } })).length);
  }
  assert.ok((await check({ ...plan, requiredConfiguration: [] })).length);
  assert.ok((await check({ ...plan, generatedSecrets: ['COMPANIST_AUTH_CLIENT_SECRET'] })).length);
  assert.ok((await check({ ...plan, companistAuth: { ...plan.companistAuth, permissions: [null] } })).length);
  assert.ok((await check({ ...plan, composeFile: '/../outside.json' })).length);
  for (const value of ['${UNDECLARED}', '${COMPANIST_AUTH_CLIENT_SECRET:-fallback}', 'prefix${COMPANIST_AUTH_CLIENT_SECRET}']) {
    const bad = structuredClone(compose);
    bad.services.web.environment.Identity__Setting0 = value;
    assert.ok((await check(plan, bad)).length);
  }
  const missing = structuredClone(compose);
  delete missing.services.web.environment.Identity__Setting0;
  assert.ok((await check(plan, missing)).length);
  console.log('Companist hosting contract scenarios passed.');
} finally {
  assert.equal(dirname(resolve(root)), resolve(tmpdir()));
  assert.ok(basename(root).startsWith('newheap-hosted-contract-'));
  await rm(root, { recursive: true, force: true });
}
