// Reports what an application ships up front, per package, from the esbuild metafile that
// `ng build <app> --stats-json` writes next to the build output.
//
//   node scripts/report-initial-bundle.mjs dist/management/stats.json [--top 15]
//
// A chunk counts as initial when the main bundle or the polyfills reach it through static imports;
// everything only reached through a dynamic import is lazy. Sizes are minified bytes before
// compression, as esbuild reports them.
import { readFileSync } from "node:fs";

const argumentsList = process.argv.slice(2);
const statsPath = argumentsList.find(argument => !argument.startsWith("--"));
const topIndex = argumentsList.indexOf("--top");
const top = topIndex >= 0 ? Number(argumentsList[topIndex + 1]) : 15;

if (!statsPath) {
  console.error("Usage: node scripts/report-initial-bundle.mjs <stats.json> [--top <count>]");
  process.exit(1);
}

const outputs = JSON.parse(readFileSync(statsPath, "utf8")).outputs;

function packageOf(inputPath) {
  const normalized = inputPath.split("\\").join("/");
  const match = normalized.match(/node_modules\/((?:@[^/]+\/)?[^/]+)\/(?:fesm2022\/([^/]+)\.mjs)?/);

  if (!match) {
    return "application source";
  }

  // A library's secondary entry points are separate files, such as
  // fesm2022/newheap-platform-common-select.mjs, and they are reported as package/entry.
  const packageName = match[1];
  const flatName = packageName.replace("@", "").replace("/", "-");

  if (!match[2]?.startsWith(`${flatName}-`)) {
    return packageName;
  }

  const rest = match[2].slice(flatName.length + 1);
  // A library that loads part of itself on first use, such as the cards of
  // @newheap/platform-ai-chat, has chunk files with rollup's eight-character hash:
  // fesm2022/newheap-platform-ai-chat-nh-assistant-plan-view.component-BeGCstiW.mjs. The chunk
  // named after the entry holds the entry's own code; the others report as package (chunk).
  const chunk = rest.match(/^(.+)-[\w-]{8}$/)?.[1];
  if (chunk === flatName) {
    return packageName;
  }
  if (chunk) {
    return `${packageName} (chunk ${chunk})`;
  }

  return `${packageName}/${rest}`;
}

function initialChunks() {
  const entryChunks = Object.keys(outputs)
    .filter(path => /(^|\/)(main|polyfills)-[^/]*\.js$/.test(path));
  const reached = new Set();
  const pending = [...entryChunks];

  while (pending.length > 0) {
    const chunk = pending.pop();

    if (reached.has(chunk)) {
      continue;
    }

    reached.add(chunk);

    for (const imported of outputs[chunk].imports ?? []) {
      if (imported.kind === "import-statement" && outputs[imported.path]) {
        pending.push(imported.path);
      }
    }
  }

  return reached;
}

const initial = initialChunks();
const totals = { initial: new Map(), lazy: new Map() };

for (const [chunk, output] of Object.entries(outputs)) {
  if (!chunk.endsWith(".js")) {
    continue;
  }

  const group = initial.has(chunk) ? totals.initial : totals.lazy;

  for (const [inputPath, input] of Object.entries(output.inputs ?? {})) {
    const name = packageOf(inputPath);
    group.set(name, (group.get(name) ?? 0) + input.bytesInOutput);
  }
}

const kib = bytes => (bytes / 1024).toFixed(1).padStart(8);

for (const [label, group] of Object.entries(totals)) {
  const sum = [...group.values()].reduce((total, bytes) => total + bytes, 0);
  console.log(`${label} JavaScript: ${kib(sum).trim()} KiB`);

  [...group.entries()]
    .sort((left, right) => right[1] - left[1])
    .slice(0, top)
    .forEach(([name, bytes]) => console.log(`${kib(bytes)} KiB  ${name}`));

  console.log("");
}
