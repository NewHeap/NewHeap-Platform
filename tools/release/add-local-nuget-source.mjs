import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { addLocalNugetSource, parseArguments } from './lib.mjs';

const options = parseArguments(process.argv.slice(2));
if (!options.config || !options.source) {
  throw new Error('Usage: node tools/release/add-local-nuget-source.mjs --config <path> --source <path> [--name <source-name>]');
}

const configPath = resolve(options.config);
const configuration = await readFile(configPath, 'utf8');
const updated = addLocalNugetSource(configuration, resolve(options.source), options.name);
await writeFile(configPath, updated, 'utf8');
