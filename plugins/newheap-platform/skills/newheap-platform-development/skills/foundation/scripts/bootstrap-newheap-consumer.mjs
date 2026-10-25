import { spawnSync } from 'node:child_process';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const skillRoot = resolve(scriptDirectory, '..');
const args = process.argv.slice(2);
const valueAfter = option => {
  const index = args.indexOf(option);
  return index >= 0 ? args[index + 1] : undefined;
};
const optionValues = new Set(['--root', '--name', '--profile', '--database'].map(valueAfter).filter(Boolean));
const positional = args.filter(value => !value.startsWith('-') && !optionValues.has(value));
const consumerRoot = resolve(valueAfter('--root') ?? positional[0] ?? process.cwd());
const applicationName = valueAfter('--name');
const applicationProfile = valueAfter('--profile')?.toLowerCase();
const database = valueAfter('--database')?.toLowerCase();
const skipInstall = args.includes('--skip-install');
const profiles = ['service', 'api', 'management-portal', 'public-website'];
const apiEnabled = ['api', 'management-portal', 'public-website'].includes(applicationProfile);
const backgroundServiceEnabled = applicationProfile === 'service';
const frontendKind = applicationProfile === 'management-portal'
  ? 'management'
  : applicationProfile === 'public-website' ? 'public' : undefined;
const frontendEnabled = frontendKind !== undefined;
const authenticationRequested = args.includes('--authentication');
const authenticationEnabled = applicationProfile === 'management-portal' || authenticationRequested;
const features = {
  aspire: args.includes('--aspire'),
  docker: args.includes('--docker'),
  elasticsearch: args.includes('--elasticsearch')
};

if (!applicationName || !/^[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*)*$/.test(applicationName)) {
  throw new Error('Use --name with a PascalCase or dotted PascalCase application name, for example Example.Portal.');
}
if (!profiles.includes(applicationProfile)) {
  throw new Error('--profile must be service, api, management-portal, or public-website. Derive it from the confirmed product scope instead of assuming a portal.');
}
if (applicationProfile === 'public-website' && authenticationRequested) {
  throw new Error('The public-website profile has no sign-in. Use management-portal for screens behind a login, or api with --authentication for protected system access.');
}
if (!['none', 'postgresql', 'sqlserver'].includes(database)) {
  throw new Error('--database must be none, postgresql, or sqlserver. Derive persistence from whether the product must retain its own data.');
}
if (applicationProfile === 'management-portal' && database === 'none') {
  throw new Error('The authenticated management-portal profile requires postgresql or sqlserver. Use api or service when the current scope has no application-owned persistence.');
}
if (!(await stat(consumerRoot).catch(() => undefined))?.isDirectory()) {
  throw new Error(`Consumer root does not exist: ${consumerRoot}`);
}

async function loadDistribution() {
  const candidates = [
    resolve(skillRoot, '.newheap-skill-install.json'),
    resolve(skillRoot, '..', '.newheap-platform-install.json'),
    resolve(skillRoot, '..', '..', '.newheap-platform-install.json'),
    resolve(skillRoot, '..', '..', 'distribution.json'),
    resolve(skillRoot, '..', '..', '..', '..', 'distribution.json'),
    resolve(skillRoot, '..', '..', 'plugins', 'newheap-platform', 'distribution.json')
  ];
  for (const candidate of candidates) {
    try {
      const value = JSON.parse(await readFile(candidate, 'utf8'));
      if (value.compatiblePackages) return value;
    } catch {
      // Try the next supported installed, plugin, or source-tree location.
    }
  }
  throw new Error('Could not find NewHeap compatibility metadata. Install this skill from the versioned plugin before bootstrapping.');
}

const distribution = await loadDistribution();
const packages = distribution.compatiblePackages;
const requiredPackageVersion = name => {
  if (!packages[name]) throw new Error(`Compatibility metadata has no version for ${name}.`);
  return packages[name];
};

// The TypeScript and zone.js ranges that go with each supported Angular major version.
const angularToolchains = new Map([
  [20, { typescript: '~5.9.2', zoneJs: '~0.15.0' }]
]);
const versionParts = text => /^(\d+)\.(\d+)\.(\d+)$/.exec(text)?.slice(1).map(Number);
const compareVersions = (left, right) => left.map((part, index) => part - right[index]).find(difference => difference !== 0) ?? 0;

