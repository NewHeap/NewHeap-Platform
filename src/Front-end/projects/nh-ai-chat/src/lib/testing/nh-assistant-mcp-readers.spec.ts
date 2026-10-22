import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { McpServerInput, NhAssistantApiError } from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAdminMcpServersComponent } from '../../../admin/src/public-api';

const docx = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
const xlsx = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    admin: {
      mcpServers: [
        {
          id: 'files', displayName: 'Files', url: 'https://files.example.com/mcp', authMode: 'none', headerName: null,
          requiredPolicy: null, isEnabled: true,
          remoteTools: [
            { remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a1', readOnlyHint: true },
            { remoteName: 'convertFile', description: 'Converts a file to text', inputSchemaHash: 'b1', readOnlyHint: true }
          ]
        },
        {
          id: 'documents', displayName: 'Documents', url: 'https://documents.example.com/mcp', authMode: 'none', headerName: null,
          requiredPolicy: null, isEnabled: true,
          remoteTools: [
            { remoteName: 'read_document', description: 'Turns a document into text', inputSchemaHash: 'c1', readOnlyHint: true },
            { remoteName: 'read_sheet', description: 'Turns a sheet into text', inputSchemaHash: 'd1', readOnlyHint: true }
          ],
          readers: [
            { mediaTypes: [docx], toolName: 'read_document', maxClassification: 'internal' },
            { mediaTypes: [xlsx], toolName: 'read_sheet', maxClassification: 'confidential', priority: 10 }
          ]
        },
        {
          // Its second reader names a tool the server does not offer.
          id: 'archive', displayName: 'Archive', url: 'https://archive.example.com/mcp', authMode: 'none', headerName: null,
          requiredPolicy: null, isEnabled: true,
          remoteTools: [{ remoteName: 'read_document', description: 'Turns a document into text', inputSchemaHash: 'e1', readOnlyHint: true }],
          readers: [
            { mediaTypes: [docx], toolName: 'read_document', maxClassification: 'internal', priority: 1 },
            { mediaTypes: [xlsx], toolName: 'legacy_read', maxClassification: 'internal' }
          ]
        }
      ]
    }
  };
}

const files: McpServerInput = {
  id: 'files', displayName: 'Files', url: 'https://files.example.com/mcp', authMode: 'none', headerName: null, requiredPolicy: null, isEnabled: true
};

async function configure(): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

async function failure(promise: Promise<unknown>): Promise<NhAssistantApiError> {
  return promise.then(() => { throw new Error('Expected a failure.'); }, error => error as NhAssistantApiError);
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve));
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function buttonNamed(element: HTMLElement, text: string, index = 0): HTMLButtonElement {
  return Array.from(element.querySelectorAll('button')).filter(item => item.textContent?.trim() === text)[index];
}

function readerRows(element: HTMLElement): HTMLElement[] {
  return Array.from(element.querySelectorAll<HTMLElement>('fieldset.reader'));
}

function control<T extends HTMLElement>(row: HTMLElement, field: 'tool' | 'classification' | 'types' | 'priority'): T {
  return row.querySelector(`[id*="-reader-${field}-"]:not(p)`) as T;
}

function choose(row: HTMLElement, field: 'tool' | 'classification' | 'types' | 'priority', value: string): void {
  const element = control<HTMLInputElement | HTMLSelectElement>(row, field);
  element.value = value;
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
}

function puts(serverId: string): McpServerInput[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(request => request.method === 'PUT' && request.path === `admin/mcp-servers/${serverId}`)
    .map(request => request.body as McpServerInput);
}

