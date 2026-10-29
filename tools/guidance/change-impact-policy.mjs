export const releaseVersionPaths = [
  'guidance/version.json',
  'plugins/newheap-platform/.codex-plugin/plugin.json'
];

export function validateLibraryChangeEvidence(changed, base) {
  // verify-change-impact checks the current API snapshot against source independently.
  // A behavioral fix can keep that verified snapshot byte-for-byte unchanged.
  const requirements = [
    ['executable SampleProjectManagement evidence', path => path.startsWith('examples/SampleProjectManagement/')],
    ['an atomic guidance rule', path => path.startsWith('guidance/rules/')],
    ['the canonical sample case registry', path => path === 'examples/SampleProjectManagement/docs/cases/sample-case-registry.json']
  ];
  return requirements
    .filter(([, matches]) => !changed.some(matches))
    .map(([label]) => `Library files changed relative to ${base}, but the diff contains no ${label}.`);
}

export function validateReleaseVersionPolicy({
  changed,
  releaseMode,
  distributableGuidanceChanged,
  previousGuidanceVersion,
  currentGuidanceVersion,
  previousPluginVersion,
  currentPluginVersion,
  changedManifestVersionUnits = []
}) {
  const failures = [];
  const changedReleaseVersionPaths = releaseVersionPaths.filter(path => changed.includes(path));

  if (!releaseMode && changedReleaseVersionPaths.length > 0) {
    failures.push(
      `Release versions are managed only by Prepare release; ordinary changes must not edit: ${changedReleaseVersionPaths.join(', ')}.`
    );
  }
  if (!releaseMode && changedManifestVersionUnits.length > 0) {
    failures.push(
      `Release unit versions are managed only by Prepare release; ordinary changes modified: ${changedManifestVersionUnits.join(', ')}.`
    );
  }

  if (!releaseMode || !distributableGuidanceChanged) return failures;

  if (!changed.includes(releaseVersionPaths[0])) failures.push('Distributable guidance changed without a guidance version bump.');
  if (!changed.includes(releaseVersionPaths[1])) failures.push('Distributable guidance changed without a plugin version bump.');
  if (previousGuidanceVersion !== undefined && previousGuidanceVersion === currentGuidanceVersion) {
    failures.push('Distributable guidance changed, but guidanceVersion was not incremented.');
  }
  if (previousPluginVersion !== undefined && previousPluginVersion === currentPluginVersion) {
    failures.push('Distributable guidance changed, but the plugin version was not incremented.');
  }

  return failures;
}
