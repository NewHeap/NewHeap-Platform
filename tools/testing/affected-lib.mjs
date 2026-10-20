// Pure helpers of `npm run test:affected`: which test projects and front-end libraries a change
// reaches, and whether it needs both database providers. Kept free of I/O so they can be tested.

/** Normalizes a repository-relative path to forward slashes without a leading `./`. */
export function normalizePath(path) {
  return path.replaceAll('\\', '/').replace(/^\.\//, '');
}

/** Resolves `reference` relative to the directory of `fromFile`, both repository-relative. */
export function resolveRelative(fromFile, reference) {
  const parts = normalizePath(fromFile).split('/').slice(0, -1);
  for (const segment of normalizePath(reference).split('/')) {
    if (segment === '..') {
      parts.pop();
    } else if (segment !== '.' && segment !== '') {
      parts.push(segment);
    }
  }
  return parts.join('/');
}

/** The project references of a `.csproj`, as repository-relative project paths. */
export function parseProjectReferences(projectPath, content) {
  const references = [];
  for (const match of content.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
    references.push(resolveRelative(projectPath, match[1]));
  }
  return references;
}

/**
 * Whether a project runs tests: it references a test framework or says `IsTestProject`. A project
 * under `Tests` that is an executable, such as an AOT smoke test, is not one.
 */
export function isTestProject(content) {
  return /<IsTestProject>\s*true\s*<\/IsTestProject>/i.test(content)
    || /<PackageReference\s+Include="(xunit(\.v3)?|Microsoft\.NET\.Test\.Sdk|NUnit|MSTest\.TestFramework)"/i.test(content);
}

/** The project that owns `file`: the project whose folder is the longest prefix of the file's path. */
export function ownerProject(file, projects) {
  const path = normalizePath(file);
  let owner = null;
  for (const project of projects) {
    const folder = project.slice(0, project.lastIndexOf('/') + 1);
    if (path.startsWith(folder) && (owner === null || folder.length > owner.slice(0, owner.lastIndexOf('/') + 1).length)) {
      owner = project;
    }
  }
  return owner;
}

/**
 * Build files that change every project below their folder, such as `Directory.Packages.props`.
 * A change to one of them marks every project in that folder as changed.
 */
export const sharedBuildFiles = ['Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config', 'nuget.config'];

/**
 * The .NET test projects a change reaches: those whose own files changed and those that depend,
 * directly or through other projects, on a changed project.
 * @param {string[]} changedFiles repository-relative paths
 * @param {Map<string, string[]>} graph project path to the project paths it references
 * @param {Set<string>} testProjects the projects that run tests
 */
export function affectedTestProjects(changedFiles, graph, testProjects) {
  const projects = [...graph.keys()];
  const changed = new Set();
  for (const file of changedFiles.map(normalizePath)) {
    const name = file.slice(file.lastIndexOf('/') + 1);
    if (sharedBuildFiles.includes(name)) {
      const folder = file.slice(0, file.lastIndexOf('/') + 1);
      projects.filter(project => project.startsWith(folder)).forEach(project => changed.add(project));
      continue;
    }
    if (graph.has(file)) {
      changed.add(file);
      continue;
    }
    const owner = ownerProject(file, projects);
    if (owner !== null) {
      changed.add(owner);
    }
  }

  const dependents = new Map(projects.map(project => [project, []]));
  for (const [project, references] of graph) {
    for (const reference of references) {
      dependents.get(reference)?.push(project);
    }
  }

  const reached = new Set(changed);
  const queue = [...changed];
  while (queue.length > 0) {
    for (const dependent of dependents.get(queue.shift()) ?? []) {
      if (!reached.has(dependent)) {
        reached.add(dependent);
        queue.push(dependent);
      }
    }
  }

  return [...reached].filter(project => testProjects.has(project)).sort();
}

/** The Angular libraries under `src/Front-end/projects` whose files changed. */
export function affectedFrontEndLibraries(changedFiles, libraries) {
  const affected = new Set();
  for (const file of changedFiles.map(normalizePath)) {
    const match = /^src\/Front-end\/projects\/([^/]+)\//.exec(file);
    if (match && libraries.includes(match[1])) {
      affected.add(match[1]);
    }
  }
  return [...affected].sort();
}

/**
 * The `npx` arguments that test one Angular library once. Karma runs it in headless Chrome: a
 * visible Chrome window that is hidden or minimized throttles timers and focus, and specs that need
 * a visible page, such as the collaboration specs of `nh-ai-chat`, then fail.
 */
export function angularTestArguments(library) {
  return ['ng', 'test', library, '--watch=false', '--browsers=ChromeHeadless'];
}

/**
 * Changes that concern how data is stored, which must be verified on SQL Server and PostgreSQL:
 * migrations, EF models and contexts, stores, repositories, provider projects and provider tests.
 */
const persistencePatterns = [
  /\/Migrations\//,
  /\/Persistence\//,
  /\/DAL\//,
  /\/Entities\//,
  /DbContext[^/]*\.cs$/,
  /Repository[^/]*\.cs$/,
  /Store\.cs$/,
  /\.(SqlServer|PostgreSql|Sqlite)\//,
  /ProviderTests\.cs$/,
  /DatabaseFixture[^/]*\.cs$/
];

/** Whether a change needs both database providers. */
export function needsAllProviders(changedFiles) {
  return changedFiles.map(normalizePath).some(file => persistencePatterns.some(pattern => pattern.test(file)));
}

/**
 * The `dotnet test` filter that runs the tests of one provider: SQL Server variants are left out and
 * PostgreSQL, the faster container, stands for both. `null` runs every test.
 */
export function providerFilter(allProviders) {
  return allProviders ? null : 'DisplayName!~SqlServer';
}
