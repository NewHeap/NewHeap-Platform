import { spawnSync } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const validator = resolve(fileURLToPath(new URL('validate-public-source.mjs', import.meta.url)));

async function createRepository(files) {
  const directory = await mkdtemp(join(tmpdir(), 'newheap-public-source-'));
  const init = spawnSync('git', ['init', '--quiet'], { cwd: directory, encoding: 'utf8' });
  if (init.status !== 0) throw new Error(init.stderr || 'Unable to initialize test repository.');

  for (const [name, content] of Object.entries(files)) {
    await writeFile(join(directory, name), content, 'utf8');
  }

  const add = spawnSync('git', ['add', '.'], { cwd: directory, encoding: 'utf8' });
  if (add.status !== 0) throw new Error(add.stderr || 'Unable to stage test fixtures.');
  return directory;
}

function validate(directory) {
  return spawnSync(process.execPath, [validator, '--root', directory], {
    cwd: directory,
    encoding: 'utf8'
  });
}

const temporaryDirectories = [];

try {
  const safeRepository = await createRepository({
    'safe.cs': 'var connection = "Server=localhost;Database=sample;Integrated Security=true";\n',
    'sample.ts': 'const password = "Sample123!";\n'
  });
  temporaryDirectories.push(safeRepository);
  const safe = validate(safeRepository);
  if (safe.status !== 0) {
    throw new Error(`Safe public-source fixture failed.\n${safe.stdout}\n${safe.stderr}`);
  }

  const repositoryWithTrackedDeletion = await createRepository({
    'removed.txt': 'safe content\n'
  });
  temporaryDirectories.push(repositoryWithTrackedDeletion);
  await rm(join(repositoryWithTrackedDeletion, 'removed.txt'));
  const trackedDeletion = validate(repositoryWithTrackedDeletion);
  if (trackedDeletion.status !== 0) {
    throw new Error(`Tracked deletion fixture failed.\n${trackedDeletion.stdout}\n${trackedDeletion.stderr}`);
  }

  const redactedValue = 'do-not-print-this-value';
  const unsafeRepository = await createRepository({
    'database.cs': `var connection = "Server=db.internal.test;Database=sample;Password=${redactedValue}";\n`,
    'private.key': '-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----\n',
    'token.txt': `github_pat_${'x'.repeat(24)}\n`
  });
  temporaryDirectories.push(unsafeRepository);
  const unsafe = validate(unsafeRepository);
  if (unsafe.status === 0
    || !unsafe.stderr.includes('database.cs:1 [external-database-endpoint]')
    || !unsafe.stderr.includes('private.key:1 [private-key]')
    || !unsafe.stderr.includes('token.txt:1 [provider-token]')
    || unsafe.stderr.includes(redactedValue)) {
    throw new Error(`Unsafe public-source fixture was not safely redacted.\n${unsafe.stdout}\n${unsafe.stderr}`);
  }

  console.log('Verified safe fixtures, tracked deletions, secret detection and redacted diagnostics.');
} finally {
  await Promise.all(temporaryDirectories.map(directory => rm(directory, { recursive: true, force: true })));
}
