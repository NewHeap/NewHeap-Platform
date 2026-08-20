import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { repositoryRoot } from './lib.mjs';

const baseIndex = process.argv.indexOf('--base');
const base = baseIndex >= 0 ? process.argv[baseIndex + 1] : undefined;
const failures = [];

const snapshot = spawnSync(process.execPath, [resolve(repositoryRoot, 'tools', 'guidance', 'snapshot-public-api.mjs'), '--check'], {
  cwd: repositoryRoot,
  encoding: 'utf8'
});
if (snapshot.status !== 0) failures.push(snapshot.stderr || snapshot.stdout || 'Public API snapshot check failed.');

if (base) {
  const diff = spawnSync('git', ['diff', '--name-only', `${base}...HEAD`], { cwd: repositoryRoot, encoding: 'utf8' });
  if (diff.status !== 0) failures.push(diff.stderr || `Unable to compare with ${base}.`);
  else {
    const changed = diff.stdout.split(/\r?\n/).filter(Boolean).map(path => path.replaceAll('\\', '/'));
    const changedLibraryPaths = changed.filter(path =>
      /^src\/Back-end\/Libraries\/.+\.cs$/.test(path) ||
      /^src\/Front-end\/projects\/nh-common\/src\/.+\.ts$/.test(path)
    );
    if (changedLibraryPaths.length > 0) {
      const exceptions = JSON.parse(await readFile(resolve(repositoryRoot, 'guidance', 'impact-exceptions.json'), 'utf8'));
      const activeException = exceptions.exceptions?.some(item =>
        item.base === base &&
        item.expiresOn >= new Date().toISOString().slice(0, 10) &&
        changedLibraryPaths.every(path => item.pathPrefixes?.some(prefix => path.startsWith(prefix)))
      );
      if (!activeException) {
        const requirements = [
          ['executable SampleProjectManagement evidence', path => path.startsWith('examples/SampleProjectManagement/')],
          ['an atomic guidance rule', path => path.startsWith('guidance/rules/')],
          ['the canonical sample case registry', path => path === 'examples/SampleProjectManagement/docs/cases/sample-case-registry.json'],
          ['the reviewed public API snapshot', path => path === 'guidance/public-api-snapshot.json'],
          ['a guidance version bump', path => path === 'guidance/version.json'],
          ['a matching plugin version bump', path => path === 'plugins/newheap-platform/.codex-plugin/plugin.json']
        ];
        for (const [label, matches] of requirements) {
          if (!changed.some(matches)) failures.push(`Library files changed relative to ${base}, but the diff contains no ${label}.`);
        }
      }
    }
    const distributableGuidanceChanged = changed.some(path =>
      path.startsWith('guidance/rules/') ||
      path.startsWith('skills/newheap-') && !path.startsWith('skills/newheap-library-maintenance/') ||
      path.startsWith('plugins/newheap-platform/scripts/') ||
      path === 'plugins/newheap-platform/INSTALL.md' ||
      path === 'examples/SampleProjectManagement/docs/cases/sample-case-registry.json'
    );
    if (distributableGuidanceChanged) {
      if (!changed.includes('guidance/version.json')) failures.push('Distributable guidance changed without a guidance version bump.');
      if (!changed.includes('plugins/newheap-platform/.codex-plugin/plugin.json')) failures.push('Distributable guidance changed without a plugin version bump.');
      const previousGuidance = spawnSync('git', ['show', `${base}:guidance/version.json`], { cwd: repositoryRoot, encoding: 'utf8' });
      const previousPlugin = spawnSync('git', ['show', `${base}:plugins/newheap-platform/.codex-plugin/plugin.json`], { cwd: repositoryRoot, encoding: 'utf8' });
      const currentGuidance = JSON.parse(await readFile(resolve(repositoryRoot, 'guidance', 'version.json'), 'utf8'));
      const currentPlugin = JSON.parse(await readFile(resolve(repositoryRoot, 'plugins', 'newheap-platform', '.codex-plugin', 'plugin.json'), 'utf8'));
      if (previousGuidance.status === 0 && JSON.parse(previousGuidance.stdout).guidanceVersion === currentGuidance.guidanceVersion) {
        failures.push('Distributable guidance changed, but guidanceVersion was not incremented.');
      }
      if (previousPlugin.status === 0 && JSON.parse(previousPlugin.stdout).version === currentPlugin.version) {
        failures.push('Distributable guidance changed, but the plugin version was not incremented.');
      }
    }
  }
}

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log(base ? `Verified library-change impact relative to ${base}.` : 'Verified current public API snapshot.');