/** The versions a comparator set accepts, as a lower bound and an exclusive upper bound. */
function comparatorBounds(name, range, comparator) {
  const caret = /^\^(\d+\.\d+\.\d+)$/.exec(comparator);
  if (caret) {
    const [major, minor, patch] = versionParts(caret[1]);
    return { min: [major, minor, patch], max: major > 0 ? [major + 1, 0, 0] : minor > 0 ? [0, minor + 1, 0] : [0, 0, patch + 1] };
  }
  const tilde = /^~(\d+\.\d+\.\d+)$/.exec(comparator);
  if (tilde) {
    const [major, minor, patch] = versionParts(tilde[1]);
    return { min: [major, minor, patch], max: [major, minor + 1, 0] };
  }
  const atLeast = /^>=\s*(\d+\.\d+\.\d+)(?:\s+<\s*(\d+\.\d+\.\d+))?$/.exec(comparator);
  if (atLeast) return { min: versionParts(atLeast[1]), max: atLeast[2] ? versionParts(atLeast[2]) : undefined };
  const exact = /^=?(\d+\.\d+\.\d+)$/.exec(comparator);
  if (exact) {
    const [major, minor, patch] = versionParts(exact[1]);
    return { min: [major, minor, patch], max: [major, minor, patch + 1] };
  }
  throw new Error(`Unsupported peer range ${range} for ${name} in the NewHeap compatibility metadata.`);
}

/** The alternatives of a peer range such as `^20.3.28 || ^21.0.0`. */
const peerRangeAlternatives = (name, range) => range.split('||').map(comparator => comparatorBounds(name, range, comparator.trim()));
const accepts = (alternatives, version) => alternatives.some(({ min, max }) =>
  compareVersions(version, min) >= 0 && (!max || compareVersions(version, max) < 0));

/**
 * The Angular versions to pin, derived from the peer ranges of @newheap/platform-common: the lowest
 * version that every @angular/* range accepts, so the pins always satisfy the library.
 */
function angularPins() {
  const peers = distribution.npmPeerDependencies?.['@newheap/platform-common'];
  if (!peers?.['@angular/core']) {
    throw new Error('Compatibility metadata has no Angular peer range for @newheap/platform-common. Install this skill from the current plugin before bootstrapping a front end.');
  }
  const ranges = Object.entries(peers)
    .filter(([name]) => name.startsWith('@angular/'))
    .map(([name, range]) => peerRangeAlternatives(name, range));
  // The lowest accepted version is always the lower bound of one of the alternatives.
  const version = ranges
    .flatMap(alternatives => alternatives.map(alternative => alternative.min))
    .sort(compareVersions)
    .find(candidate => ranges.every(alternatives => accepts(alternatives, candidate)));
  if (!version) {
    throw new Error('No single Angular version satisfies every @angular/* peer range of @newheap/platform-common.');
  }
  const toolchain = angularToolchains.get(version[0]);
  if (!toolchain) {
    throw new Error(`The bootstrap has no TypeScript and zone.js ranges for Angular ${version[0]}. Add them before bootstrapping with this platform-common version.`);
  }
  return {
    framework: version.join('.'),
    tooling: `^${version[0]}.${version[1]}.0`,
    rxjs: peers.rxjs ?? '~7.8.0',
    ...toolchain
  };
}

const backendRoot = resolve(consumerRoot, 'src', 'Back-end');
const frontendRoot = resolve(consumerRoot, 'src', 'Front-end');
const created = [];
const unchanged = [];

async function writeManaged(relativePath, content) {
  const path = resolve(consumerRoot, relativePath);
  const normalized = content.replaceAll('\r\n', '\n');
  let current;
  try { current = (await readFile(path, 'utf8')).replaceAll('\r\n', '\n'); } catch { current = undefined; }
  if (current === normalized) {
    unchanged.push(relativePath);
    return;
  }
  if (current !== undefined) throw new Error(`Refusing to overwrite existing file with different content: ${relativePath}`);
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, normalized, 'utf8');
  created.push(relativePath);
}

const providerPackage = database === 'postgresql'
  ? '<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />'
  : database === 'sqlserver'
    ? '<PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.10" />'
    : undefined;
const providerReference = database === 'postgresql'
  ? 'Npgsql.EntityFrameworkCore.PostgreSQL'
  : database === 'sqlserver'
    ? 'Microsoft.EntityFrameworkCore.SqlServer'
    : undefined;
const applicationProjectName = `${applicationName}.${apiEnabled ? 'Api' : 'Service'}`;
const applicationProjectPath = `Applications/${applicationProjectName}/${applicationProjectName}.csproj`;
const centralPackageVersions = [
  `<PackageVersion Include="NewHeap.Platform.Common" Version="${requiredPackageVersion('NewHeap.Platform.Common')}" />`,
  ...(apiEnabled ? [
    `<PackageVersion Include="NewHeap.Platform.AspNet.Common" Version="${requiredPackageVersion('NewHeap.Platform.AspNet.Common')}" />`,
    '<PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.10" />',
    '<PackageVersion Include="Scalar.AspNetCore" Version="2.16.7" />'
  ] : [
    '<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />'
  ]),
  ...(providerPackage ? [providerPackage] : [])
];

