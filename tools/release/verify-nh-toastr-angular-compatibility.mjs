import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const workspace = resolve(repositoryRoot, 'src/Front-end');
const dist = resolve(workspace, 'dist/nh-toastr');
const angularCli = resolve(workspace, 'node_modules/@angular/cli/bin/ng.js');
const npmCli = process.env.npm_execpath;

if (!npmCli) throw new Error('Run this compatibility check through npm.');

const matrix = [
  {
    angular: '20.3.28',
    tooling: '20.3.34',
    typescript: '5.9.3',
    translate: '17.0.0',
    zone: '0.15.1'
  },
  {
    angular: '21.2.21',
    tooling: '21.2.21',
    typescript: '5.9.3',
    translate: '17.0.0',
    zone: '0.16.0'
  },
  {
    angular: '22.1.3',
    tooling: '22.1.5',
    typescript: '6.0.2',
    translate: '18.0.0',
    zone: '0.16.0'
  }
];

const runNodeTool = (tool, args, cwd, stdio = 'inherit') => execFileSync(
  process.execPath,
  [tool, ...args],
  { cwd, encoding: stdio === 'pipe' ? 'utf8' : undefined, stdio }
);

const tempRoot = await mkdtemp(join(tmpdir(), 'nh-toastr-angular-'));

try {
  runNodeTool(angularCli, ['build', 'nh-toastr', '--configuration=production'], workspace);
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
        name: `nh-toastr-angular-${target.angular}`,
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
import { Component, inject } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideTranslateService } from '@ngx-translate/core';
import {
  NhToastrContainerComponent,
  NhToastrService,
  provideNhToastr
} from '@newheap/nh-toastr';

@Component({
  selector: 'app-root',
  imports: [NhToastrContainerComponent],
  template: '<button type="button" (click)="show()">Show</button><nh-toastr-container />'
})
class AppComponent {
  private readonly toastr = inject(NhToastrService);
  show(): void { this.toastr.success('Compatible'); }
}

bootstrapApplication(AppComponent, {
  providers: [provideTranslateService(), provideNhToastr({})]
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
      `@angular/platform-browser@${target.angular}`,
      `@ngx-translate/core@${target.translate}`,
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
    console.log(`Verified @newheap/nh-toastr with Angular ${target.angular}.`);
  }
} finally {
  await rm(tempRoot, { recursive: true, force: true });
}
