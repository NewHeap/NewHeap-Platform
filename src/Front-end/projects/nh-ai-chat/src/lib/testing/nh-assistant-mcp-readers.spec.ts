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
          remoteTools: [{ remoteName: 'readFile', description: 'Reads a file', inputSchemaHash: 'a1', readOnlyHint: true }]
        },
        {
          id: 'documents', displayName: 'Documents', url: 'https://documents.example.com/mcp', authMode: 'none', headerName: null,
          requiredPolicy: null, isEnabled: true,
          remoteTools: [{ remoteName: 'read_document', description: 'Turns a document into text', inputSchemaHash: 'c1', readOnlyHint: true }],
          reader: { mediaTypes: [docx], toolName: 'read_document', maxClassification: 'internal' }
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

function editButton(element: HTMLElement, index: number): HTMLButtonElement {
  return Array.from(element.querySelectorAll('button')).filter(item => item.textContent?.trim() === 'Edit')[index];
}

function saveButton(element: HTMLElement): HTMLButtonElement {
  return Array.from(element.querySelectorAll('button')).find(item => item.textContent?.trim() === 'Save')!;
}

describe('MCP servers as readers', () => {
  beforeEach(configure);

  it('sets a server up as reader once its reading tool is synced, as the server does', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const reader = { mediaTypes: [docx], toolName: 'readFile', maxClassification: 'internal' as const };

    const beforeSync = await failure(firstValueFrom(admin.updateMcpServer('files', { ...files, reader })));
    await firstValueFrom(admin.syncMcpServer('files'));
    const invalid = await failure(firstValueFrom(admin.updateMcpServer('files', {
      ...files, reader: { mediaTypes: ['word', docx], toolName: null, maxClassification: 'secret' as never }
    })));
    const tooMany = await failure(firstValueFrom(admin.updateMcpServer('files', {
      ...files, reader: { ...reader, mediaTypes: Array.from({ length: 17 }, (_, index) => `application/x-type-${index}`) }
    })));
    const stored = await firstValueFrom(admin.updateMcpServer('files', {
      ...files, reader: { ...reader, mediaTypes: [`Application/VND.openxmlformats-officedocument.wordprocessingml.document; charset=binary`, xlsx, docx], maxClassification: 'confidential' }
    }));
    const kept = await firstValueFrom(admin.updateMcpServer('files', files));
    const removed = await firstValueFrom(admin.updateMcpServer('files', { ...files, reader: { mediaTypes: [], toolName: null, maxClassification: null } }));
    const preconfigured = (await firstValueFrom(admin.getMcpServers())).find(server => server.id === 'documents');

    expect(beforeSync.fieldErrors).toEqual({ 'reader.toolName': ['not-found'] });
    expect(invalid.fieldErrors).toEqual({
      'reader.mediaTypes': ['invalid'],
      'reader.toolName': ['required'],
      'reader.maxClassification': ['invalid']
    });
    expect(tooMany.fieldErrors).toEqual({ 'reader.mediaTypes': ['too-long'] });
    expect(stored.reader).toEqual({ mediaTypes: [docx, xlsx], toolName: 'readFile', maxClassification: 'confidential' });
    expect(kept.reader).toEqual(stored.reader);
    expect(removed.reader).toBeNull();
    expect(preconfigured?.reader).toEqual({ mediaTypes: [docx], toolName: 'read_document', maxClassification: 'internal' });
  });

  it('chooses the media types, the synced reading tool and the highest classification in the editor', async () => {
    await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).syncMcpServer('files'));
    const fixture = TestBed.createComponent(NhAssistantAdminMcpServersComponent);
    let element = await settle(fixture);

    editButton(element, 0).click();
    element = await settle(fixture);
    expect(element.textContent).toContain('Reading documents');
    const types = element.querySelector('input[id$="-reader-types"]') as HTMLInputElement;
    expect(types.value).toBe('');
    // A new type asks for the tool and the classification before it saves.
    types.value = `${docx}, ${xlsx}`;
    types.dispatchEvent(new Event('input'));
    element = await settle(fixture);
    saveButton(element).click();
    element = await settle(fixture);
    expect(TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.method === 'PUT')).toEqual([]);
    expect(element.textContent).toContain('A file sent to this server leaves the application.');

    const tool = element.querySelector('select[id$="-reader-tool"]') as HTMLSelectElement;
    expect(Array.from(tool.options).map(option => option.value)).toEqual(['', 'readFile']);
    tool.value = 'readFile';
    tool.dispatchEvent(new Event('change'));
    const classification = element.querySelector('select[id$="-reader-classification"]') as HTMLSelectElement;
    expect(Array.from(classification.options).map(option => option.value)).toEqual(['', 'public', 'internal', 'confidential', 'restricted']);
    classification.value = 'confidential';
    classification.dispatchEvent(new Event('change'));
    element = await settle(fixture);
    saveButton(element).click();
    await settle(fixture);

    const [body] = TestBed.inject(NhAssistantMockBackend).requests
      .filter(request => request.method === 'PUT' && request.path === 'admin/mcp-servers/files')
      .map(request => request.body as McpServerInput);
    expect(body.reader).toEqual({ mediaTypes: [docx, xlsx], toolName: 'readFile', maxClassification: 'confidential' });
    const saved = (await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getMcpServers())).find(server => server.id === 'files');
    expect(saved?.reader?.toolName).toBe('readFile');
  });
});
