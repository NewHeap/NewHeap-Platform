import { mkdir, readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { relative, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  isPackageSemver,
  loadReleaseManifest,
  parseArguments,
  repositoryRoot,
  resolveRepositoryPath
} from './lib.mjs';

const usage = 'Usage: npm run release:sql-scripts -- --out <directory> [--version <semver>] [--schema <name>] [--dry-run]';

/**
 * The idempotent SQL scripts that a release publishes for a library-owned schema (FB92), for applications
 * that apply migrations themselves: `dotnet ef migrations script --idempotent` from an empty database,
 * written by each provider project's design-time factory. A release unit publishes the scripts of the
 * provider packages it contains.
 */
export const schemaScripts = [
  {
    name: 'newheap-assistant-schema',
    context: 'NhAssistantDbContext',
    provider: 'sqlserver',
    packageId: 'NewHeap.Platform.AI.Chat.SqlServer',
    project: 'src/Back-end/Libraries/NewHeap.Platform.AI.Chat.SqlServer/NewHeap.Platform.AI.Chat.SqlServer.csproj'
  },
  {
    name: 'newheap-assistant-schema',
    context: 'NhAssistantDbContext',
    provider: 'postgresql',
    packageId: 'NewHeap.Platform.AI.Chat.PostgreSql',
    project: 'src/Back-end/Libraries/NewHeap.Platform.AI.Chat.PostgreSql/NewHeap.Platform.AI.Chat.PostgreSql.csproj'
  }
];

/** The schema scripts of a release unit: those of the provider packages it contains. */
export function schemaScriptsOf(unit) {
  if (unit.kind !== 'nuget') {
    return [];
  }

  const packageIds = new Set(unit.projects.map(project => project.packageId));
  return schemaScripts.filter(script => packageIds.has(script.packageId));
}

/**
 * The file name of a script for `version`, such as `newheap-assistant-schema-sqlserver-32.0.0.sql`; a
 * script for another schema than the default ends with that schema.
 */
export function schemaScriptFileName(script, version, schema) {
  return `${script.name}-${script.provider}-${version}${schema ? `-${schema}` : ''}.sql`;
}

/**
 * The command that writes `script` into `outputDirectory`: the EF tools with the provider project as
 * project and startup project, so its design-time factory creates the context. `noBuild` reuses the
 * build of `configuration`, as release packaging does after `dotnet pack`; `schema` writes the script
 * for another schema than the default (`-- --schema <name>`).
 */
export function schemaScriptCommand(script, { version, outputDirectory, configuration = 'Release', noBuild = false, schema }) {
  if (!isPackageSemver(version ?? '')) throw new Error(`Invalid package version: ${version}`);
  if (schema !== undefined && !/^[A-Za-z0-9_]{1,64}$/.test(schema)) {
    throw new Error('--schema must contain only ASCII letters, digits and underscores.');
  }

  const project = resolveRepositoryPath(script.project);
  return {
    command: 'dotnet',
    arguments: [
      'ef', 'migrations', 'script',
      '--idempotent',
      '--project', project,
      '--startup-project', project,
      '--context', script.context,
      '--configuration', configuration,
      ...(noBuild ? ['--no-build'] : []),
      '--output', resolve(outputDirectory, schemaScriptFileName(script, version, schema)),
      ...(schema ? ['--', '--schema', schema] : [])
    ]
  };
}

/**
 * The version of the EF Core tools that match the EF Core packages of the repository, which the release
 * workflows install to write the scripts.
 */
export async function efToolVersion() {
  const versions = await readFile(resolve(repositoryRoot, 'src', 'Back-end', 'Directory.Packages.props'), 'utf8');
  const match = versions.match(/<PackageVersion Include="Microsoft\.EntityFrameworkCore\.Design" Version="([^"]+)"\s*\/?>/);
  if (!match) throw new Error('Directory.Packages.props has no Microsoft.EntityFrameworkCore.Design version.');
  return match[1];
}

/**
 * Checks the content of a written script: an idempotent script of the migrations history, without a
 * schema placeholder that only a host's SQL generator replaces, and without local paths. Returns the
 * failures.
 */
export function validateSchemaScript({ fileName, content }) {
  const failures = [];
  if (content.trim().length === 0) failures.push(`${fileName}: the script is empty.`);
  if (!content.includes('__EFMigrationsHistory') || !content.includes('IF NOT EXISTS')) {
    failures.push(`${fileName}: not an idempotent migrations script.`);
  }
  if (content.includes('{nhai}')) failures.push(`${fileName}: contains the unrouted schema placeholder {nhai}.`);
  if (/[A-Z]:\\Users\\|\/(?:Users|home)\/[^/\s]+\//i.test(content)) failures.push(`${fileName}: contains a local user path.`);
  return failures;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const options = parseArguments(process.argv.slice(2));
  if (options['tool-version']) {
    console.log(await efToolVersion());
    process.exit(0);
  }
  if (typeof options.out !== 'string') throw new Error(usage);

  const manifest = await loadReleaseManifest();
  const [component, unit] = Object.entries(manifest.units).find(([, candidate]) => schemaScriptsOf(candidate).length > 0);
  const version = typeof options.version === 'string' ? options.version : unit.version;
  const schema = typeof options.schema === 'string' ? options.schema : undefined;
  const outputDirectory = resolve(process.env.INIT_CWD ?? process.cwd(), options.out);
  const dryRun = Boolean(options['dry-run']);
  const commands = schemaScriptsOf(unit).map(script => schemaScriptCommand(script, { version, outputDirectory, schema }));
  if (!dryRun) {
    await mkdir(outputDirectory, { recursive: true });
    for (const { command, arguments: argumentsList } of commands) {
      const result = spawnSync(command, argumentsList, { cwd: repositoryRoot, stdio: 'inherit', shell: false });
      if (result.error) throw new Error(`${command} failed to start: ${result.error.message}`);
      if (result.status !== 0) throw new Error(`${command} failed with exit code ${result.status}.`);
    }
    for (const script of schemaScriptsOf(unit)) {
      const fileName = schemaScriptFileName(script, version, schema);
      const failures = validateSchemaScript({ fileName, content: await readFile(resolve(outputDirectory, fileName), 'utf8') });
      if (failures.length > 0) throw new Error(failures.join('\n'));
    }
  }

  console.log(JSON.stringify({
    component,
    version,
    output: outputDirectory,
    files: schemaScriptsOf(unit).map(script => schemaScriptFileName(script, version, schema)),
    commands: commands.map(({ command, arguments: argumentsList }) => ({
      command,
      arguments: argumentsList.map(argument => argument.startsWith(repositoryRoot) ? relative(repositoryRoot, argument) : argument)
    }))
  }));
}
