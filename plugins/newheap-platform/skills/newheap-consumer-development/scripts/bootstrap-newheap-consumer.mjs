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
const apiEnabled = ['api', 'management-portal'].includes(applicationProfile);
const backgroundServiceEnabled = applicationProfile === 'service';
const frontendEnabled = applicationProfile === 'management-portal';
const authenticationEnabled = frontendEnabled || args.includes('--authentication');
const features = {
  aspire: args.includes('--aspire'),
  docker: args.includes('--docker'),
  elasticsearch: args.includes('--elasticsearch')
};

if (!applicationName || !/^[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*)*$/.test(applicationName)) {
  throw new Error('Use --name with a PascalCase or dotted PascalCase application name, for example Example.Portal.');
}
if (!['service', 'api', 'management-portal'].includes(applicationProfile)) {
  throw new Error('--profile must be service, api, or management-portal. Derive it from the confirmed product scope instead of assuming a portal.');
}
if (!['none', 'postgresql', 'sqlserver'].includes(database)) {
  throw new Error('--database must be none, postgresql, or sqlserver. Derive persistence from whether the product must retain its own data.');
}
if (frontendEnabled && database === 'none') {
  throw new Error('The authenticated management-portal profile requires postgresql or sqlserver. Use api or service when the current scope has no application-owned persistence.');
}
if (!(await stat(consumerRoot).catch(() => undefined))?.isDirectory()) {
  throw new Error(`Consumer root does not exist: ${consumerRoot}`);
}

async function loadDistribution() {
  const candidates = [
    resolve(skillRoot, '.newheap-skill-install.json'),
    resolve(skillRoot, '..', '.newheap-platform-install.json'),
    resolve(skillRoot, '..', '..', 'distribution.json'),
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
    frontend: frontendEnabled ? 'management' : 'deferred'
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
  await writeManaged('src/Front-end/.npmrc', `registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
`);

  await writeManaged('src/Front-end/package.json', `${JSON.stringify({
  name: `${applicationName.toLowerCase().replaceAll('.', '-')}-front-end`,
  version: '0.1.0',
  private: true,
  scripts: {},
  dependencies: {
    '@angular/animations': '20.3.27',
    '@angular/common': '20.3.27',
    '@angular/compiler': '20.3.27',
    '@angular/core': '20.3.27',
    '@angular/forms': '20.3.27',
    '@angular/platform-browser': '20.3.27',
    '@angular/platform-browser-dynamic': '20.3.27',
    '@angular/router': '20.3.27',
    '@newheap/platform-common': `^${requiredPackageVersion('@newheap/platform-common')}`,
    'rxjs': '~7.8.0',
    'tslib': '^2.3.0',
    'zone.js': '~0.15.0'
  },
  devDependencies: {
    '@angular/build': '^20.3.26',
    '@angular/cli': '^20.3.26',
    '@angular/compiler-cli': '20.3.27',
    'typescript': '~5.9.2'
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
  const installedSkill = await stat(resolve(consumerRoot, target.directory, 'skills', 'newheap-consumer-development', 'SKILL.md')).catch(() => undefined);
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
- Run \`node ${skillDirectory}/skills/newheap-consumer-development/scripts/inspect-newheap-consumer.mjs . --mode validate\` before handoff.
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
