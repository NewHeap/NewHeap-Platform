import { resolve, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
await import(pathToFileURL(resolve(repositoryRoot, 'plugins', 'newheap-platform', 'scripts', 'install-consumer-skills.mjs')).href);
