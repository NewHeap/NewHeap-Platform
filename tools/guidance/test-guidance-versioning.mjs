import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { relative, resolve } from 'node:path';
import {
  consumerPluginSkillsRoot,
  consumerSkillBundleRoot,
  consumerSkillEvidenceCatalogName,
  consumerSkillRoots,
  readJson,
  repositoryRoot,
  walkFiles
} from './lib.mjs';

const guidance = await readJson(resolve(repositoryRoot, 'guidance', 'version.json'));
const releaseLinkFragment = `blob/newheap-platform-plugin-v${guidance.guidanceVersion}/docs/consumer-guide/`;
const roots = [consumerSkillBundleRoot, ...consumerSkillRoots.values(), consumerPluginSkillsRoot];
const taggedFiles = [];

for (const root of roots) {
  for (const path of await walkFiles(root)) {
    if ((await readFile(path, 'utf8')).includes(releaseLinkFragment)) {
      taggedFiles.push(relative(repositoryRoot, path).replaceAll('\\', '/'));
    }
  }
}

assert.deepEqual(taggedFiles.sort(), [
  `plugins/newheap-platform/skills/newheap-platform-development/references/${consumerSkillEvidenceCatalogName}`,
  `skills/newheap-platform-development/references/${consumerSkillEvidenceCatalogName}`
]);

console.log('Verified that release-pinned source links are centralized in two mirrored catalog files.');