describe('MCP servers as readers', () => {
  beforeEach(configure);

  it('stores several readers of a server once their tools are synced, with field errors per reader, as the server does', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const reader = { mediaTypes: [docx], toolName: 'readFile', maxClassification: 'internal' as const };

    const beforeSync = await failure(firstValueFrom(admin.updateMcpServer('files', { ...files, readers: [reader] })));
    await firstValueFrom(admin.syncMcpServer('files'));
    const invalid = await failure(firstValueFrom(admin.updateMcpServer('files', {
      ...files,
      readers: [reader, { mediaTypes: ['word', docx], toolName: null, maxClassification: 'secret' as never, priority: 1001 }]
    })));
    const sameTool = await failure(firstValueFrom(admin.updateMcpServer('files', {
      ...files, readers: [reader, { ...reader, mediaTypes: [xlsx] }]
    })));
    const tooMany = await failure(firstValueFrom(admin.updateMcpServer('files', {
      ...files, readers: [{ ...reader, mediaTypes: Array.from({ length: 17 }, (_, index) => `application/x-type-${index}`) }]
    })));
    const tooManyReaders = await failure(firstValueFrom(admin.updateMcpServer('files', {
      ...files, readers: Array.from({ length: 17 }, () => reader)
    })));
    const stored = await firstValueFrom(admin.updateMcpServer('files', {
      ...files,
      readers: [
        { ...reader, mediaTypes: [`Application/VND.openxmlformats-officedocument.wordprocessingml.document; charset=binary`, docx], maxClassification: 'confidential' },
        { mediaTypes: [xlsx], toolName: 'convertFile', maxClassification: 'internal', priority: 5 }
      ]
    }));
    const kept = await firstValueFrom(admin.updateMcpServer('files', files));
    const removed = await firstValueFrom(admin.updateMcpServer('files', { ...files, readers: [] }));
    const preconfigured = (await firstValueFrom(admin.getMcpServers())).find(server => server.id === 'documents');

    expect(beforeSync.fieldErrors).toEqual({ 'readers[0].toolName': ['not-found'] });
    expect(invalid.fieldErrors).toEqual({
      'readers[1].mediaTypes': ['invalid'],
      'readers[1].toolName': ['required'],
      'readers[1].maxClassification': ['invalid'],
      'readers[1].priority': ['invalid']
    });
    expect(sameTool.fieldErrors).toEqual({ 'readers[1].toolName': ['invalid'] });
    expect(tooMany.fieldErrors).toEqual({ 'readers[0].mediaTypes': ['too-long'] });
    expect(tooManyReaders.fieldErrors).toEqual({ readers: ['too-long'] });
    // By priority, highest first; an omitted priority is 0.
    expect(stored.readers).toEqual([
      { mediaTypes: [xlsx], toolName: 'convertFile', maxClassification: 'internal', priority: 5 },
      { mediaTypes: [docx], toolName: 'readFile', maxClassification: 'confidential', priority: 0 }
    ]);
    expect(kept.readers).toEqual(stored.readers);
    expect(removed.readers).toEqual([]);
    expect(preconfigured?.readers.map(item => [item.toolName, item.priority])).toEqual([['read_sheet', 10], ['read_document', 0]]);
  });

  it('adds, changes and removes readers in the editor and saves them as shown', async () => {
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).syncMcpServer('files'));
    const fixture = TestBed.createComponent(NhAssistantAdminMcpServersComponent);
    let element = await settle(fixture);

    buttonNamed(element, 'Edit', 0).click();
    element = await settle(fixture);
    expect(element.textContent).toContain('Reading documents');
    expect(element.textContent).toContain('This server reads no files.');
    expect(readerRows(element)).toEqual([]);

    // A new reader asks for its tool, media types and classification before it saves; the focus
    // moves to its tool.
    buttonNamed(element, 'Add reader').click();
    element = await settle(fixture);
    const [first] = readerRows(element);
    expect(first.querySelector('legend')?.textContent?.trim()).toBe('Reader 1');
    expect(document.activeElement).toBe(control(first, 'tool'));
    expect(Array.from(control<HTMLSelectElement>(first, 'tool').options).map(option => option.value)).toEqual(['', 'readFile', 'convertFile']);
    expect(Array.from(control<HTMLSelectElement>(first, 'classification').options).map(option => option.value))
      .toEqual(['', 'public', 'internal', 'confidential', 'restricted']);
    buttonNamed(element, 'Save').click();
    element = await settle(fixture);
    expect(puts('files')).toEqual([]);
    expect(first.querySelectorAll('[aria-invalid="true"]').length).toBe(3);
    expect(element.textContent).toContain('A file sent to this server leaves the application.');

    choose(first, 'tool', 'readFile');
    choose(first, 'classification', 'confidential');
    choose(first, 'types', `${docx}, ${xlsx}`);
    choose(first, 'priority', '2000');

    // A second reader of the same tool is refused at its field.
    buttonNamed(element, 'Add reader').click();
    element = await settle(fixture);
    const second = readerRows(element)[1];
    expect(second.querySelector('legend')?.textContent?.trim()).toBe('Reader 2');
    choose(second, 'tool', 'readFile');
    choose(second, 'classification', 'internal');
    choose(second, 'types', xlsx);
    element = await settle(fixture);
    buttonNamed(element, 'Save').click();
    element = await settle(fixture);
    expect(puts('files')).toEqual([]);
    expect(readerRows(element)[0].textContent).toContain('Enter a whole number from -1000 to 1000.');
    expect(readerRows(element)[1].textContent).toContain('Another reader of this server already uses this tool.');

    choose(readerRows(element)[0], 'priority', '-5');
    choose(readerRows(element)[1], 'tool', 'convertFile');
    choose(readerRows(element)[1], 'priority', '7');
    element = await settle(fixture);
    buttonNamed(element, 'Save').click();
    element = await settle(fixture);

    expect(puts('files').map(body => body.readers)).toEqual([[
      { mediaTypes: [docx, xlsx], toolName: 'readFile', maxClassification: 'confidential', priority: -5 },
      { mediaTypes: [xlsx], toolName: 'convertFile', maxClassification: 'internal', priority: 7 }
    ]]);
    let saved = (await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getMcpServers())).find(server => server.id === 'files');
    expect(saved?.readers.map(reader => reader.toolName)).toEqual(['convertFile', 'readFile']);

    // Editing again shows the stored readers, highest priority first; removing one saves the other.
    buttonNamed(element, 'Edit', 0).click();
    element = await settle(fixture);
    expect(readerRows(element).map(row => control<HTMLSelectElement>(row, 'tool').value)).toEqual(['convertFile', 'readFile']);
    expect(readerRows(element).map(row => control<HTMLInputElement>(row, 'priority').value)).toEqual(['7', '-5']);
    const remove = buttonNamed(readerRows(element)[0], 'Remove');
    expect(remove.getAttribute('aria-label')).toBe('Remove reader 1');
    remove.click();
    element = await settle(fixture);
    expect(document.activeElement?.textContent?.trim()).toBe('Add reader');
    buttonNamed(element, 'Save').click();
    await settle(fixture);

    expect(puts('files')[1].readers).toEqual([{ mediaTypes: [docx, xlsx], toolName: 'readFile', maxClassification: 'confidential', priority: -5 }]);
    saved = (await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getMcpServers())).find(server => server.id === 'files');
    expect(saved?.readers.map(reader => reader.toolName)).toEqual(['readFile']);
  });

  it('shows a server field error of a reader at that reader', async () => {
    const fixture = TestBed.createComponent(NhAssistantAdminMcpServersComponent);
    let element = await settle(fixture);

    buttonNamed(element, 'Edit', 2).click();
    element = await settle(fixture);
    // A stored tool the server no longer offers stays selectable, so the editor shows what is saved.
    expect(readerRows(element).map(row => control<HTMLSelectElement>(row, 'tool').value)).toEqual(['read_document', 'legacy_read']);
    buttonNamed(element, 'Save').click();
    element = await settle(fixture);

    expect(puts('archive').length).toBe(1);
    const tool = control<HTMLSelectElement>(readerRows(element)[1], 'tool');
    expect(tool.getAttribute('aria-invalid')).toBe('true');
    const errorId = tool.getAttribute('aria-describedby')!.split(' ')[1];
    expect(element.querySelector(`[id="${errorId}"]`)?.textContent).toContain('The application does not know this name.');
    expect(control<HTMLSelectElement>(readerRows(element)[0], 'tool').getAttribute('aria-invalid')).toBeNull();
    // The error is shown at the reader, not again in the form's list of other errors.
    expect(element.querySelector('[data-server-error] ul')).toBeNull();
  });

  it('fits its readers into 375 pixels without scrolling sideways', async () => {
    const fixture = TestBed.createComponent(NhAssistantAdminMcpServersComponent);
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '375px';
    let element = await settle(fixture);

    buttonNamed(element, 'Edit', 1).click();
    element = await settle(fixture);

    const fieldset = element.querySelector('fieldset.readers') as HTMLElement;
    expect(readerRows(element).length).toBe(2);
    expect(fieldset.scrollWidth).toBeLessThanOrEqual(fieldset.clientWidth);
    expect(host.scrollWidth).toBeLessThanOrEqual(375);
    const columns = (row: HTMLElement) =>
      new Set(Array.from(row.querySelectorAll<HTMLElement>('select, input')).map(control => Math.round(control.getBoundingClientRect().left))).size;
    // One column: every control of a reader starts at the same left edge.
    expect(readerRows(element).map(columns)).toEqual([1, 1]);

    // A wide administration places the fields of a reader side by side.
    host.style.width = '1100px';
    element = await settle(fixture);
    expect(readerRows(element).every(row => columns(row) > 1)).toBeTrue();
  });
});
