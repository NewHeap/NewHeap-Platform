import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import {
  breakingChangeRows,
  collectBreakingChanges,
  compareStableVersions,
  formatBreakingChanges,
  readReleaseNotes
} from './breaking-changes.mjs';
import { loadReleaseManifest, repositoryRoot } from './lib.mjs';

const manifest = await loadReleaseManifest();
const tool = resolve(repositoryRoot, 'tools', 'release', 'breaking-changes.mjs');

function table(kind, ...rows) {
  return [`| ${kind} | Required action |`, '|---|---|', ...rows.map(([change, action]) => `| ${change} | ${action} |`)].join('\n');
}

function run(...argumentsList) {
  return spawnSync(process.execPath, [tool, ...argumentsList], { cwd: repositoryRoot, encoding: 'utf8' });
}

// Versions compare as numbers, not as text.
assert.ok(compareStableVersions('10.0.0', '9.9.9') > 0);
assert.ok(compareStableVersions('0.9.0', '0.10.0') < 0);
assert.equal(compareStableVersions('32.0.0', '32.0.0'), 0);

// Only the tables an upgrade must act on count; an escaped pipe stays inside its cell.
assert.deepEqual(breakingChangeRows([
  'Some prose.',
  '',
  table('Breaking change', ['`A` was removed.', 'Use `B`.'], ['`a \\| b` changed.', 'Write `a`.']),
  '',
  table('Adoption note', ['New option.', 'No action.']),
  '',
  '| Per host | Before | After |',
  '|---|---|---|',
  '| Median | 2 ms | 1 ms |',
  '',
  table('Behavior change', ['Logout sends the token.', 'Deploy when convenient.'])
].join('\r\n')), [
  { kind: 'Breaking change', change: '`A` was removed.', action: 'Use `B`.' },
  { kind: 'Breaking change', change: '`a \\| b` changed.', action: 'Write `a`.' },
  { kind: 'Behavior change', change: 'Logout sends the token.', action: 'Deploy when convenient.' }
]);

