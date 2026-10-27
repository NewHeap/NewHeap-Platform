// The files `npm run test:affected` writes, kept where neither a concurrent run nor another local
// account can reach them: the generated solution of a run in a private folder of that run, and the
// durations of the last runs in the user's own cache folder (`durationsFilePath` in affected-lib.mjs).
import { randomBytes } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { validDurations } from './affected-lib.mjs';

/** The name of the generated solution inside a run folder. */
export const solutionFileName = 'affected-tests.slnx';

/**
 * Creates the folder of one run in the temp folder: a new folder with an unpredictable name that only
 * the current user may enter (mode 0700 where the file system has modes). Nothing else uses it, so a
 * concurrent run or another account cannot have put anything there.
 * @param {string} [root] the temp folder; the system's by default
 */
export function createRunFolder(root = tmpdir()) {
  return mkdtemp(join(root, 'newheap-test-affected-'));
}

/** Removes the folder of a run with everything in it. */
export function removeRunFolder(folder) {
  return rm(folder, { recursive: true, force: true });
}

/**
 * Writes the generated solution as a new file: the write fails when anything, a link included,
 * already exists at the path, instead of following or replacing it.
 */
export function writeSolution(path, content) {
  return writeFile(path, content, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
}

/** The durations kept by earlier runs, or none when the file is missing or unreadable. */
export async function readDurations(file) {
  try {
    return validDurations(JSON.parse(await readFile(file, 'utf8')));
  } catch {
    return {};
  }
}

/**
 * Keeps the durations: a new private file next to the durations file, moved over it in one step, so a
 * concurrent run reads either the old or the new durations and never a partly written file.
 */
export async function writeDurations(file, durations) {
  const folder = dirname(file);
  await mkdir(folder, { recursive: true, mode: 0o700 });
  const temporary = join(folder, `.durations-${process.pid}-${randomBytes(6).toString('hex')}.tmp`);
  try {
    await writeFile(temporary, `${JSON.stringify(durations, null, 2)}\n`, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    await rename(temporary, file);
  } finally {
    await rm(temporary, { force: true });
  }
}
