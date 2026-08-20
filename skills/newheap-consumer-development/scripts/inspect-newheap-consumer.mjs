import { access, readdir, readFile, stat } from 'node:fs/promises';
import { extname, relative, resolve } from 'node:path';

const args = process.argv.slice(2);
const modeIndex = args.indexOf('--mode');
const mode = modeIndex >= 0 ? args[modeIndex + 1] : 'inventory';
const rootArgument = args.find((value, index) => !value.startsWith('-') && index !== modeIndex + 1);
const root = resolve(rootArgument ?? process.cwd());
if (!['inventory', 'foundation', 'validate'].includes(mode)) throw new Error('--mode must be inventory, foundation, or validate.');
if (!(await stat(root)).isDirectory()) throw new Error(`${root} is not a directory.`);

const ignored = new Set(['.agents', '.angular', '.claude', '.git', '.nx', 'bin', 'dist', 'docs', 'node_modules', 'obj']);
const extensions = new Set(['.cjs', '.config', '.cs', '.csproj', '.html', '.json', '.mjs', '.props', '.sln', '.slnx', '.ts']);
const files = [];
async function walk(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isSymbolicLink() || entry.isDirectory() && ignored.has(entry.name)) continue;
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) await walk(path);
    else if (extensions.has(extname(entry.name)) || entry.name === 'package.json') files.push(path);
  }
}
await walk(root);

const sources = await Promise.all(files.map(async path => ({
  path,
  name: relative(root, path).replaceAll('\\', '/'),
  content: await readFile(path, 'utf8')
})));
const sourceByName = new Map(sources.map(source => [source.name, source]));
const matchingFiles = pattern => sources.filter(file => pattern.test(file.content)).map(file => file.name);
const filesNamed = name => sources.filter(file => file.name.endsWith(name)).map(file => file.name);
const pathExists = async path => access(resolve(root, path)).then(() => true, () => false);
const hasInlinePackageVersion = source =>
  /<PackageReference\b[^>]*\bVersion(?:Override)?\s*=/i.test(source)
  || [...source.matchAll(/<PackageReference\b[^>]*>([\s\S]*?)<\/PackageReference>/gi)]
    .some(match => /<Version(?:Override)?>/i.test(match[1]));

const packageReferences = new Set();
for (const file of sources.filter(file => file.path.endsWith('.csproj'))) {
  for (const match of file.content.matchAll(/<PackageReference\b[^>]*\bInclude="(NewHeap\.[^"]+)"[^>]*>/gi)) {
    packageReferences.add(match[1]);
  }
}
const npmPackages = new Set();
for (const file of sources.filter(file => file.name.endsWith('package.json'))) {
  let value;
  try { value = JSON.parse(file.content); } catch { continue; }
  for (const [name, version] of Object.entries({ ...value.dependencies, ...value.devDependencies })) {
    if (name.startsWith('@newheap/')) npmPackages.add(`${name}@${version}`);
  }
}

let manifest;
try { manifest = JSON.parse(sourceByName.get('newheap-consumer.json')?.content); } catch { manifest = undefined; }
const applicationProfile = manifest?.applicationProfile;
const apiExpected = ['api', 'management-portal'].includes(applicationProfile);
const frontendExpected = applicationProfile === 'management-portal';
const backgroundServiceExpected = applicationProfile === 'service';
const exactFoundationPaths = [
  'src/Back-end/Directory.Build.props',
  'src/Back-end/Directory.Packages.props',
  ...(frontendExpected
    ? ['src/Front-end/angular.json', 'src/Front-end/package.json', 'src/Front-end/projects']
    : ['src/Front-end/.gitkeep'])
];
const missingFoundationPaths = [];
for (const path of exactFoundationPaths) if (!(await pathExists(path))) missingFoundationPaths.push(path);
const rootWorkspaceFiles = sources
  .filter(file => !file.name.includes('/') && (
    ['angular.json', 'Directory.Build.props', 'Directory.Packages.props', 'proxy.conf.cjs', 'tsconfig.json'].includes(file.name)
    || file.name.endsWith('.slnx')
    || file.name.endsWith('.sln')
    || file.name === 'package.json' && /"@angular\//.test(file.content)
  ))
  .map(file => file.name);
const solutionFiles = sources.filter(file => file.name.endsWith('.slnx') || file.name.endsWith('.sln')).map(file => file.name);
const invalidSolutionFiles = solutionFiles.filter(file => !file.startsWith('src/Back-end/'));
const frontendWorkspaceFiles = sources
  .filter(file => file.name.startsWith('src/Front-end/') && (
    ['src/Front-end/angular.json', 'src/Front-end/package.json', 'src/Front-end/proxy.conf.cjs', 'src/Front-end/tsconfig.json'].includes(file.name)
    || file.name.startsWith('src/Front-end/projects/')
  ))
  .map(file => file.name);