await writeManaged('newheap-consumer.json', `${JSON.stringify({
  schemaVersion: 1,
  applicationName,
  applicationProfile,
  capabilities: {
    api: apiEnabled,
    backgroundService: backgroundServiceEnabled,
    persistence: database !== 'none',
    authentication: authenticationEnabled,
    frontend: frontendKind ?? 'deferred'
  },
  databaseProvider: database === 'none' ? null : database,
  features,
  newHeap: {
    pluginVersion: distribution.pluginVersion,
    guidanceVersion: distribution.guidanceVersion,
    compatiblePackages: packages
  },
  paths: {
    backend: 'src/Back-end',
    frontend: 'src/Front-end',
    angularProjects: 'src/Front-end/projects'
  }
}, null, 2)}\n`);

await writeManaged('src/Back-end/Directory.Build.props', `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
`);

await writeManaged('src/Back-end/Directory.Packages.props', `<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    ${centralPackageVersions.join('\n    ')}
  </ItemGroup>
</Project>
`);

await writeManaged(`src/Back-end/${applicationName}.slnx`, `<Solution>
  <Folder Name="/Applications/">
    <Project Path="${applicationProjectPath}" />
  </Folder>
  <Folder Name="/Libraries/">
    <Project Path="Libraries/${applicationName}.Core/${applicationName}.Core.csproj" />
  </Folder>
</Solution>
`);

await writeManaged('src/Back-end/nuget.config', `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
`);

await writeManaged(`src/Back-end/Libraries/${applicationName}.Core/${applicationName}.Core.csproj`, `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="NewHeap.Platform.Common" />
    ${apiEnabled ? '<PackageReference Include="NewHeap.Platform.AspNet.Common" />' : ''}
  </ItemGroup>
</Project>
`);

if (apiEnabled) {
  await writeManaged(`src/Back-end/Applications/${applicationName}.Api/${applicationName}.Api.csproj`, `<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="NewHeap.Platform.AspNet.Common" />
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Scalar.AspNetCore" />
    ${providerReference ? `<PackageReference Include="${providerReference}" />` : ''}
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../Libraries/${applicationName}.Core/${applicationName}.Core.csproj" />
  </ItemGroup>
</Project>
`);

  await writeManaged(`src/Back-end/Applications/${applicationName}.Api/Program.cs`, `using NewHeap.Platform.AspNet.Common.Controllers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();
app.MapOpenApi();
app.MapGet("/health/newheap-package", () => Results.Ok(new
{
    Package = typeof(ProtectedNhBaseController).Assembly.GetName().Name
})).AllowAnonymous();

app.Run();
`);
} else {
  await writeManaged(`src/Back-end/Applications/${applicationName}.Service/${applicationName}.Service.csproj`, `<Project Sdk="Microsoft.NET.Sdk.Worker">
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    ${providerReference ? `<PackageReference Include="${providerReference}" />` : ''}
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../Libraries/${applicationName}.Core/${applicationName}.Core.csproj" />
  </ItemGroup>
</Project>
`);

  await writeManaged(`src/Back-end/Applications/${applicationName}.Service/Program.cs`, `var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
`);

  await writeManaged(`src/Back-end/Applications/${applicationName}.Service/Worker.cs`, `public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{ServiceName} started.", nameof(Worker));
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
`);
}

if (frontendEnabled) {
  const angular = angularPins();
  await writeManaged('src/Front-end/.npmrc', `registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
`);

  await writeManaged('src/Front-end/package.json', `${JSON.stringify({
  name: `${applicationName.toLowerCase().replaceAll('.', '-')}-front-end`,
  version: '0.1.0',
  private: true,
  scripts: {},
  dependencies: {
    '@angular/animations': angular.framework,
    '@angular/common': angular.framework,
    '@angular/compiler': angular.framework,
    '@angular/core': angular.framework,
    '@angular/forms': angular.framework,
    '@angular/platform-browser': angular.framework,
    '@angular/platform-browser-dynamic': angular.framework,
    '@angular/router': angular.framework,
    '@newheap/platform-common': `^${requiredPackageVersion('@newheap/platform-common')}`,
    'rxjs': angular.rxjs,
    'tslib': '^2.3.0',
    'zone.js': angular.zoneJs
  },
  devDependencies: {
    '@angular/build': angular.tooling,
    '@angular/cli': angular.tooling,
    '@angular/compiler-cli': angular.framework,
    'typescript': angular.typescript
  }
}, null, 2)}\n`);

  await writeManaged('src/Front-end/angular.json', `${JSON.stringify({
  $schema: './node_modules/@angular/cli/lib/config/schema.json',
  version: 1,
  newProjectRoot: 'projects',
  projects: {}
}, null, 2)}\n`);

  await writeManaged('src/Front-end/tsconfig.json', `${JSON.stringify({
  compileOnSave: false,
  compilerOptions: {
    baseUrl: './',
    outDir: './dist/out-tsc',
    strict: true,
    sourceMap: true,
    declaration: false,
    moduleResolution: 'bundler',
    importHelpers: true,
    target: 'ES2022',
    module: 'preserve',
    lib: ['ES2022', 'dom']
  }
}, null, 2)}\n`);

  await writeManaged('src/Front-end/proxy.conf.cjs', `const apiTarget = process.env['services__api__https__0'] ?? 'https://localhost:7001';

module.exports = [{
  context: ['/api'],
  target: apiTarget,
  secure: false,
  changeOrigin: true,
  pathRewrite: { '^/api': '' }
}];
`);
  await writeManaged('src/Front-end/projects/.gitkeep', '');
} else {
  await writeManaged('src/Front-end/.gitkeep', '');
}

