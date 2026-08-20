import { dirname, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
await import(pathToFileURL(resolve(scriptDirectory, 'install-consumer-skills.mjs')).href);
