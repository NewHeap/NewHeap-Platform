import { execFileSync } from "node:child_process";
import { open, readdir, stat, unlink } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const sampleRoot = resolve(scriptDirectory, "..");
const libraryRoot = resolve(scriptDirectory, "../../../../../src/Front-end");
const angularCli = resolve(libraryRoot, "node_modules/@angular/cli/bin/ng.js");
const lockFile = resolve(sampleRoot, ".nh-common-sync.lock");
const npmCli = process.env.npm_execpath;
const libraries = ["nh-common", "nh-toastr"];

if (!npmCli) {
  throw new Error("npm_execpath is required to synchronize the local NH package.");
}

const runNpm = (argumentsList, options = {}) =>
  execFileSync(process.execPath, [npmCli, ...argumentsList], {
    stdio: "inherit",
    ...options
  });

const runAngularCli = argumentsList =>
  execFileSync(process.execPath, [angularCli, ...argumentsList], {
    cwd: libraryRoot,
    stdio: "inherit"
  });

const delay = milliseconds => new Promise(resolveDelay => setTimeout(resolveDelay, milliseconds));

async function removePackedPackages(libraryPackageDirectory) {
  const entries = await readdir(libraryPackageDirectory, { withFileTypes: true }).catch(error => {
    if (error.code === "ENOENT") return [];
    throw error;
  });
  await Promise.all(entries
    .filter(entry => entry.isFile() && entry.name.endsWith(".tgz"))
    .map(entry => unlink(resolve(libraryPackageDirectory, entry.name))));
}

async function acquireLock() {
  const deadline = Date.now() + 120_000;

  while (Date.now() < deadline) {
    try {
      return await open(lockFile, "wx");
    } catch (error) {
      if (error.code !== "EEXIST") {
        throw error;
      }

      const lockStats = await stat(lockFile).catch(() => undefined);
      if (lockStats && Date.now() - lockStats.mtimeMs > 300_000) {
        await unlink(lockFile).catch(() => undefined);
        continue;
      }

      await delay(250);
    }
  }

  throw new Error("Timed out waiting for the NH common package synchronization lock.");
}

const lock = await acquireLock();
const packedPackagePaths = [];
let failure;

try {
  for (const library of libraries) {
    const libraryPackageDirectory = resolve(libraryRoot, `dist/${library}`);
    runAngularCli(["build", library, "--configuration=development"]);
    await removePackedPackages(libraryPackageDirectory);

    const packedPackage = JSON.parse(
      execFileSync(process.execPath, [npmCli, "pack", "--json"], {
        cwd: libraryPackageDirectory,
        encoding: "utf8"
      })
    )[0];
    const packedPackagePath = resolve(libraryPackageDirectory, packedPackage.filename);
    packedPackagePaths.push(packedPackagePath);
  }

  runNpm([
    "install",
    "--no-save",
    ...packedPackagePaths
  ], { cwd: sampleRoot });
} catch (error) {
  failure = error;
} finally {
  for (const packedPackagePath of packedPackagePaths) {
    try { await unlink(packedPackagePath); } catch (error) { if (error.code !== "ENOENT") failure ??= error; }
  }
  try { await lock.close(); } catch (error) { failure ??= error; }
  try { await unlink(lockFile); } catch (error) { if (error.code !== "ENOENT") failure ??= error; }
}

if (failure) throw failure;
