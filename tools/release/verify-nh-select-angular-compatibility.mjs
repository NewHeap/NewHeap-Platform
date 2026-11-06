import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const workspace = resolve(repositoryRoot, 'src/Front-end');
const dist = resolve(workspace, 'dist/nh-select');
const angularCli = resolve(workspace, 'node_modules/@angular/cli/bin/ng.js');
const npmCli = process.env.npm_execpath;

if (!npmCli) throw new Error('Run this compatibility check through npm.');

const matrix = [
  {
    angular: '20.3.28',
    tooling: '20.3.34',
    typescript: '5.9.3',
    zone: '0.15.1'
  },
  {
    angular: '21.2.21',
    tooling: '21.2.21',
    typescript: '5.9.3',
    zone: '0.16.0'
  },
  {
    angular: '22.1.3',
    tooling: '22.1.5',
    typescript: '6.0.2',
    zone: '0.16.0'
  }
];

const runNodeTool = (tool, args, cwd, stdio = 'inherit') => execFileSync(
  process.execPath,
  [tool, ...args],
  { cwd, encoding: stdio === 'pipe' ? 'utf8' : undefined, stdio }
);

// A consumer of both controls: a form control with 0 as a value and an empty choice, a multiselect
// with server search and create, and a combobox bound through value and valueChange.
const consumerSource = `
import { Component, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { bootstrapApplication } from '@angular/platform-browser';
import {
  NhComboboxComponent,
  NhMultiselectComponent,
  NhSelectOption,
  provideNhSelect
} from '@newheap/platform-select';
import { of } from 'rxjs';

@Component({
  selector: 'app-root',
  imports: [ReactiveFormsModule, NhComboboxComponent, NhMultiselectComponent],
  template: \`
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
  providers: [provideNhSelect({ texts: { noResults: 'Compatible' } })]
});
`;

const tempRoot = await mkdtemp(join(tmpdir(), 'nh-select-angular-'));

try {
  runNodeTool(angularCli, ['build', 'nh-select', '--configuration=production'], workspace);
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
        name: `nh-select-angular-${target.angular}`,
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
      writeFile(resolve(fixture, 'src/main.ts'), consumerSource)
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
    console.log(`Verified @newheap/platform-select with Angular ${target.angular}.`);
  }
} finally {
  await rm(tempRoot, { recursive: true, force: true });
}
