import { readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  isStableSemver,
  loadReleaseManifest,
  parseArguments,
  releaseNoteHeadingPackages,
  releaseUnit,
  repositoryRoot
} from './lib.mjs';

const usage = 'Usage: npm run release:breaking -- --from <version> [--to <version>] [--unit <id>] [--next] [--json] [--notes <directory>]';

/**
 * The first header cells of the tables whose rows an upgrade must act on: `Breaking change | Required action`,
 * and `Behavior change | Required action`, which older notes use for the same purpose.
 */
export const breakingChangeTableKinds = ['Breaking change', 'Behavior change'];

/** Compares two stable SemVer versions as numbers: negative, zero or positive. */
export function compareStableVersions(left, right) {
  const leftParts = left.split('.').map(Number);
  const rightParts = right.split('.').map(Number);
  for (let index = 0; index < 3; index += 1) {
    if (leftParts[index] !== rightParts[index]) {
      return leftParts[index] - rightParts[index];
    }
  }
  return 0;
}

function tableCells(line) {
  const cells = [];
  let cell = '';
  const content = line.trim().replace(/^\|/, '').replace(/\|$/, '');
  for (let index = 0; index < content.length; index += 1) {
    const character = content[index];
    if (character === '\\' && content[index + 1] === '|') {
      cell += '\\|';
      index += 1;
    } else if (character === '|') {
      cells.push(cell.trim());
      cell = '';
    } else {
      cell += character;
    }
  }
  cells.push(cell.trim());
  return cells;
}

/**
 * The rows of the `Breaking change | Required action` (or `Behavior change | Required action`) tables
 * in the text of one release-note section, as `{ kind, change, action }`, in their order.
 */
export function breakingChangeRows(sectionText) {
  const lines = sectionText.replaceAll('\r\n', '\n').split('\n');
  const rows = [];
  for (let index = 0; index < lines.length; index += 1) {
    const header = lines[index].trim().startsWith('|') ? tableCells(lines[index]) : [];
    const separator = lines[index + 1]?.trim() ?? '';
    if (header.length !== 2
      || !breakingChangeTableKinds.includes(header[0])
      || header[1] !== 'Required action'
      || !/^\|(?:\s*:?-{3,}:?\s*\|){2}$/.test(separator)) {
      continue;
    }

    index += 2;
    for (; index < lines.length && lines[index].trim().startsWith('|'); index += 1) {
      const [change = '', action = ''] = tableCells(lines[index]);
      rows.push({ kind: header[0], change, action });
    }
  }
  return rows;
}