const notesDirectory = await mkdtemp(resolve(tmpdir(), 'newheap-breaking-changes-'));
try {
  // Two units share version numbers, as nuget-common and npm-platform-common do in the real notes.
  const files = {
    'v1.0.0.md': ['# v1.0.0',
      `## NewHeap.Platform.Common: first\n\n${table('Breaking change', ['One changed.', 'Do one.'])}`,
      `## @newheap/platform-common\n\n${table('Breaking change', ['Front end changed.', 'Do front end.'])}`],
    'v2.0.0.md': ['# v2.0.0',
      `## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: second\n\n${table('Behavior change', ['Two changed.', 'Do two.'])}`,
      '## NewHeap.Platform.AspNet.Common: prose only\n\n- Nothing to do.',
      `## @newheap/platform-common\n\n${table('Breaking change', ['Front end two.', 'Do front end two.'])}`],
    'v10.0.0.md': ['# v10.0.0',
      `## NewHeap.Platform.AI.Chat\n\n${table('Breaking change', ['Ten changed.', 'Do ten.'], ['Ten again.', 'Do ten again.'])}`],
    'v-next.md': ['# v-next',
      `## NewHeap.Platform.AI.Chat: pending\n\n${table('Breaking change', ['Pending changed.', 'Do pending.'])}`],
    'README.md': ['Not a release note.']
  };
  for (const [name, parts] of Object.entries(files)) {
    await writeFile(resolve(notesDirectory, name), `${parts.join('\n\n')}\n`);
  }

  const archived = await readReleaseNotes(notesDirectory);
  assert.deepEqual(archived.map(note => note.version).sort(compareStableVersions), ['1.0.0', '2.0.0', '10.0.0']);
  const withNext = await readReleaseNotes(notesDirectory, { next: true });
  assert.equal(withNext.filter(note => note.version === null).length, 1);

  // The range excludes --from and includes --to, ordered by version as numbers, per unit.
  const common = collectBreakingChanges(manifest, archived, { unit: 'nuget-common', from: '1.0.0', to: '10.0.0' });
  assert.equal(common.unit, 'nuget-common');
  assert.deepEqual(common.changes.map(change => [change.version, change.change]), [
    ['2.0.0', 'Two changed.'],
    ['10.0.0', 'Ten changed.'],
    ['10.0.0', 'Ten again.']
  ]);
  assert.deepEqual(common.changes[0].packages, ['NewHeap.Platform.Common', 'NewHeap.Platform.AspNet.Common']);
  const frontEnd = collectBreakingChanges(manifest, archived, { unit: 'npm-platform-common', from: '0.0.0' });
  assert.deepEqual(frontEnd.changes.map(change => change.change), ['Front end changed.', 'Front end two.']);

  // Without --unit a selection of one unit needs no choice; one of several units is refused.
  assert.equal(collectBreakingChanges(manifest, archived, { from: '2.0.0' }).unit, 'nuget-common');
  assert.throws(() => collectBreakingChanges(manifest, archived, { from: '0.0.0', to: '2.0.0' }),
    /several release units \(nuget-common, npm-platform-common\).*--unit/);
  assert.throws(() => collectBreakingChanges(manifest, archived, { from: 'latest' }), /--from must be a stable version/);
  assert.throws(() => collectBreakingChanges(manifest, archived, { from: '2.0.0', to: '2.0.0' }), /must be later than/);
  assert.throws(() => collectBreakingChanges(manifest, archived, { from: '1.0.0', unit: 'nuget-everything' }), /Unknown release component/);

  // The pending notes come last, after every released version.
  const pending = collectBreakingChanges(manifest, withNext, { unit: 'nuget-common', from: '2.0.0' });
  assert.deepEqual(pending.changes.map(change => change.version), ['10.0.0', '10.0.0', 'next']);

  assert.equal(formatBreakingChanges(collectBreakingChanges(manifest, archived, { unit: 'nuget-common', from: '0.0.0', to: '2.0.0' })), [
    '# Breaking changes of nuget-common after 0.0.0 up to 2.0.0',
    '',
    '## v1.0.0',
    '',
    '### NewHeap.Platform.Common: first',
    '',
    '| Breaking change | Required action |',
    '|---|---|',
    '| One changed. | Do one. |',
    '',
    '## v2.0.0',
    '',
    '### NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: second',
    '',
    '| Behavior change | Required action |',
    '|---|---|',
    '| Two changed. | Do two. |',
    '',
    '2 breaking changes in 2 versions.',
    ''
  ].join('\n'));
  assert.equal(formatBreakingChanges(collectBreakingChanges(manifest, archived, { unit: 'nuget-proxy', from: '0.0.0' })),
    '# Breaking changes of nuget-proxy after 0.0.0\n\nNo breaking changes.\n');

  // The command reads a notes directory, prints Markdown or JSON and fails with a message, not a stack trace.
  const markdown = run('--notes', notesDirectory, '--unit', 'nuget-common', '--from', '2.0.0', '--next');
  assert.equal(markdown.status, 0, markdown.stderr);
  assert.match(markdown.stdout, /^# Breaking changes of nuget-common after 2\.0\.0\n/);
  assert.match(markdown.stdout, /## v-next \(not released\)\n\n### NewHeap\.Platform\.AI\.Chat: pending/);
  const json = run('--notes', notesDirectory, '--unit', 'nuget-common', '--from', '1.0.0', '--to', '2.0.0', '--json');
  assert.equal(json.status, 0, json.stderr);
  assert.deepEqual(JSON.parse(json.stdout), {
    unit: 'nuget-common',
    from: '1.0.0',
    to: '2.0.0',
    changes: [{
      version: '2.0.0',
      unit: 'nuget-common',
      section: 'NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: second',
      packages: ['NewHeap.Platform.Common', 'NewHeap.Platform.AspNet.Common'],
      kind: 'Behavior change',
      change: 'Two changed.',
      action: 'Do two.'
    }]
  });
  const mixed = run('--notes', notesDirectory, '--from', '0.0.0');
  assert.equal(mixed.status, 1);
  assert.match(mixed.stderr, /several release units/);
  assert.doesNotMatch(mixed.stderr, /at collectBreakingChanges/);
} finally {
  await rm(notesDirectory, { recursive: true, force: true });
}

// Every archived note of the repository parses, and each of its rows names a change and an action.
const repositoryNotes = await readReleaseNotes(resolve(repositoryRoot, 'docs', 'release-notes'), { next: true });
let repositoryRows = 0;
for (const id of Object.keys(manifest.units)) {
  for (const change of collectBreakingChanges(manifest, repositoryNotes, { unit: id, from: '0.0.0' }).changes) {
    assert.ok(change.change && change.action, `v${change.version} '${change.section}' has a row without a change or an action.`);
    repositoryRows += 1;
  }
}
assert.ok(repositoryRows > 0, 'The repository notes hold breaking changes.');

console.log(`Exercised the breaking-change overview on fixtures and on ${repositoryRows} rows of the repository's release notes.`);
