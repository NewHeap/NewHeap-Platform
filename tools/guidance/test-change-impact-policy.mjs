import assert from 'node:assert/strict';
import { validateLibraryChangeEvidence, validateReleaseVersionPolicy } from './change-impact-policy.mjs';

const guidancePath = 'guidance/rules/backend-modules/nh-backend-controller-contracts.md';
const guidanceVersionPath = 'guidance/version.json';
const pluginVersionPath = 'plugins/newheap-platform/.codex-plugin/plugin.json';

const behavioralFix = [
  'src/Back-end/Libraries/NewHeap.Platform.AI.Chat/Persistence/NhAssistantRunStore.cs',
  'examples/SampleProjectManagement/src/Back-end/Tests/SampleProjectManagement.Core.Tests/AssistantApprovalPolicySelectionSamplesTests.cs',
  'guidance/rules/ai/nh-ai-agent-runs.md',
  'examples/SampleProjectManagement/docs/cases/sample-case-registry.json'
];
assert.deepEqual(validateLibraryChangeEvidence(behavioralFix, 'origin/main'), []);
for (const missing of behavioralFix.slice(2)) {
  const failures = validateLibraryChangeEvidence(behavioralFix.filter(path => path !== missing), 'origin/main');
  assert.equal(failures.length, 1);
}
assert.equal(validateLibraryChangeEvidence([behavioralFix[0]], 'origin/main').length, 3);
assert.match(validateLibraryChangeEvidence(
  behavioralFix.filter(path => !path.startsWith('examples/')), 'origin/main'
).join('\n'), /executable SampleProjectManagement evidence/);

assert.deepEqual(validateReleaseVersionPolicy({
  changed: [guidancePath],
  releaseMode: false,
  distributableGuidanceChanged: true
}), []);

assert.match(validateReleaseVersionPolicy({
  changed: [guidancePath, guidanceVersionPath, pluginVersionPath],
  releaseMode: false,
  distributableGuidanceChanged: true
}).join('\n'), /managed only by Prepare release/);

assert.match(validateReleaseVersionPolicy({
  changed: ['release/manifest.json'],
  releaseMode: false,
  distributableGuidanceChanged: false,
  changedManifestVersionUnits: ['nuget-common', 'npm-platform-common']
}).join('\n'), /ordinary changes modified: nuget-common, npm-platform-common/);

assert.match(validateReleaseVersionPolicy({
  changed: [guidancePath],
  releaseMode: true,
  distributableGuidanceChanged: true
}).join('\n'), /guidance version bump[\s\S]*plugin version bump/);

assert.match(validateReleaseVersionPolicy({
  changed: [guidancePath, guidanceVersionPath, pluginVersionPath],
  releaseMode: true,
  distributableGuidanceChanged: true,
  previousGuidanceVersion: '1.2.3',
  currentGuidanceVersion: '1.2.3',
  previousPluginVersion: '1.2.3',
  currentPluginVersion: '1.2.3'
}).join('\n'), /guidanceVersion was not incremented[\s\S]*plugin version was not incremented/);

assert.deepEqual(validateReleaseVersionPolicy({
  changed: [guidancePath, guidanceVersionPath, pluginVersionPath],
  releaseMode: true,
  distributableGuidanceChanged: true,
  previousGuidanceVersion: '1.2.3',
  currentGuidanceVersion: '1.3.0',
  previousPluginVersion: '1.2.3',
  currentPluginVersion: '1.3.0'
}), []);

assert.deepEqual(validateReleaseVersionPolicy({
  changed: ['release/manifest.json'],
  releaseMode: true,
  distributableGuidanceChanged: false,
  changedManifestVersionUnits: ['nuget-common']
}), []);

console.log('Verified feature and release version-change policies.');