const lifecycleBasePattern = /extends\s+(?:Nh(?:PageTypeBase|CollectionTypeBase|MutateBaseType|ModalComponentImpl|ModalMutateBase|[A-Za-z0-9]*(?:Page|Collection|Mutate|Modal)[A-Za-z0-9]*)Component)(?:<[^>{}]+>)?/;
const lifecycleHookPattern = /\bng(?:OnInit|OnChanges|OnDestroy|AfterViewInit|AfterContentInit)\s*\(/;
const directLifecycleOverrides = sources
  .filter(file => file.name.endsWith('.ts') && lifecycleBasePattern.test(file.content) && lifecycleHookPattern.test(file.content))
  .map(file => file.name);
const asideMutationFiles = sources
  .filter(file => file.name.endsWith('.html') && /<aside\b/i.test(file.content) && /<form\b|ngForm|formGroup/i.test(file.content))
  .map(file => file.name);

const report = {
  root,
  mode,
  manifest,
  newHeapPackages: [...packageReferences, ...npmPackages].sort(),
  projectFoundation: {
    directoryBuildProps: filesNamed('Directory.Build.props'),
    directoryPackagesProps: filesNamed('Directory.Packages.props'),
    solutions: solutionFiles,
    centralPackageManagement: matchingFiles(/<ManagePackageVersionsCentrally>\s*true\s*<\/ManagePackageVersionsCentrally>/i),
    projectFilesWithInlinePackageVersions: sources
      .filter(file => file.path.endsWith('.csproj') && hasInlinePackageVersion(file.content))
      .map(file => file.name),
    missingFoundationPaths,
    rootWorkspaceFiles,
    invalidSolutionFiles
  },
  providers: {
    selected: manifest?.databaseProvider,
    sqlServer: matchingFiles(/UseSqlServer|Microsoft\.EntityFrameworkCore\.SqlServer/i),
    postgreSql: matchingFiles(/UseNpgsql|Npgsql\.EntityFrameworkCore\.PostgreSQL|PostgreSql/i),
    inMemory: matchingFiles(/UseInMemoryDatabase/i)
  },
  angular: {
    rootRegistrations: matchingFiles(/NhCommonModule\.forRoot/),
    apiServices: matchingFiles(/extends\s+NhBaseApiService/),
    modalContent: matchingFiles(/NhModalMutateBaseComponent|NhModalComponentImpl/),
    getDeduplicationOptIns: matchingFiles(/deduplicateGetRequests\s*:\s*true/),
    deferredDropdownOptIns: matchingFiles(/deferLazyLoadUntilOpened\s*:\s*true/),
    directAngularLifecycleOverrides: directLifecycleOverrides,
    asideMutationFiles,
    genericStarterFiles: matchingFiles(/Congratulations! Your app is running|Welcome to your app/i)
  },
  backend: {
    dbContexts: matchingFiles(/\bNhIdentityDbContext\b/),
    protectedControllers: matchingFiles(/(?:DbEntityProtectedNhBaseController|CompositeDbEntityProtectedNhBaseController|ProtectedNhBaseController)/),
    apiPrefixedControllerRoutes: matchingFiles(/\[Route\("\/?api\//i),
    migrations: sources.filter(file => /(^|\/)Migrations\//i.test(file.name)).map(file => file.name),
    scalarOrOpenApi: matchingFiles(/AddOpenApi|MapOpenApi|MapScalarApiReference|EndpointSummary/),
    authenticationOverrides: matchingFiles(/WithAuthenticationService|IClaimsTransformation/)
  },
  optionalInfrastructure: {
    aspire: matchingFiles(/DistributedApplication\.CreateBuilder|Aspire\.Hosting\.AppHost/),
    docker: sources.filter(file => /(^|\/)(?:Dockerfile|compose\.ya?ml|docker-compose\.ya?ml)$/i.test(file.name)).map(file => file.name),
    elasticsearch: matchingFiles(/Elastic\.Clients\.Elasticsearch|NEST|AddElasticsearch|ElasticsearchClient/)
  },
  issues: []
};

const error = (code, message) => report.issues.push({ severity: 'error', code, message });
const warning = (code, message) => report.issues.push({ severity: 'warning', code, message });
if (mode !== 'inventory') {
  if (!manifest) error('manifest-missing', 'newheap-consumer.json is missing or invalid.');
  else if (!['service', 'api', 'management-portal'].includes(applicationProfile)) error('profile-invalid', 'applicationProfile must be service, api, or management-portal.');
  for (const path of missingFoundationPaths) error('foundation-path-missing', `Required foundation path is missing: ${path}`);
  for (const path of rootWorkspaceFiles) error('root-workspace-file', `Move workspace file out of the repository root: ${path}`);
  for (const path of invalidSolutionFiles) error('solution-location', `Move the solution into src/Back-end: ${path}`);
  if (!solutionFiles.some(path => path.startsWith('src/Back-end/'))) error('solution-missing', 'Create the .slnx in src/Back-end.');
  if (report.projectFoundation.centralPackageManagement.length === 0) error('central-packages-disabled', 'Enable central package management in src/Back-end/Directory.Packages.props.');
  for (const path of report.projectFoundation.projectFilesWithInlinePackageVersions) error('inline-package-version', `Remove inline package versions from ${path}.`);
  if (!packageReferences.has('NewHeap.Platform.Common')) error('backend-package-missing', 'Reference NewHeap.Platform.Common from a backend project.');
  if (apiExpected && !packageReferences.has('NewHeap.Platform.AspNet.Common')) error('backend-package-missing', 'Reference NewHeap.Platform.AspNet.Common from an API project.');
  if (frontendExpected && ![...npmPackages].some(value => value.startsWith('@newheap/platform-common@'))) error('frontend-package-missing', 'Install @newheap/platform-common in src/Front-end.');
  if (frontendExpected && !(await pathExists('src/Front-end/node_modules/@newheap/platform-common/package.json'))) error('npm-install-missing', 'Run npm install in src/Front-end and remove stale private-registry overrides first.');
  if (!frontendExpected && frontendWorkspaceFiles.length > 0) error('frontend-untracked', 'The current scope defers the frontend. Update the confirmed profile before creating an Angular workspace.');
  const applicationSuffix = backgroundServiceExpected ? 'Service' : 'Api';
  if (!(await pathExists(`src/Back-end/Applications/${manifest?.applicationName}.${applicationSuffix}/obj/project.assets.json`))) {
    error('nuget-restore-missing', 'Run dotnet restore from src/Back-end and remove stale private-feed overrides first.');
  }
  if (manifest?.databaseProvider === 'postgresql' && report.providers.postgreSql.length === 0) error('database-provider-missing', 'PostgreSQL is selected but no Npgsql provider reference was found.');
  if (manifest?.databaseProvider === 'sqlserver' && report.providers.sqlServer.length === 0) error('database-provider-missing', 'SQL Server is selected but no SQL Server provider reference was found.');
}

if (mode === 'validate') {
  if (frontendExpected && report.angular.rootRegistrations.length === 0) error('angular-root-config', 'Register NhCommonModule.forRoot(...) once in the management application root.');
  if (frontendExpected && report.angular.apiServices.length === 0) error('angular-api-base', 'Use NhBaseApiService for the management portal API layer.');
  if (frontendExpected && report.angular.modalContent.length === 0) error('angular-modal-base', 'Use NewHeap modal content and NhModalService for create/edit flows.');
  for (const path of directLifecycleOverrides) error('angular-lifecycle', `Use appOn... hooks instead of owned Angular lifecycle hooks: ${path}`);
  for (const path of asideMutationFiles) error('aside-mutation', `Move create/edit form content from an aside into a NewHeap modal: ${path}`);
  for (const path of report.angular.genericStarterFiles) error('generic-angular-starter', `Replace the generic Angular starter with the management portal shell: ${path}`);
  if (frontendExpected && report.backend.dbContexts.length === 0) error('identity-dbcontext', 'Use a consumer DbContext derived from NhIdentityDbContext for the authenticated management portal.');
  if ((frontendExpected || manifest?.capabilities?.authentication) && apiExpected && report.backend.protectedControllers.length === 0) error('protected-controller', 'Use a NewHeap protected/base controller with explicit authorization metadata for the selected authenticated profile.');
  if (apiExpected && report.backend.scalarOrOpenApi.length === 0) error('openapi-missing', 'Expose the API contract through OpenAPI and Scalar metadata.');
  if (apiExpected && report.backend.apiPrefixedControllerRoutes.length > 0) error('api-route-prefix', 'Backend controller routes must not repeat the browser /api proxy prefix.');
  const proxy = sourceByName.get('src/Front-end/proxy.conf.cjs')?.content ?? '';
  if (frontendExpected && !/pathRewrite[\s\S]*\^\/api[\s\S]*['"]{2}/.test(proxy)) error('proxy-contract', 'The frontend proxy must strip browser prefix /api before forwarding to backend routes.');
  if (manifest?.features?.aspire && report.optionalInfrastructure.aspire.length === 0) error('aspire-missing', 'Aspire was selected but no AppHost composition was found.');
  if (manifest?.features?.docker && report.optionalInfrastructure.docker.length === 0) error('docker-missing', 'Docker was selected but no Dockerfile or Compose definition was found.');
  if (manifest?.features?.elasticsearch && report.optionalInfrastructure.elasticsearch.length === 0) error('elasticsearch-missing', 'Elasticsearch was selected but no client registration or package was found.');
  if (!manifest?.features?.aspire && report.optionalInfrastructure.aspire.length > 0) warning('aspire-untracked', 'Aspire exists but is not enabled in newheap-consumer.json.');
  if (!manifest?.features?.elasticsearch && report.optionalInfrastructure.elasticsearch.length > 0) warning('elasticsearch-untracked', 'Elasticsearch exists but is not enabled in newheap-consumer.json.');
}

console.log(JSON.stringify(report, null, 2));
if (mode !== 'inventory' && report.issues.some(issue => issue.severity === 'error')) process.exitCode = 1;
