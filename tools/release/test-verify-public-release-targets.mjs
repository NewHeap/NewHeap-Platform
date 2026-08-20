import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { resolve } from 'node:path';
import { loadReleaseManifest, repositoryRoot } from './lib.mjs';

const manifest = await loadReleaseManifest();
const npmComponent = 'npm-nh-toastr';
const npmVersion = manifest.units[npmComponent].version;
const previewVersion = `${npmVersion}-ci.42.abcdef0`;
const nugetComponent = 'nuget-caching';
const nugetVersion = manifest.units[nugetComponent].version;
const verifier = resolve(repositoryRoot, 'tools', 'release', 'verify-public-release-targets.mjs');

function json(response, status, value) {
  response.writeHead(status, { 'Content-Type': 'application/json' });
  response.end(JSON.stringify(value));
}

async function exercise(component, handler, arguments_) {
  const server = createServer(handler);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const { port } = server.address();
  const child = spawn(process.execPath, [verifier, '--component', component, ...arguments_], {
    cwd: repositoryRoot,
    env: {
      ...process.env,
      NPM_REGISTRY_URL: `http://127.0.0.1:${port}/npm`,
      NUGET_FLAT_CONTAINER_URL: `http://127.0.0.1:${port}/nuget`
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let stdout = '';
  let stderr = '';
  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stdout.on('data', chunk => { stdout += chunk; });
  child.stderr.on('data', chunk => { stderr += chunk; });
  const [code] = await once(child, 'close');
  await new Promise((resolveClose, reject) => server.close(error => error ? reject(error) : resolveClose()));
  return { code, stdout, stderr };
}

let npmRequests = 0;
const delayed = await exercise(npmComponent, (_request, response) => {
  npmRequests += 1;
  json(response, 200, { versions: npmRequests === 1 ? {} : { [previewVersion]: {} } });
}, ['--version', previewVersion, '--attempts', '3', '--retry-delay-ms', '1']);
if (delayed.code !== 0 || npmRequests !== 2 || !delayed.stdout.includes('exact public package version')) {
  throw new Error(`Delayed npm visibility was not retried correctly.\n${delayed.stdout}\n${delayed.stderr}`);
}

const missingAllowed = await exercise(npmComponent, (_request, response) => {
  json(response, 404, { message: 'Not Found' });
}, ['--allow-missing', '--attempts', '1', '--retry-delay-ms', '0']);
if (missingAllowed.code !== 0 || !missingAllowed.stdout.includes('0 existing anonymously readable package target')) {
  throw new Error(`The pre-publication missing-package check failed.\n${missingAllowed.stdout}\n${missingAllowed.stderr}`);
}

const missingRequired = await exercise(npmComponent, (_request, response) => {
  json(response, 404, { message: 'Not Found' });
}, ['--require-missing', '--attempts', '1', '--retry-delay-ms', '0']);
if (missingRequired.code !== 0 || !missingRequired.stdout.includes('unused public package name')) {
  throw new Error(`The bootstrap missing-package requirement failed.\n${missingRequired.stdout}\n${missingRequired.stderr}`);
}

const existingRejected = await exercise(npmComponent, (_request, response) => {
  json(response, 200, { versions: { [npmVersion]: {} } });
}, ['--require-missing', '--attempts', '1', '--retry-delay-ms', '0']);
if (existingRejected.code === 0 || !existingRejected.stderr.includes('already exists')) {
  throw new Error(`The bootstrap accepted an existing package name.\n${existingRejected.stdout}\n${existingRejected.stderr}`);
}

const nugetVisible = await exercise(nugetComponent, (_request, response) => {
  json(response, 200, { versions: [nugetVersion] });
}, ['--attempts', '1', '--retry-delay-ms', '0']);
if (nugetVisible.code !== 0 || !nugetVisible.stdout.includes('1 exact public package version')) {
  throw new Error(`The public NuGet version check failed.\n${nugetVisible.stdout}\n${nugetVisible.stderr}`);
}

const inaccessible = await exercise(npmComponent, (_request, response) => {
  json(response, 401, { message: 'Authentication required' });
}, ['--allow-missing', '--attempts', '1', '--retry-delay-ms', '0']);
if (inaccessible.code === 0 || !inaccessible.stderr.includes('Anonymous npm registry request')) {
  throw new Error(`An authentication-gated package target did not fail closed.\n${inaccessible.stdout}\n${inaccessible.stderr}`);
}

const exhausted = await exercise(npmComponent, (_request, response) => {
  json(response, 200, { versions: {} });
}, ['--attempts', '2', '--retry-delay-ms', '1']);
if (exhausted.code === 0 || !exhausted.stderr.includes(`version ${npmVersion} was not found after 2 attempt(s)`)) {
  throw new Error(`Missing exact versions did not fail closed.\n${exhausted.stdout}\n${exhausted.stderr}`);
}

console.log('Verified anonymous npm/NuGet checks, delayed indexing retries and fail-closed behavior.');