/** The `## ` sections of one release-note file as `{ heading, text }`. */
function noteSections(source, title, path) {
  const [heading, ...sections] = source.replaceAll('\r\n', '\n').trim().split(/^## /m);
  if (heading.trim() !== title) {
    throw new Error(`${path}: expected ${title} followed by package sections.`);
  }
  return sections.map(section => ({ heading: section.split('\n', 1)[0].trim(), text: section }));
}

/**
 * Reads the archived release notes `v<version>.md` of `directory`, and `v-next.md` when `next` is set,
 * as `{ version, sections }`; `version` is `null` for `v-next.md`.
 */
export async function readReleaseNotes(directory, { next = false } = {}) {
  const notes = [];
  for (const name of (await readdir(directory)).sort()) {
    const match = name.match(/^v(\d+\.\d+\.\d+)\.md$/);
    if (!match && !(next && name === 'v-next.md')) {
      continue;
    }

    const path = resolve(directory, name);
    const version = match ? match[1] : null;
    const source = await readFile(path, 'utf8');
    notes.push({ version, sections: noteSections(source, version ? `# v${version}` : '# v-next', path) });
  }
  return notes;
}

/**
 * The breaking changes in `notes` (from `readReleaseNotes`) of the versions after `from` up to and
 * including `to`, with the pending notes of `v-next.md` last when they were read. A section belongs to
 * the release unit of the packages its heading names, as release preparation archives it; with
 * `unit` only that unit's sections count. Versions of different units are independent, so a selection
 * without `unit` that finds rows of several units is refused instead of mixing them.
 */
export function collectBreakingChanges(manifest, notes, { from, to, unit } = {}) {
  if (!isStableSemver(from ?? '')) throw new Error(`--from must be a stable version such as 8.0.0. ${usage}`);
  if (to !== undefined && !isStableSemver(to)) throw new Error(`--to must be a stable version such as 32.0.0. ${usage}`);
  if (to !== undefined && compareStableVersions(from, to) >= 0) throw new Error(`--to ${to} must be later than --from ${from}.`);
  if (unit !== undefined) releaseUnit(manifest, unit);

  const unitsByPackage = new Map();
  for (const [id, releaseUnitEntry] of Object.entries(manifest.units)) {
    const names = releaseUnitEntry.kind === 'nuget'
      ? releaseUnitEntry.projects.map(project => project.packageId)
      : [releaseUnitEntry.packageName];
    for (const name of names) {
      unitsByPackage.set(name, id);
    }
  }

  const selected = notes
    .filter(note => note.version === null
      || (compareStableVersions(note.version, from) > 0 && (to === undefined || compareStableVersions(note.version, to) <= 0)))
    .sort((left, right) => left.version === null ? 1 : right.version === null ? -1 : compareStableVersions(left.version, right.version));
  const changes = [];
  for (const note of selected) {
    for (const section of note.sections) {
      const packages = releaseNoteHeadingPackages(section.heading);
      const units = [...new Set(packages.map(name => unitsByPackage.get(name) ?? null))];
      const sectionUnit = units.length === 1 ? units[0] : null;
      if (unit !== undefined && sectionUnit !== unit) {
        continue;
      }

      for (const row of breakingChangeRows(section.text)) {
        changes.push({ version: note.version ?? 'next', unit: sectionUnit, section: section.heading, packages, ...row });
      }
    }
  }

  const units = [...new Set(changes.map(change => change.unit))];
  if (unit === undefined && units.length > 1) {
    throw new Error(
      `The versions after ${from}${to ? ` up to ${to}` : ''} hold breaking changes of several release units `
      + `(${units.map(id => id ?? 'unknown packages').join(', ')}), whose versions are independent. Choose one with --unit <id>.`);
  }

  return { unit: unit ?? units[0] ?? null, from, to: to ?? null, changes };
}

/** The breaking changes of `collectBreakingChanges` as Markdown, grouped by version and section. */
export function formatBreakingChanges(result) {
  const title = `# Breaking changes${result.unit ? ` of ${result.unit}` : ''} after ${result.from}${result.to ? ` up to ${result.to}` : ''}`;
  if (result.changes.length === 0) {
    return `${title}\n\nNo breaking changes.\n`;
  }

  const lines = [title];
  let version;
  let section;
  let kind;
  for (const change of result.changes) {
    if (change.version !== version) {
      version = change.version;
      section = undefined;
      kind = undefined;
      lines.push('', `## ${version === 'next' ? 'v-next (not released)' : `v${version}`}`);
    }
    if (change.section !== section || change.kind !== kind) {
      section = change.section;
      kind = change.kind;
      lines.push('', `### ${section}`, '', `| ${kind} | Required action |`, '|---|---|');
    }
    lines.push(`| ${change.change} | ${change.action} |`);
  }

  const versions = new Set(result.changes.map(change => change.version)).size;
  lines.push('', `${result.changes.length} breaking changes in ${versions} ${versions === 1 ? 'version' : 'versions'}.`);
  return `${lines.join('\n')}\n`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const options = parseArguments(process.argv.slice(2));
  if (options.help) {
    console.log(usage);
    process.exit(0);
  }

  try {
    const manifest = await loadReleaseManifest();
    const directory = options.notes
      ? resolve(process.env.INIT_CWD ?? process.cwd(), options.notes)
      : resolve(repositoryRoot, 'docs', 'release-notes');
    const notes = await readReleaseNotes(directory, { next: Boolean(options.next) });
    const result = collectBreakingChanges(manifest, notes, {
      from: typeof options.from === 'string' ? options.from : undefined,
      to: typeof options.to === 'string' ? options.to : undefined,
      unit: typeof options.unit === 'string' ? options.unit : undefined
    });
    process.stdout.write(options.json ? `${JSON.stringify(result, null, 2)}\n` : formatBreakingChanges(result));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
