import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

// The supported host is Linux. Exercise its compiler affinity failure in CI.
if (process.platform === 'linux') {
  const root = await mkdtemp(resolve(tmpdir(), 'newheap-companist-build-'));
  try {
    const status = await readFile('/proc/self/status', 'utf8');
    const ranges = status.match(/^Cpus_allowed_list:\s*(.+)$/m)[1].split(',').map(range => range.split('-').map(Number));
    let unavailableCpu = 0;
    while (ranges.some(([first, last = first]) => unavailableCpu >= first && unavailableCpu <= last)) {
      unavailableCpu++;
    }
    await writeFile(resolve(root, 'Probe.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ServerGarbageCollection>true</ServerGarbageCollection></PropertyGroup></Project>');
    await writeFile(resolve(root, 'Program.cs'), 'System.Console.WriteLine("Published application started.");');
    const script = resolve(dirname(fileURLToPath(import.meta.url)), 'publish-companist.sh');
    const build = spawnSync('sh', [script, 'Probe.csproj', '-c', 'Release', '-o', 'published', '-p:UseSharedCompilation=false'], {
      cwd: root, encoding: 'utf8', timeout: 120_000,
      env: { ...process.env, DOTNET_gcServer: '1', DOTNET_GCHeapAffinitizeRanges: String(unavailableCpu) }
    });
    assert.equal(build.status, 0, build.stdout + build.stderr);
    const runtime = JSON.parse(await readFile(resolve(root, 'published/Probe.runtimeconfig.json'), 'utf8'));
    assert.equal(runtime.runtimeOptions.configProperties['System.GC.Server'], true, 'Compilation must preserve application GC settings.');
    const started = spawnSync('dotnet', ['published/Probe.dll'], { cwd: root, encoding: 'utf8', timeout: 30_000 });
    assert.equal(started.status, 0, started.stdout + started.stderr);
    assert.match(started.stdout, /Published application started/);
    console.log('Companist publish passed with unavailable compiler affinity and preserved runtime GC.');
  } finally {
    assert.equal(dirname(root), resolve(tmpdir()));
    assert.ok(basename(root).startsWith('newheap-companist-build-'));
    await rm(root, { recursive: true, force: true });
  }
} else {
  console.log('Companist Linux compiler affinity check runs on Linux CI.');
}
