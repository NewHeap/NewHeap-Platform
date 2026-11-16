import assert from 'node:assert/strict';
import { execFile, execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import { promisify } from 'node:util';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const workspace = resolve(repositoryRoot, 'src/Front-end');
const dist = resolve(workspace, 'dist/nh-common');
const angularCli = resolve(workspace, 'node_modules/@angular/cli/bin/ng.js');
const npmCli = process.env.npm_execpath;
const aiChat = process.argv.includes('--ai-chat');

if (!npmCli) {
  throw new Error('Run this compatibility check through npm.');
}

const matrix = [
  {
    angular: '20.3.28',
    cdk: '20.2.14',
    tooling: '20.3.34',
    typescript: '5.9.3',
    translate: '17.0.0',
    zone: '0.15.1',
    cookie: '20.1.1',
    datatable: '23.0.1'
  },
  {
    angular: '21.2.21',
    cdk: '21.2.14',
    tooling: '21.2.21',
    typescript: '5.9.3',
    translate: '18.0.0',
    zone: '0.16.0',
    cookie: '21.3.1',
    datatable: '24.0.1'
  },
  {
    angular: '22.2.6',
    cdk: '22.2.6',
    tooling: '22.2.6',
    typescript: '6.0.2',
    translate: '18.0.0',
    zone: '0.16.0',
    cookie: '22.0.0',
    datatable: '25.1.0'
  }
];

const runNodeTool = (tool, args, cwd, stdio = 'inherit') => execFileSync(
  process.execPath,
  [tool, ...args],
  { cwd, encoding: stdio === 'pipe' ? 'utf8' : undefined, stdio }
);

const tempRoot = await mkdtemp(join(tmpdir(), 'nh-common-angular-'));

try {
  runNodeTool(angularCli, ['build', 'nh-toastr', '--configuration=production'], workspace);
  runNodeTool(angularCli, ['build', 'nh-common', '--configuration=production'], workspace);
  // Install both release artifacts together, so a toast release cannot be hidden by a pinned older peer.
  const packedToastr = JSON.parse(runNodeTool(
    npmCli,
    ['pack', '--json', '--pack-destination', tempRoot],
    resolve(workspace, 'dist/nh-toastr'),
    'pipe'
  ))[0];
  const toastrPackagePath = resolve(tempRoot, packedToastr.filename);
  const packed = JSON.parse(runNodeTool(
    npmCli,
    ['pack', '--json', '--pack-destination', tempRoot],
    dist,
    'pipe'
  ))[0];
  const packagePath = resolve(tempRoot, packed.filename);
  let aiChatPackagePath;
  if (aiChat) {
    runNodeTool(angularCli, ['build', 'nh-ai-chat', '--configuration=production'], workspace);
    const packedChat = JSON.parse(runNodeTool(
      npmCli, ['pack', '--json', '--pack-destination', tempRoot],
      resolve(workspace, 'dist/nh-ai-chat'), 'pipe'
    ))[0];
    aiChatPackagePath = resolve(tempRoot, packedChat.filename);
  }

  for (const target of matrix) {
    const fixture = resolve(tempRoot, `angular-${target.angular}`);
    await mkdir(resolve(fixture, 'src'), { recursive: true });
    await Promise.all([
      writeFile(resolve(fixture, 'package.json'), JSON.stringify({
        name: `nh-common-angular-${target.angular}`,
        private: true,
        type: 'module'
      }, null, 2)),
      writeFile(resolve(fixture, 'angular.json'), JSON.stringify({
        version: 1,
        projects: {
          compatibility: {
            projectType: 'application',
            root: '',
            sourceRoot: 'src',
            architect: {
              build: {
                builder: '@angular/build:application',
                options: {
                  outputPath: 'dist',
                  index: 'src/index.html',
                  browser: 'src/main.ts',
                  tsConfig: 'tsconfig.json'
                }
              }
            }
          }
        },
        cli: { analytics: false }
      }, null, 2)),
      writeFile(resolve(fixture, 'tsconfig.json'), JSON.stringify({
        compilerOptions: {
          target: 'ES2022',
          module: 'ES2022',
          moduleResolution: 'bundler',
          strict: true,
          experimentalDecorators: true,
          skipLibCheck: false,
          types: [],
          lib: ['ES2022', 'dom']
        },
        angularCompilerOptions: {
          strictInjectionParameters: true,
          strictTemplates: true
        },
        files: ['src/main.ts']
      }, null, 2)),
      writeFile(resolve(fixture, 'src/index.html'), '<!doctype html><html lang="en"><head><base href="/"></head><body><app-root></app-root></body></html>\n'),
      writeFile(resolve(fixture, 'src/main.ts'), `
import { Component, Injectable, importProvidersFrom, signal, provideZonelessChangeDetection } from '@angular/core';
${aiChat ? "import { AssistantCompatibilityComponent, assistantCompatibilityProviders, verifyAssistantCompatibility } from './assistant';" : ''}
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { of } from 'rxjs';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideTranslateService } from '@ngx-translate/core';
import { NhAuthService, NhCommonModule, NhCommonModuleConfig } from '@newheap/platform-common';
import { NhComboboxComponent, NhMultiselectComponent, NhSelectOption, provideNhSelect } from '@newheap/platform-common/select';
import { NhPasskeyLoginButtonComponent } from '@newheap/platform-common/two-factor';
import { NhToastrContainerComponent, provideNhToastr } from '@newheap/nh-toastr';

@Injectable()
class CompatibilityAuthService extends NhAuthService {}

@Component({
  selector: 'app-root',
  imports: [${aiChat ? 'AssistantCompatibilityComponent, ' : ''}ReactiveFormsModule, NhCommonModule, NhPasskeyLoginButtonComponent, NhToastrContainerComponent, NhComboboxComponent, NhMultiselectComponent],
  template: \`
    ${aiChat ? '<sample-assistant-compatibility />' : ''}
    <nh-loader /><nh-passkey-login-button /><nh-toastr-container />
    <label for="status">Status</label>
    <nh-combobox inputId="status" [formControl]="status" [options]="statuses" noneLabel="None" />
    <label for="tags">Tags</label>
    <nh-multiselect inputId="tags" [formControl]="tags" [search]="search" [create]="create" />
    <nh-combobox ariaLabel="Owner" size="compact" [value]="owner()" [options]="statuses" (valueChange)="owner.set($event)" />
  \`
})
class AppComponent {
  readonly statuses: NhSelectOption<number>[] = [{ value: 0, label: 'Draft' }, { value: 1, label: 'Active' }];
  readonly status = new FormControl<number | null>(0);
  readonly tags = new FormControl<string[]>([], { nonNullable: true });
  readonly owner = signal<number | null>(null);
  readonly search = (query: string) => of([{ value: query, label: query }]);
  readonly create = (text: string) => Promise.resolve({ value: text, label: text });
}

bootstrapApplication(AppComponent, {
  providers: [
    provideZonelessChangeDetection(),
    provideTranslateService({ lang: 'en', fallbackLang: 'en' }),
    provideNhSelect({}),
    provideNhToastr({}),
    CompatibilityAuthService,
    importProvidersFrom(NhCommonModule.forRoot(new NhCommonModuleConfig({
      defaultLanguage: 'en', supportedLanguages: ['en'], defaultCulture: 'en-US'
    }), CompatibilityAuthService)),
    ${aiChat ? '...assistantCompatibilityProviders' : ''}
  ]
})${aiChat ? '.then(verifyAssistantCompatibility).catch(error => { document.body.dataset.compatibility = String(error); })' : ''};
`)
    ]);

    if (aiChat) {
      const sample = resolve(repositoryRoot,
        'examples/SampleProjectManagement/src/Front-end/compatibility/assistant.ts');
      await writeFile(resolve(fixture, 'src/assistant.ts'), await readFile(sample));
    }

    runNodeTool(npmCli, [
      'install',
      '--no-audit',
      '--no-fund',
      packagePath,
      ...(aiChat ? [aiChatPackagePath, `@angular/cdk@${target.cdk}`] : []),
      `@angular/build@${target.tooling}`,
      `@angular/cli@${target.tooling}`,
      `@angular/common@${target.angular}`,
      `@angular/compiler@${target.angular}`,
      `@angular/compiler-cli@${target.angular}`,
      `@angular/core@${target.angular}`,
      `@angular/forms@${target.angular}`,
      `@angular/platform-browser@${target.angular}`,
      `@angular/router@${target.angular}`,
      `@ngx-translate/core@${target.translate}`,
      `@swimlane/ngx-datatable@${target.datatable}`,
      `ngx-cookie-service@${target.cookie}`,
      toastrPackagePath,
      '--@newheap:registry=https://registry.npmjs.org/',
      'ngx-bootstrap-multiselect@4.0.0',
      '@microsoft/signalr@10.0.11',
      '@msgpack/msgpack@2.8.0',
      'js-base64@3.7.8',
      'luxon@3.7.2',
      '@types/luxon@3.7.1',
      'rxjs@7.8.2',
      'tslib@2.8.1',
      `typescript@${target.typescript}`,
      `zone.js@${target.zone}`
    ], fixture);
    runNodeTool(
      resolve(fixture, 'node_modules/@angular/cli/bin/ng.js'),
      ['build', 'compatibility'],
      fixture
    );
    if (aiChat) {
      await verifyBrowser(fixture);
    }
    console.log(`Verified ${aiChat ? '@newheap/platform-ai-chat and ' : ''}@newheap/platform-common with Angular ${target.angular} / TypeScript ${target.typescript}.`);
  }
} finally {
  if (process.env.NH_KEEP_COMPATIBILITY_FIXTURE === '1') {
    console.log(`Kept compatibility fixtures at ${tempRoot}.`);
  } else {
    await rm(tempRoot, { recursive: true, force: true });
  }
}

async function verifyBrowser(fixture) {
  const require = createRequire(resolve(workspace, 'package.json'));
  const launcher = require('karma-chrome-launcher')['launcher:ChromeHeadless'][1];
  const chrome = process.env.CHROME_BIN || launcher.prototype.DEFAULT_CMD[process.platform];
  assert.ok(chrome, 'Install Chrome or set CHROME_BIN for the runtime compatibility check.');
  const browserRoot = resolve(fixture, 'dist/browser');
  const server = createServer(async (request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    const file = resolve(browserRoot, `.${path === '/' ? '/index.html' : path}`);
    if (!file.startsWith(`${browserRoot}/`) && !file.startsWith(`${browserRoot}\\`)) {
      response.writeHead(403).end();
      return;
    }
    try {
      const content = await readFile(file);
      response.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html');
      response.end(content);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const { stdout } = await promisify(execFile)(chrome, [
      '--headless', '--disable-gpu', '--no-sandbox', '--dump-dom', '--virtual-time-budget=30000',
      `--user-data-dir=${resolve(fixture, 'chrome-profile')}`,
      `http://127.0.0.1:${server.address().port}/`
    ], { timeout: 60000, maxBuffer: 10 * 1024 * 1024, windowsHide: true });
    assert.match(stdout, /data-compatibility="passed"/, `Assistant runtime check failed:\n${stdout}`);
    console.log('Verified assistant launcher, panel, admin, fetch and live service in Chrome.');
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
}
