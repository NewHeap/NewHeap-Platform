import {environment} from "./environments/environment";

export { AppServerModule as default } from './app/app.module.server';

if (environment?.name === 'development') {
  console.warn('main.server.ts: SSR is running with SSL Certificate Checking disabled because environment === development is true.');
  process.env['NODE_TLS_REJECT_UNAUTHORIZED'] = '0';
}
