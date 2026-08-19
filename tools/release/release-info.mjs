import { loadReleaseManifest, parseArguments, releaseSelection, releaseTag } from './lib.mjs';

const options = parseArguments(process.argv.slice(2));
if (!options.component) throw new Error('Usage: node tools/release/release-info.mjs --component <id|all> [--field version|tag|tagPrefix|kind|displayName|summary|components|nugetComponents|npmComponents]');
const manifest = await loadReleaseManifest();
const releases = releaseSelection(manifest, options.component).map(({ id, unit }) => ({
  component: id,
  version: unit.version,
  tag: releaseTag(unit),
  tagPrefix: unit.tagPrefix,
  kind: unit.kind,
  displayName: unit.displayName
}));
const info = options.component === 'all'
  ? {
      component: 'all',
      releases,
      summary: releases.map(release => `${release.component}=${release.version}`).join(', '),
      components: releases.map(release => release.component),
      nugetComponents: releases.filter(release => release.kind === 'nuget').map(release => release.component),
      npmComponents: releases.filter(release => release.kind === 'npm').map(release => release.component)
    }
  : releases[0];
if (options.field) {
  if (!(options.field in info)) throw new Error(`Unknown field '${options.field}'.`);
  const value = info[options.field];
  console.log(Array.isArray(value) ? value.join('\n') : value);
} else console.log(JSON.stringify(info));