const installedAgentTargets = [];
for (const target of [
  { directory: '.agents', instructionsFile: 'AGENTS.md' },
  { directory: '.claude', instructionsFile: 'CLAUDE.md' }
]) {
  const installedSkill = await stat(resolve(
    consumerRoot,
    target.directory,
    'skills',
    'newheap-platform-development',
    'skills',
    'foundation',
    'SKILL.md'
  )).catch(() => undefined);
  if (installedSkill?.isFile()) installedAgentTargets.push(target);
}
if (installedAgentTargets.length === 0) installedAgentTargets.push({ directory: '.agents', instructionsFile: 'AGENTS.md' });

const renderAgentInstructions = skillDirectory => `# NewHeap consumer instructions

- Infer the smallest current product scope from existing context. Ask only missing product questions in plain language, summarize the resulting profile, and scaffold only confirmed capabilities.
- Keep the .NET solution and central props in \`src/Back-end\`.
- Keep application hosts in \`src/Back-end/Applications\`, reusable application code in \`src/Back-end/Libraries\`, and tests in \`src/Back-end/Tests\` so more APIs or services can be added without restructuring.
- Leave only \`src/Front-end/.gitkeep\` while no user interface is needed. Create the Angular workspace in \`src/Front-end\` and applications in \`src/Front-end/projects\` only after an interactive frontend is confirmed.
- Complete package restore and installation before generating domain features.
- For a confirmed management portal, use NewHeap protected/base controllers, \`NhBaseApiService\`, collection bases and modal services; do not substitute generic CRUD panels or edit asides.
- For a confirmed public website, keep the Angular application and its API endpoints anonymous, without a login; when sign-in becomes part of the scope, change the profile instead of adding it to the public site.
- Run \`node ${skillDirectory}/skills/newheap-platform-development/skills/foundation/scripts/inspect-newheap-consumer.mjs . --mode validate\` before handoff.
`;

for (const target of installedAgentTargets) {
  try {
    await readFile(resolve(consumerRoot, target.instructionsFile), 'utf8');
    unchanged.push(`${target.instructionsFile} (preserved existing repository instructions)`);
  } catch {
    await writeManaged(target.instructionsFile, renderAgentInstructions(target.directory));
  }
}

await writeManaged('src/Back-end/Orchestration/.gitkeep', '');
await writeManaged('src/Back-end/Tests/.gitkeep', '');

console.log(`Prepared ${applicationName} at ${consumerRoot}. Created ${created.length} files; ${unchanged.length} were already identical.`);

function run(command, commandArgs, cwd) {
  const result = spawnSync(command, commandArgs, { cwd, stdio: 'inherit', shell: false });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} ${commandArgs.join(' ')} failed. Remove stale private-feed overrides or fix public registry access before generating any domain features.`);
}

if (skipInstall) {
  console.log(`Skipped package installation by request. The foundation is not ready for feature scaffolding until dotnet restore${frontendEnabled ? ' and npm install succeed' : ' succeeds'}.`);
} else {
  run('dotnet', ['restore', `${applicationName}.slnx`], backendRoot);
  if (frontendEnabled) {
    if (process.platform === 'win32') {
      run(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', 'npm install --no-audit'], frontendRoot);
    } else {
      run('npm', ['install', '--no-audit'], frontendRoot);
    }
  }
  run(process.execPath, [resolve(scriptDirectory, 'inspect-newheap-consumer.mjs'), consumerRoot, '--mode', 'foundation'], consumerRoot);
  console.log(`NewHeap packages restored successfully for the ${applicationProfile} profile. Continue only with evidence for the confirmed capabilities, then run --mode validate.`);
}

console.log(`Optional features selected: ${Object.entries(features).filter(([, enabled]) => enabled).map(([name]) => name).join(', ') || 'none'}. Add them only after the baseline validates.`);
