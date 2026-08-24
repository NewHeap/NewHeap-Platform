import { access, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import {
  loadReleaseManifest,
  missingTargetFrameworks,
  projectTargetFrameworks,
  readJson,
  releaseTag,
  repositoryRoot,
  resolveRepositoryPath
} from './lib.mjs';

const manifest = await loadReleaseManifest();
const failures = [];
const workflowPaths = [
  '.github/workflows/release-contract.yml',
  '.github/workflows/prepare-release.yml',
  '.github/workflows/publish-preview.yml',
  '.github/workflows/publish-release.yml',
  '.github/workflows/finalize-pending-release.yml'
];

const packageReleaseTool = await readFile(resolveRepositoryPath('tools/release/package-release.mjs'), 'utf8');
if (!packageReleaseTool.includes('registry: manifest.registries.npm, access: manifest.packageVisibility')) {
  failures.push('npm release artifacts must inherit the public npmjs.org target from the release manifest.');
}
const packageVerifier = await readFile(resolveRepositoryPath('tools/release/verify-public-release-targets.mjs'), 'utf8');
if (!packageVerifier.includes('NPM_REGISTRY_URL')
  || !packageVerifier.includes('NUGET_FLAT_CONTAINER_URL')
  || !packageVerifier.includes("numericOption('attempts', 8")
  || !packageVerifier.includes('Anonymous ${releasePackage.packageType} registry request')
  || !packageVerifier.includes('version ${expectedVersion} was not found')) {
  failures.push('Package verification must use anonymous public registries, retry indexing and require every exact release version.');
}
const rootPackage = await readJson(resolveRepositoryPath('package.json'));
const frontEndWorkspacePackage = await readJson(resolveRepositoryPath('src/Front-end/package.json'));
for (const legacyScript of ['nh-common:publish', 'nh-toastr:publish']) {
  if (frontEndWorkspacePackage.scripts?.[legacyScript]) {
    failures.push(`src/Front-end/package.json must not expose legacy local publication script '${legacyScript}'.`);
  }
}
const requiredNpmPeers = {
  '@newheap/platform-common': [
    '@angular/common',
    '@angular/core',
    '@angular/forms',
    '@angular/platform-browser',
    '@angular/router',
    '@msgpack/msgpack',
    '@ngx-translate/core',
    '@sentry/angular',
    '@sentry/browser',
    '@sentry/core',
    '@swimlane/ngx-datatable',
    'js-base64',
    'luxon',
    'ngx-bootstrap-multiselect',
    'ngx-cookie-service',
    'ngx-toastr',
    'rxjs'
  ],
  '@newheap/nh-toastr': [
    '@angular/common',
    '@angular/core',
    '@ngx-translate/core',
    'rxjs'
  ]
};
if (!rootPackage.scripts?.['release:test']?.includes('test-verify-public-release-targets.mjs')) {
  failures.push('release:test must exercise anonymous registry access, delayed package visibility and exact-version verification.');
}
const prepareReleaseTool = await readFile(resolveRepositoryPath('tools/release/prepare-release.mjs'), 'utf8');
if (!/runGuidanceTool\(\s*'snapshot-public-api\.mjs',\s*\['--check'\]/.test(prepareReleaseTool)
  || !/runGuidanceTool\(\s*'snapshot-public-api\.mjs',\s*\[\]/.test(prepareReleaseTool)) {
  failures.push('Release preparation must reject existing public API drift and refresh the snapshot after changing guidance versions.');
}
for (const [id, unit] of Object.entries(manifest.units)) {
  releaseTag(unit);
  if (unit.kind === 'nuget') {
    for (const project of unit.projects) {
      try { await access(resolveRepositoryPath(project.path)); }
      catch { failures.push(`${id}: missing project ${project.path}.`); }
    }
    if (unit.replaces) {
      try {
        await access(resolveRepositoryPath(unit.replaces));
        failures.push(`${id}: legacy script ${unit.replaces} still exists.`);
      } catch { /* The replacement is complete when the legacy script is gone. */ }
    }
  }
  if (unit.kind === 'npm') {
    const packageJson = await readJson(resolveRepositoryPath(unit.packageJson));
    if (packageJson.name !== unit.packageName) failures.push(`${id}: package name mismatch.`);
    if (packageJson.version !== unit.version) failures.push(`${id}: package version ${packageJson.version} does not match ${unit.version}.`);
    const readmePath = unit.packageJson.replace(/package\.json$/, 'README.md');
    const readme = await readFile(resolveRepositoryPath(readmePath), 'utf8');
    if (readme.includes('pkgs.dev.azure.com') || readme.includes('vsts-npm-auth') || readme.includes('npm.pkg.github.com') || readme.includes('NODE_AUTH_TOKEN')) {
      failures.push(`${readmePath}: public npm installation must not require a private feed or token.`);
    }
    for (const required of [
      `npm install ${unit.packageName}`,
      `https://www.npmjs.com/package/${unit.packageName}`
    ]) {
      if (!readme.includes(required)) failures.push(`${readmePath}: missing public npm installation guidance '${required}'.`);
    }
    if (packageJson.license !== 'Apache-2.0'
      || packageJson.repository?.url !== 'git+https://github.com/NewHeap/NewHeap-Platform.git'
      || packageJson.publishConfig?.registry !== manifest.registries.npm
      || packageJson.publishConfig?.access !== manifest.packageVisibility) {
      failures.push(`${unit.packageJson}: public package metadata is incomplete or does not match the release manifest.`);
    }
    for (const dependency of requiredNpmPeers[unit.packageName] ?? []) {
      const declaredRange = packageJson.peerDependencies?.[dependency];
      const workspaceRange = frontEndWorkspacePackage.dependencies?.[dependency];
      if (!declaredRange) {
        failures.push(`${unit.packageJson}: missing public runtime peer dependency ${dependency}.`);
      } else if (workspaceRange && declaredRange !== workspaceRange) {
        failures.push(`${unit.packageJson}: peer dependency ${dependency} ${declaredRange} must match the tested workspace range ${workspaceRange}.`);
      }
    }
  }
}

const media = manifest.units['nuget-media'];
if (!manifest.units['nuget-common'].includeSymbols || !manifest.units['nuget-caching'].includeSymbols || !media.includeSymbols) {
  failures.push('Every public NuGet release unit must publish Portable PDB symbol packages.');
}
if (!packageReleaseTool.includes('validatePackageArtifacts')) {
  failures.push('NuGet packaging must validate produced artifacts before checksums and publication.');
}
const [centralBuildProperties, centralBuildTargets] = await Promise.all([
  readFile(resolveRepositoryPath('src/Back-end/Directory.Build.props'), 'utf8'),
  readFile(resolveRepositoryPath('src/Back-end/Directory.Build.targets'), 'utf8')
]);
for (const [property, value] of [
  ['DebugType', 'portable'],
  ['EmbedAllSources', 'false'],
  ['EmbedUntrackedSources', 'true'],
  ['IncludeSymbols', 'true'],
  ['SymbolPackageFormat', 'snupkg']
]) {
  if (!centralBuildProperties.includes(`<${property}>${value}</${property}>`)) {
    failures.push(`Packable NuGet projects must centrally set ${property}=${value}.`);
  }
}
if (!centralBuildTargets.includes('RemoveSentryProjectDirectoryMetadata')
  || !centralBuildTargets.includes('$(SentryAttributesFilePath)')
  || !centralBuildTargets.includes('<Compile Remove="$(SentryAttributesFilePath)"')) {
  failures.push('Packable NuGet projects must exclude Sentry.ProjectDirectory source generation before compilation.');
}
for (const [id, unit] of Object.entries(manifest.units)) {
  if (unit.kind !== 'nuget') continue;
  for (const project of unit.projects) {
    const projectSource = await readFile(resolveRepositoryPath(project.path), 'utf8');
    if (!/<PackageDescription>(?!Package Description<)[^<]+<\/PackageDescription>/i.test(projectSource)) {
      failures.push(`${id}: ${project.path} must declare a meaningful PackageDescription.`);
    }
    if (!/<PackageTags>[^<]+<\/PackageTags>/i.test(projectSource)) {
      failures.push(`${id}: ${project.path} must declare PackageTags.`);
    }
    if (/<EmbedAllSources>\s*true\s*<\/EmbedAllSources>/i.test(projectSource)
      || /<DebugType>\s*embedded\s*<\/DebugType>/i.test(projectSource)) {
      failures.push(`${id}: ${project.path} must not embed source code or PDBs in the library assembly.`);
    }
  }
}
const commonProject = manifest.units['nuget-common'].projects.find(project => project.packageId === 'NewHeap.Platform.Common');
const mediaCoreProject = media.projects.find(project => project.packageId === 'NewHeap.Platform.Media.Core');
const [commonProjectSource, mediaCoreProjectSource, centralPackageVersions] = await Promise.all([
  readFile(resolveRepositoryPath(commonProject.path), 'utf8'),
  readFile(resolveRepositoryPath(mediaCoreProject.path), 'utf8'),
  readFile(resolveRepositoryPath('src/Back-end/Directory.Packages.props'), 'utf8')
]);
const commonVersionMatch = centralPackageVersions.match(/<PackageVersion Include="NewHeap\.Platform\.Common" Version="([^"]+)"\s*\/?>/);
if (commonVersionMatch?.[1] !== manifest.units['nuget-common'].version) {
  failures.push(`NewHeap.Platform.Common dependency version ${commonVersionMatch?.[1] ?? '(missing)'} does not match the nuget-common release version ${manifest.units['nuget-common'].version}.`);
}
const commonTargetFrameworks = projectTargetFrameworks(commonProjectSource, commonProject.packageId);
const mediaCoreTargetFrameworks = projectTargetFrameworks(mediaCoreProjectSource, mediaCoreProject.packageId);
for (const framework of missingTargetFrameworks(commonTargetFrameworks, mediaCoreTargetFrameworks)) {
  failures.push(`${mediaCoreProject.packageId} targets ${framework}, but its ${commonProject.packageId} dependency does not.`);
}
if (!mediaCoreProjectSource.includes("Condition=\"'$(UseLocalNewHeapProjects)' == 'true'\"")
  || !mediaCoreProjectSource.includes('<ProjectReference Include="..\\NewHeap.Platform.Common\\NewHeap.Platform.Common.csproj" />')
  || !mediaCoreProjectSource.includes("Condition=\"'$(UseLocalNewHeapProjects)' != 'true'\"")
  || !packageReleaseTool.includes("UseLocalNewHeapProjects: 'false'")) {
  failures.push('Media.Core must build against local Common source while release packaging restores the declared public package dependency.');
}
for (const provider of ['NewHeap.Platform.Media.FileStructureStorage.SqlServer', 'NewHeap.Platform.Media.FileStructureStorage.PostgreSql']) {
  if (!media.projects.some(project => project.packageId === provider)) failures.push(`nuget-media: missing provider package ${provider}.`);
}
const [sqlServerProviderProject, postgreSqlProviderProject] = await Promise.all([
  readFile(resolveRepositoryPath('src/Back-end/Libraries/NewHeap.Platform.Media.FileStructureStorage.SqlServer/NewHeap.Platform.Media.FileStructureStorage.SqlServer.csproj'), 'utf8'),
  readFile(resolveRepositoryPath('src/Back-end/Libraries/NewHeap.Platform.Media.FileStructureStorage.PostgreSql/NewHeap.Platform.Media.FileStructureStorage.PostgreSql.csproj'), 'utf8')
]);
if (!sqlServerProviderProject.includes('..\\NewHeap.Platform.Media.Core\\NewHeap.Platform.Media.Core.csproj')) {
  failures.push('The SQL Server file-structure provider must depend on the neutral Media.Core package boundary.');
}
if (!postgreSqlProviderProject.includes('..\\NewHeap.Platform.Media.Core\\NewHeap.Platform.Media.Core.csproj')
  || /NewHeap\.Platform\.Media\.FileStructureStorage\.SqlServer/i.test(postgreSqlProviderProject)) {
  failures.push('The PostgreSQL file-structure provider must depend directly on Media.Core and never on the SQL Server provider.');
}
const mediaBundle = await readFile(resolveRepositoryPath('src/Back-end/Libraries/NewHeap.Platform.Media/NewHeap.Platform.Media.csproj'), 'utf8');
for (const dependency of ['Media.Core', 'Media.FileStructureStorage.SqlServer', 'Media.Http', 'Media.MediaStorage.FileSystem']) {
  if (!mediaBundle.includes(`<ProjectReference Include="..\\NewHeap.Platform.${dependency}\\`)) {
    failures.push(`Media bundle does not use a project reference for NewHeap.Platform.${dependency}.`);
  }
}
if (mediaBundle.includes('<PackageReference Include="NewHeap.Platform.Media.')) {
  failures.push('Media bundle must not require an already-published version of packages from the same release.');
}

const pluginUnit = manifest.units['newheap-platform-plugin'];
const guidance = await readJson(resolve(repositoryRoot, 'guidance', 'version.json'));
const plugin = await readJson(resolve(repositoryRoot, 'plugins', 'newheap-platform', '.codex-plugin', 'plugin.json'));
const pluginVersion = plugin.version;
const guidanceVersion = guidance.guidanceVersion;
if (guidanceVersion !== pluginVersion || pluginVersion !== pluginUnit.version) {
  failures.push('Guidance and plugin versions must match the released manifest version; Prepare release owns every version bump.');
}

for (const workflowPath of workflowPaths) {
  let workflow;
  try { workflow = await readFile(resolveRepositoryPath(workflowPath), 'utf8'); }
  catch { failures.push(`Missing workflow ${workflowPath}.`); continue; }
  for (const privateRegistry of ['pkgs.dev.azure.com', 'npm.pkg.github.com', 'nuget.pkg.github.com']) {
    if (workflow.includes(privateRegistry)) failures.push(`${workflowPath}: must not use private registry ${privateRegistry}.`);
  }
  if (/\+\s+--/.test(workflow)) failures.push(`${workflowPath}: contains a malformed multiline shell command.`);
  for (const match of workflow.matchAll(/^\s*uses:\s+([^\s@]+)@([^\s#]+)/gm)) {
    const [, action, reference] = match;
    if (!action.startsWith('./') && !/^[0-9a-f]{40}$/.test(reference)) {
      failures.push(`${workflowPath}: external action ${action} is not pinned to a full commit SHA.`);
    }
  }
  if (workflow.includes('gh pr checks')) {
    failures.push(`${workflowPath}: must not depend on GraphQL status-check access through gh pr checks.`);
  }
  if (workflowPath.endsWith('publish-preview.yml') || workflowPath.endsWith('publish-release.yml')) {
    if (!workflow.includes('*.symbols.nupkg')) failures.push(`${workflowPath}: must exclude legacy symbol packages from registry publication.`);
    if ((workflow.match(/verify-public-release-targets\.mjs/g) ?? []).length < 2) failures.push(`${workflowPath}: must verify anonymous public targets both before and after publication.`);
    if (!workflow.includes('https://api.nuget.org/v3/index.json')) failures.push(`${workflowPath}: NuGet publication must target nuget.org.`);
    if (!workflow.includes('id-token: write') || !workflow.includes('NuGet/login@d22cc5f58ff5b88bf9bd452535b4335137e24544')) {
      failures.push(`${workflowPath}: public publication must use pinned OIDC trusted publishing for NuGet.`);
    }
  }
  if (workflowPath.endsWith('prepare-release.yml')) {
    if (!workflow.includes('ref: main')
      || !workflow.includes('uses: ./.github/workflows/release-contract.yml')
      || !workflow.includes('source_branch: ${{ needs.prepare.outputs.source_branch }}')
      || !workflow.includes('source_sha: ${{ needs.prepare.outputs.source_sha }}')
      || !workflow.includes('base_sha: ${{ needs.prepare.outputs.base_sha }}')
      || !workflow.includes('git commit -m "$title"')
      || workflow.includes('skip-checks: true')
      || workflow.includes('gh pr create')
      || workflow.includes('gh workflow run')) {
      failures.push(`${workflowPath}: preparation must create one release commit from main and call the reusable automatic release contract directly.`);
    }
    if ((workflow.match(/npm run skills:validate/g) ?? []).length < 2) {
      failures.push(`${workflowPath}: guidance state must be validated both before and after release preparation.`);
    }
    if (!workflow.includes('- all')) failures.push(`${workflowPath}: must support selecting all release units.`);
    if (!workflow.includes('refresh-plugin:')
      || !workflow.includes('name: Queue plugin compatibility release')
      || !workflow.includes('needs: release')
      || !workflow.includes("inputs.component != 'all' && inputs.component != 'newheap-platform-plugin'")
      || !workflow.includes('actions: write')
      || !workflow.includes('actions/workflows/prepare-release.yml/dispatches')
      || !workflow.includes('inputs[component]=newheap-platform-plugin')
      || !workflow.includes('inputs[bump]=patch')) {
      failures.push(`${workflowPath}: successful individual package releases must queue one guarded plugin patch release.`);
    }
  }
  if (workflowPath.endsWith('release-contract.yml')) {
    if (!workflow.includes('- main') || workflow.includes('- staging') || workflow.includes('- production')) {
      failures.push(`${workflowPath}: package release validation must use main as its only long-lived branch.`);
    }
    for (const input of ['component:', 'version:', 'source_branch:', 'source_sha:', 'base_sha:']) {
      if (!workflow.includes(input)) failures.push(`${workflowPath}: automatic release input ${input} is missing.`);
    }
    if (!workflow.includes('workflow_call:')
      || workflow.includes('workflow_dispatch:')
      || workflow.includes('pull-requests: write')
      || workflow.includes('statuses: write')
      || workflow.includes('/pulls/')
      || !workflow.includes('current_main')
      || !workflow.includes('if [[ "$current_main" != "$BASE_SHA" ]]')
      || !workflow.includes('repos/${GITHUB_REPOSITORY}/git/refs/heads/main')
      || !workflow.includes('--method PATCH')
      || !workflow.includes('--raw-field sha="$SOURCE_SHA"')
      || !workflow.includes('--field force=false')
      || !workflow.includes('uses: ./.github/workflows/publish-release.yml')
      || !workflow.includes('release_sha: ${{ needs.complete-release.outputs.release_sha }}')) {
      failures.push(`${workflowPath}: the validated release must fast-forward main through the GitHub REST API and pass that exact SHA to the reusable publisher.`);
    }
    if (!workflow.includes('verify-change-impact.mjs --base "${{ inputs.base_sha }}" --release')) {
      failures.push(`${workflowPath}: generated release commits must use explicit release-mode impact validation.`);
    }
    for (const auditMarker of [
      'NuGetAuditMode=all',
      'WarningsAsErrors=NU1903%3BNU1904',
      'npm --prefix src/Front-end audit --audit-level=critical',
      'npm --prefix src/Front-end audit --omit=dev --audit-level=high',
      'npm --prefix examples/SampleProjectManagement/src/Front-end audit --audit-level=critical',
      'npm --prefix examples/SampleProjectManagement/src/Front-end audit --omit=dev --audit-level=high'
    ]) {
      if (!workflow.includes(auditMarker)) failures.push(`${workflowPath}: missing public dependency audit '${auditMarker}'.`);
    }
  }
  if (workflowPath.endsWith('publish-preview.yml')) {
    if (!workflow.includes('public-package-preview')
      || !workflow.includes("github.ref_name == 'main'")
      || !workflow.includes('--ref main')
      || !workflow.includes('-ci.')) {
      failures.push(`${workflowPath}: preview packages must be built from main with an immutable prerelease suffix.`);
    }
    if (!workflow.includes('--version "${{ needs.pack.outputs.version }}"')) {
      failures.push(`${workflowPath}: post-publication verification must require the exact immutable preview version.`);
    }
    if (!workflow.includes('NuGetAuditMode=all') || !workflow.includes('WarningsAsErrors=NU1903%3BNU1904')) {
      failures.push(`${workflowPath}: preview packaging must reject high and critical NuGet advisories.`);
    }
  }
  if (workflowPath.endsWith('publish-release.yml')) {
    if (!workflow.includes('workflow_call:')
      || workflow.includes('workflow_dispatch:')
      || workflow.includes('production')
      || workflow.includes('staging')
      || !workflow.includes('ref: ${{ inputs.release_sha }}')
      || !workflow.includes('git merge-base --is-ancestor "$RELEASE_SHA" origin/main')) {
      failures.push(`${workflowPath}: stable publication must be reusable-only and build the exact validated commit contained in main.`);
    }
    if (!workflow.includes('--access public --registry https://registry.npmjs.org')) {
      failures.push(`${workflowPath}: npm publication must explicitly use public npmjs.org access.`);
    }
    if (!workflow.includes('npm audit --audit-level=critical')
      || !workflow.includes('npm audit --omit=dev --audit-level=high')) {
      failures.push(`${workflowPath}: npm publication must reject high and critical runtime dependency advisories.`);
    }
    if (!workflow.includes('REQUESTED_VERSION') || !workflow.includes('add-local-nuget-source.mjs')) {
      failures.push(`${workflowPath}: release-all must confirm its selection and restore dependent units from the locally packed common artifacts.`);
    }
    if (!workflow.includes('--field components') || !workflow.includes('--field nugetComponents') || !workflow.includes('--field npmComponents')) {
      failures.push(`${workflowPath}: release-all component lists must be derived from the release manifest.`);
    }
    if (!/\r?\n  finalize:\r?\n/.test(workflow)
      || !workflow.includes('needs: publish')
      || !workflow.includes('Verify exact public package versions are anonymously readable')
      || !workflow.includes('Validate and publish immutable GitHub releases')
      || !workflow.includes('Release ${tag} must contain artifacts and SHA256SUMS before publication.')) {
      failures.push(`${workflowPath}: registry publication and retry-safe GitHub Release finalization must be separate jobs.`);
    }
  }
  if (workflowPath.endsWith('finalize-pending-release.yml')) {
    if (!workflow.includes('workflow_dispatch:')
      || !workflow.includes('ref: main')
      || !workflow.includes('verify-public-release-targets.mjs')
      || !workflow.includes('git merge-base --is-ancestor "$release_sha" origin/main')
      || !workflow.includes('Release ${tag} must contain artifacts and SHA256SUMS before finalization.')
      || !workflow.includes('gh release edit "$tag" --draft=false --latest=false')
      || workflow.includes('npm publish')
      || workflow.includes('dotnet nuget push')
      || workflow.includes('prepare-release.mjs')) {
      failures.push(`${workflowPath}: recovery must only verify current public versions and finalize complete drafts from a commit contained in main.`);
    }
  }
  const selectable = workflowPath.endsWith('publish-preview.yml')
    ? Object.keys(manifest.units).filter(id => id.startsWith('nuget-'))
    : workflowPath.endsWith('prepare-release.yml') || workflowPath.endsWith('release-contract.yml') || workflowPath.endsWith('finalize-pending-release.yml')
      ? Object.keys(manifest.units)
      : [];
  for (const id of selectable) {
    if (!workflow.includes(id)) failures.push(`${workflowPath}: release unit ${id} is not selectable.`);
  }
}

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log(`Validated ${Object.keys(manifest.units).length} release units and ${workflowPaths.length} GitHub workflows.`);
