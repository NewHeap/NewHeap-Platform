import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const workspace = resolve(repositoryRoot, 'src/Front-end');
const dist = resolve(workspace, 'dist/nh-common');
const angularCli = resolve(workspace, 'node_modules/@angular/cli/bin/ng.js');
const npmCli = process.env.npm_execpath;

if (!npmCli) throw new Error('Run this compatibility check through npm.');

const matrix = [
  {
    angular: '20.3.28',
    tooling: '20.3.34',
    typescript: '5.9.3',
    translate: '17.0.0',
    zone: '0.15.1',
    cookie: '20.1.1',
    datatable: '23.0.1'
  },
  {
    angular: '21.2.21',
    tooling: '21.2.21',
    typescript: '5.9.3',
    translate: '18.0.0',
    zone: '0.16.0',
    cookie: '21.3.1',
    datatable: '24.0.1'
  },
  {
    angular: '22.2.3', tooling: '22.2.3', typescript: '6.0.2',
    translate: '18.0.0', zone: '0.16.0', cookie: '22.0.0', datatable: '25.1.0'
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
  const packed = JSON.parse(runNodeTool(
    npmCli,
    ['pack', '--json', '--pack-destination', tempRoot],
    dist,
    'pipe'
  ))[0];
  const packagePath = resolve(tempRoot, packed.filename);

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
      writeFile(resolve(fixture, 'src/index.html'), '<app-root></app-root>\n'),
      writeFile(resolve(fixture, 'src/main.ts'), `
import { Component, importProvidersFrom } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideTranslateService } from '@ngx-translate/core';
import { NhAuthService, NhCommonModule, NhCommonModuleConfig } from '@newheap/platform-common';
import { NhPasskeyLoginButtonComponent } from '@newheap/platform-common/two-factor';
import { NhToastrContainerComponent, provideNhToastr } from '@newheap/nh-toastr';

@Component({
  selector: 'app-root',
  imports: [NhCommonModule, NhPasskeyLoginButtonComponent, NhToastrContainerComponent],
  template: '<nh-loader /><nh-passkey-login-button /><nh-toastr-container />'
})
class AppComponent {}

bootstrapApplication(AppComponent, {
  providers: [
    provideTranslateService(),
    provideNhToastr({}),
    NhAuthService,
    importProvidersFrom(NhCommonModule.forRoot(new NhCommonModuleConfig({
      defaultLanguage: 'en', supportedLanguages: ['en'], defaultCulture: 'en-US'
    }), NhAuthService))
  ]
});
`)
    ]);

    runNodeTool(npmCli, [
      'install',
      '--no-audit',
      '--no-fund',
      packagePath,
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
      '@newheap/nh-toastr@0.0.7',
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
    console.log(`Verified @newheap/platform-common with Angular ${target.angular}.`);
  }
} finally {
  await rm(tempRoot, { recursive: true, force: true });
}
