import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const testDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(testDirectory, '..', '..', '..', '..', '..', '..');
const bootstrapScript = resolve(repositoryRoot, 'skills/newheap-consumer-development/scripts/bootstrap-newheap-consumer.mjs');
const inspectorScript = resolve(repositoryRoot, 'skills/newheap-consumer-development/scripts/inspect-newheap-consumer.mjs');
const consumerRoot = await mkdtemp(resolve(tmpdir(), 'newheap-consumer-bootstrap-'));
const sqlServerRoot = await mkdtemp(resolve(tmpdir(), 'newheap-consumer-bootstrap-sqlserver-'));
const serviceRoot = await mkdtemp(resolve(tmpdir(), 'newheap-consumer-bootstrap-service-'));

function run(script, argumentsList) {
  return spawnSync(process.execPath, [script, ...argumentsList], {
    cwd: repositoryRoot,
    encoding: 'utf8'
  });
}

try {
  const existingInstructions = '# Existing repository instructions\n\nKeep local ownership rules.\n';
  await writeFile(resolve(consumerRoot, 'AGENTS.md'), existingInstructions, 'utf8');
  for (const targetDirectory of ['.agents', '.claude']) {
    const installedSkillRoot = resolve(
      consumerRoot,
      targetDirectory,
      'skills',
      'newheap-platform-development',
      'skills',
      'foundation'
    );
    await mkdir(installedSkillRoot, { recursive: true });
    await writeFile(resolve(installedSkillRoot, 'SKILL.md'), '---\nname: newheap-consumer-development\ndescription: Test installation.\n---\n', 'utf8');
  }
  const bootstrap = run(bootstrapScript, [
    consumerRoot,
    '--name', 'Example.Portal',
    '--profile', 'management-portal',
    '--database', 'postgresql',
    '--aspire',
    '--docker',
    '--elasticsearch',
    '--skip-install'
  ]);
  assert.equal(bootstrap.status, 0, bootstrap.stderr || bootstrap.stdout);

  const manifest = JSON.parse(await readFile(resolve(consumerRoot, 'newheap-consumer.json'), 'utf8'));
  assert.equal(manifest.applicationProfile, 'management-portal');
  assert.deepEqual(manifest.capabilities, {
    api: true,
    backgroundService: false,
    persistence: true,
    authentication: true,
    frontend: 'management'
  });
  assert.equal(manifest.databaseProvider, 'postgresql');
  assert.deepEqual(manifest.features, { aspire: true, docker: true, elasticsearch: true });
  assert.equal(manifest.paths.backend, 'src/Back-end');
  assert.equal(manifest.paths.frontend, 'src/Front-end');
  assert.equal(await readFile(resolve(consumerRoot, 'AGENTS.md'), 'utf8'), existingInstructions);
  const claudeInstructions = await readFile(resolve(consumerRoot, 'CLAUDE.md'), 'utf8');
  assert.match(claudeInstructions, /node \.claude\/skills\/newheap-platform-development\/skills\/foundation\/scripts\/inspect-newheap-consumer\.mjs/);

  await readFile(resolve(consumerRoot, 'src/Back-end/Example.Portal.slnx'), 'utf8');
  await readFile(resolve(consumerRoot, 'src/Back-end/Directory.Build.props'), 'utf8');
  await readFile(resolve(consumerRoot, 'src/Back-end/Directory.Packages.props'), 'utf8');
  const publicNugetConfig = await readFile(resolve(consumerRoot, 'src/Back-end/nuget.config'), 'utf8');
  const publicNpmConfig = await readFile(resolve(consumerRoot, 'src/Front-end/.npmrc'), 'utf8');
  assert.match(publicNugetConfig, /https:\/\/api\.nuget\.org\/v3\/index\.json/);
  assert.doesNotMatch(publicNugetConfig, /github|password|token/i);
  assert.match(publicNpmConfig, /@newheap:registry=https:\/\/registry\.npmjs\.org\//);
  assert.doesNotMatch(publicNpmConfig, /auth|token|npm\.pkg\.github/i);
  await readFile(resolve(consumerRoot, 'src/Front-end/angular.json'), 'utf8');
  await readFile(resolve(consumerRoot, 'src/Front-end/package.json'), 'utf8');
  await assert.rejects(readFile(resolve(consumerRoot, 'Example.Portal.slnx'), 'utf8'));
  await assert.rejects(readFile(resolve(consumerRoot, 'angular.json'), 'utf8'));

  await mkdir(resolve(consumerRoot, '.agents/skills/noise'), { recursive: true });
  await mkdir(resolve(consumerRoot, '.claude/skills/noise'), { recursive: true });
  await mkdir(resolve(consumerRoot, 'docs'), { recursive: true });
  const ignoredLifecycleSource = 'export class Noise extends NhModalMutateBaseComponent<object, object> { ngOnInit() {} }';
  await writeFile(resolve(consumerRoot, '.agents/skills/noise/noise.ts'), ignoredLifecycleSource, 'utf8');
  await writeFile(resolve(consumerRoot, '.claude/skills/noise/noise.ts'), ignoredLifecycleSource, 'utf8');
  await writeFile(resolve(consumerRoot, 'docs/noise.ts'), ignoredLifecycleSource, 'utf8');

  const inventory = run(inspectorScript, [consumerRoot, '--mode', 'inventory']);
  assert.equal(inventory.status, 0, inventory.stderr || inventory.stdout);
  const report = JSON.parse(inventory.stdout);
  assert.deepEqual(report.projectFoundation.missingFoundationPaths, []);
  assert.deepEqual(report.projectFoundation.rootWorkspaceFiles, []);
  assert.deepEqual(report.angular.directAngularLifecycleOverrides, []);
  assert.ok(report.newHeapPackages.includes('NewHeap.Platform.AspNet.Common'));
  assert.ok(report.newHeapPackages.some(value => value.startsWith('@newheap/platform-common@')));

  const repeat = run(bootstrapScript, [
    consumerRoot,
    '--name', 'Example.Portal',
    '--profile', 'management-portal',
    '--database', 'postgresql',
    '--aspire',
    '--docker',
    '--elasticsearch',
    '--skip-install'
  ]);
  assert.equal(repeat.status, 0, repeat.stderr || repeat.stdout);

  manifest.capabilities.identityOwnership = 'external';
  await writeFile(
    resolve(consumerRoot, 'newheap-consumer.json'),
    `${JSON.stringify(manifest, null, 2)}\n`,
    'utf8'
  );
  const controllerDirectory = resolve(
    consumerRoot,
    'src/Back-end/Applications/Example.Portal.Api/Controllers'
  );
  await mkdir(controllerDirectory, { recursive: true });
  await writeFile(
    resolve(controllerDirectory, 'ExternalIdentityController.cs'),
    'using Microsoft.AspNetCore.Authorization;\nusing Microsoft.AspNetCore.Mvc;\n\n[ApiController]\n[Route("external-identity")]\npublic sealed class ExternalIdentityController : ControllerBase\n{\n    [HttpGet]\n    [Authorize]\n    public IActionResult Get() => Ok();\n}\n',
    'utf8'
  );
  const portalSourceDirectory = resolve(consumerRoot, 'src/Front-end/projects/portal/src/app');
  await mkdir(portalSourceDirectory, { recursive: true });
  await writeFile(
    resolve(portalSourceDirectory, 'external-confirmation.ts'),
    'import { NhModalConfirmComponent, NhModalService } from "@newheap/platform-common";\nexport const externalConfirmation = [NhModalConfirmComponent, NhModalService];\n',
    'utf8'
  );
  const deploymentDirectory = resolve(consumerRoot, 'deployment');
  await mkdir(deploymentDirectory, { recursive: true });
  await writeFile(resolve(deploymentDirectory, 'Api.Dockerfile'), 'FROM scratch\n', 'utf8');

  const externalIdentityValidation = run(inspectorScript, [consumerRoot, '--mode', 'validate']);
  const externalIdentityReport = JSON.parse(externalIdentityValidation.stdout);
  const externalIdentityIssueCodes = externalIdentityReport.issues.map(issue => issue.code);
  assert.doesNotMatch(externalIdentityIssueCodes.join(','), /identity-dbcontext|protected-controller|angular-modal-base|docker-missing/);
  assert.ok(externalIdentityReport.backend.authorizedControllers.includes(
    'src/Back-end/Applications/Example.Portal.Api/Controllers/ExternalIdentityController.cs'
  ));
  assert.ok(externalIdentityReport.angular.modalContent.includes(
    'src/Front-end/projects/portal/src/app/external-confirmation.ts'
  ));
  assert.ok(externalIdentityReport.optionalInfrastructure.docker.includes('deployment/Api.Dockerfile'));

  await writeFile(resolve(consumerRoot, 'angular.json'), '{}\n', 'utf8');
  const invalid = run(inspectorScript, [consumerRoot, '--mode', 'foundation']);
  assert.notEqual(invalid.status, 0, 'Foundation validation must reject Angular workspace files at the repository root.');
  assert.match(invalid.stdout, /root-workspace-file/);

  const sqlServerBootstrap = run(bootstrapScript, [
    sqlServerRoot,
    '--name', 'Example.SqlPortal',
    '--profile', 'api',
    '--database', 'sqlserver',
    '--authentication',
    '--skip-install'
  ]);
  assert.equal(sqlServerBootstrap.status, 0, sqlServerBootstrap.stderr || sqlServerBootstrap.stdout);
  const sqlServerManifest = JSON.parse(await readFile(resolve(sqlServerRoot, 'newheap-consumer.json'), 'utf8'));
  const sqlServerPackages = await readFile(resolve(sqlServerRoot, 'src/Back-end/Directory.Packages.props'), 'utf8');
  const sqlServerInventory = run(inspectorScript, [sqlServerRoot, '--mode', 'inventory']);
  assert.equal(sqlServerManifest.databaseProvider, 'sqlserver');
  assert.equal(sqlServerManifest.applicationProfile, 'api');
  assert.equal(sqlServerManifest.capabilities.frontend, 'deferred');
  assert.equal(sqlServerManifest.capabilities.authentication, true);
  assert.match(sqlServerPackages, /Microsoft\.EntityFrameworkCore\.SqlServer/);
  assert.doesNotMatch(sqlServerPackages, /Npgsql\.EntityFrameworkCore\.PostgreSQL/);
  await readFile(resolve(sqlServerRoot, 'src/Front-end/.gitkeep'), 'utf8');
  await assert.rejects(readFile(resolve(sqlServerRoot, 'src/Front-end/angular.json'), 'utf8'));
  await assert.rejects(readFile(resolve(sqlServerRoot, 'src/Front-end/package.json'), 'utf8'));
  assert.equal(sqlServerInventory.status, 0, sqlServerInventory.stderr || sqlServerInventory.stdout);
  assert.deepEqual(JSON.parse(sqlServerInventory.stdout).projectFoundation.missingFoundationPaths, []);

  const serviceBootstrap = run(bootstrapScript, [
    serviceRoot,
    '--name', 'Example.Processor',
    '--profile', 'service',
    '--database', 'none',
    '--skip-install'
  ]);
  assert.equal(serviceBootstrap.status, 0, serviceBootstrap.stderr || serviceBootstrap.stdout);
  const serviceManifest = JSON.parse(await readFile(resolve(serviceRoot, 'newheap-consumer.json'), 'utf8'));
  const serviceSolution = await readFile(resolve(serviceRoot, 'src/Back-end/Example.Processor.slnx'), 'utf8');
  const servicePackages = await readFile(resolve(serviceRoot, 'src/Back-end/Directory.Packages.props'), 'utf8');
  const serviceInventory = run(inspectorScript, [serviceRoot, '--mode', 'inventory']);
  assert.equal(serviceManifest.applicationProfile, 'service');
  assert.equal(serviceManifest.databaseProvider, null);
  assert.deepEqual(serviceManifest.capabilities, {
    api: false,
    backgroundService: true,
    persistence: false,
    authentication: false,
    frontend: 'deferred'
  });
  assert.match(serviceSolution, /Example\.Processor\.Service/);
  assert.doesNotMatch(servicePackages, /NewHeap\.Platform\.AspNet\.Common/);
  assert.doesNotMatch(servicePackages, /EntityFrameworkCore\.(?:SqlServer|PostgreSQL)/);
  await readFile(resolve(serviceRoot, 'src/Back-end/Applications/Example.Processor.Service/Worker.cs'), 'utf8');
  await readFile(resolve(serviceRoot, 'src/Front-end/.gitkeep'), 'utf8');
  await assert.rejects(readFile(resolve(serviceRoot, 'src/Front-end/angular.json'), 'utf8'));
  assert.equal(serviceInventory.status, 0, serviceInventory.stderr || serviceInventory.stdout);
  assert.deepEqual(JSON.parse(serviceInventory.stdout).projectFoundation.missingFoundationPaths, []);

  const missingScope = run(bootstrapScript, [
    serviceRoot,
    '--name', 'Example.Unclear',
    '--database', 'none',
    '--skip-install'
  ]);
  assert.notEqual(missingScope.status, 0, 'Bootstrap must not assume a technical profile when the product scope is unresolved.');
  assert.match(missingScope.stderr, /--profile must be service, api, or management-portal/);
} finally {
  await rm(consumerRoot, { recursive: true, force: true });
  await rm(sqlServerRoot, { recursive: true, force: true });
  await rm(serviceRoot, { recursive: true, force: true });
}

console.log('Validated scope-driven NewHeap consumer profiles and post-bootstrap inspection.');
