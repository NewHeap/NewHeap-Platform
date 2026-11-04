import { readFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';

export const managedAuthConfiguration = ['COMPANIST_AUTH_AUTHORITY', 'COMPANIST_AUTH_CLIENT_ID',
  'COMPANIST_TENANT_ID', 'COMPANIST_ENVIRONMENT_ID', 'COMPANIST_APPLICATION_ID', 'COMPANIST_DEPLOYMENT_ENVIRONMENT_ID'];

// A static contract check, never a deployment, credential reader or security approval.
export async function validateCompanistHosting(root) {
  const issues = [];
  const error = message => issues.push({ severity: 'error', code: 'companist-hosting', message });
  let plan;
  try {
    plan = JSON.parse(await readFile(resolve(root, '.companist/hosting.json'), 'utf8'));
  } catch (cause) {
    if (cause.code !== 'ENOENT') {
      error('The Companist hosting manifest must be valid JSON.');
    }
    return issues;
  }
  if (plan?.schemaVersion !== 2 || plan.runtime !== 'ubuntu-docker-ansible-v1' || plan.buildPack !== 'dockercompose' ||
      plan.architecture !== 'amd64' || plan.baseDirectory !== '/') {
    error('Use the supported Companist v2 Docker Compose contract.');
    return issues;
  }
  const lists = ['requiredSecrets', 'requiredConfiguration', 'optionalSecrets', 'optionalConfiguration'];
  if (!Array.isArray(plan.generatedSecrets ?? [])) {
    error('Generated secrets must be an array of runtime variable names.');
    return issues;
  }
  if (lists.some(key => !Array.isArray(plan[key] ?? []) || (plan[key] ?? []).some(name => typeof name !== 'string' || !/^[A-Z][A-Z0-9_]{0,127}$/.test(name)))) {
    error('Declare uppercase runtime variable names in arrays.');
    return issues;
  }
  const names = lists.flatMap(key => plan[key] ?? []);
  if (new Set(names).size !== names.length) {
    error('Runtime variable names must not be declared more than once.');
  }
  if (plan.companistAuth) {
    const auth = plan.companistAuth;
    if (typeof auth.callbackPath !== 'string' || !/^\/[A-Za-z0-9/_-]{1,199}$/.test(auth.callbackPath) || auth.callbackPath.includes('//')) {
      error('Companist Auth needs an exact rooted callback path, without a hostname, query or traversal.');
    }
    if (!Array.isArray(auth.permissions) || auth.permissions.length > 200 ||
        auth.permissions.some(p => !p || typeof p.name !== 'string' || p.name.length > 120 || !/^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*)+$/.test(p.name) ||
          typeof p.description !== 'string' || p.description.length > 500) ||
        new Set(auth.permissions.map(p => p.name)).size !== auth.permissions.length) {
      error('Declare the unique application permission catalog with bounded descriptions.');
    }
    if (!(plan.requiredSecrets ?? []).includes('COMPANIST_AUTH_CLIENT_SECRET') ||
        managedAuthConfiguration.some(name => !(plan.requiredConfiguration ?? []).includes(name)) ||
        (plan.generatedSecrets ?? []).includes('COMPANIST_AUTH_CLIENT_SECRET')) {
      error('Declare all six managed identity settings and the required client secret; do not generate that secret as an application key.');
    }
  }
  const path = typeof plan.composeFile === 'string' ? resolve(root, plan.composeFile.replace(/^\//, '')) : root;
  const child = relative(root, path);
  if (!child || child.startsWith('..') || isAbsolute(child)) {
    error('The Compose file must stay inside the repository.');
    return issues;
  }
  let compose;
  try {
    compose = JSON.parse(await readFile(path, 'utf8'));
  } catch {
    error('Provide the declared JSON Compose file.');
    return issues;
  }
  if (!compose?.services?.[plan.publicService]) {
    error('The public service must exist in Compose.');
  }
  const bound = new Set();
  for (const service of Object.values(compose?.services ?? {})) {
    if (!service?.healthcheck?.test || service.healthcheck.disable) {
      error('Each service needs an enabled healthcheck.');
    }
    for (const [key, value] of Object.entries(service?.environment ?? {})) {
      if (!/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(key) || typeof value !== 'string') {
        error('Compose environment entries need portable names and string values.');
        continue;
      }
      if (value.includes('$')) {
        const match = /^\$\{([A-Z][A-Z0-9_]{0,127})\}$/.exec(value);
        if (!match || !names.includes(match[1])) {
          error('Use only whole-value substitutions of declared runtime variables.');
        } else {
          bound.add(match[1]);
        }
      }
    }
  }
  if (plan.companistAuth && [...managedAuthConfiguration, 'COMPANIST_AUTH_CLIENT_SECRET'].some(name => !bound.has(name))) {
    error('Map every managed identity variable into the BFF service configuration.');
  }
  return issues;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const issues = await validateCompanistHosting(resolve(process.argv[2] ?? process.cwd()));
  console.log(JSON.stringify({ issues }, null, 2));
  process.exitCode = issues.some(issue => issue.severity === 'error') ? 1 : 0;
}
