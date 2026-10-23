import { TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import {
  AgentUploads,
  Message,
  NH_ASSISTANT_UPLOAD_HEADER,
  NhAssistantApiError,
  NhAssistantApiService,
  NhAssistantSseEvent,
  NhAssistantStore,
  NhAssistantThreadComponent,
  SendMessageRequest,
  provideNhAssistant
} from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, lastValueFrom, toArray } from 'rxjs';

/**
 * Chat attachments against the mock API: the store checks a file before it uploads it, creates the
 * conversation an upload belongs to, follows the upload's progress and sends the stored files with
 * the message; the mock answers uploads and messages with the server's checks and codes, and the
 * thread shows the files a person attached by name, type and size.
 */
const uploads: AgentUploads = {
  acceptedMediaTypes: [
    'application/pdf',
    'image/png',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
  ],
  maxFileBytes: 1024,
  maxFiles: 2
};

function scenario(overrides: Partial<NhAssistantMockScenario> = {}): NhAssistantMockScenario {
  return {
    agents: [
      { id: 'documents', version: 1, displayNameKey: 'agents.documents', descriptionKey: 'agents.documents', canMutate: false, uploads },
      { id: 'notes', version: 1, displayNameKey: 'agents.notes', descriptionKey: 'agents.notes', canMutate: false }
    ],
    timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 64 },
    turns: [{ steps: [{ text: 'The plan moves the launch to May.' }] }],
    refuseUpload: upload => upload.name.startsWith('Virus') ? 'application-virus-found' : null,
    ...overrides
  };
}

function pdf(name = 'Plan.pdf', content = '%PDF-1.7 launch plan'): File {
  return new File([content], name, { type: 'application/pdf' });
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function until(condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 100 && !condition(); attempt++) {
    await flush();
  }
  expect(condition()).withContext('the condition was never met').toBeTrue();
}

describe('Chat attachments', () => {
  let store: NhAssistantStore;
  let api: NhAssistantApiService;
  let backend: NhAssistantMockBackend;

  async function setup(script = scenario()): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', defaultAgentId: 'documents' }),
        provideNhAssistantMockApi(script)
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    store = TestBed.inject(NhAssistantStore);
    api = TestBed.inject(NhAssistantApiService);
    backend = TestBed.inject(NhAssistantMockBackend);
    await store.initialize();
  }

  function uploadRequests(): number {
    return backend.requests.filter(request => request.path.endsWith('/files')).length;
  }

  async function upload(conversationId: string, file: Blob, name: string, header = true): Promise<{ status: number; code: string | null }> {
    const form = new FormData();
    form.append('file', file, name);
    const response = await backend.upload({
      url: `/api/assistant/conversations/${conversationId}/files`,
      headers: header ? { [NH_ASSISTANT_UPLOAD_HEADER]: '1' } : {},
      body: form,
      signal: new AbortController().signal,
      onProgress: () => undefined
    });
    const body = await response.json() as { code?: string };
    return { status: response.status, code: body.code ?? null };
  }

  it('uploads a file to a new conversation with progress and sends it with the message', async () => {
    await setup();
    const progress: number[] = [];

    const attaching = store.attach([pdf()]);
    expect(store.attachments().map(item => [item.name, item.state])).toEqual([['Plan.pdf', 'uploading']]);
    await attaching;
    // An upload belongs to a conversation: attaching in a new chat created it.
    const conversationId = store.activeConversation()!.id;
    await until(() => {
      progress.push(store.attachments()[0].progress);
      return store.attachments()[0].state === 'uploaded';
    });

    expect(progress.some(value => value > 0 && value < 1)).toBeTrue();
    const request = backend.requests.find(item => item.path === `conversations/${conversationId}/files`)!;
    expect(request.headers[NH_ASSISTANT_UPLOAD_HEADER]).toBe('1');
    expect(request.headers['Authorization']).toBe('Bearer token');
    const stored = backend.uploads[0].reference;
    expect(store.attachedFiles()).toEqual([stored]);

    expect(await store.submit('What does the plan say?', store.attachedFiles())).toBeTrue();

    // The message carries the reference; the composer is empty again.
    const message = backend.requests.find(item => item.path.endsWith('/messages'))!.body as SendMessageRequest;
    expect(message.files).toEqual([stored]);
    expect(store.attachments()).toEqual([]);
    await until(() => store.activeConversation()!.status === 'idle');
    const view = await firstValueFrom(api.getConversation(conversationId));
    const user = view.messages.find(item => item.role === 'user')!;
    expect(user.parts).toEqual([
      { type: 'text', text: 'What does the plan say?' },
      { type: 'file', name: 'Plan.pdf', mediaType: 'application/pdf', size: stored.size }
    ]);
  });

  it('checks the type, size and number of files before it uploads anything', async () => {
    await setup();

    await store.attach([
      new File(['notes'], 'Notes.txt', { type: 'text/plain' }),
      pdf('Large.pdf', '%PDF-1.7 ' + 'x'.repeat(2048)),
      new File([], 'Empty.pdf', { type: 'application/pdf' })
    ]);

    expect(store.attachments().map(item => [item.name, item.state, item.error?.code])).toEqual([
      ['Notes.txt', 'failed', 'assistant-file-type-not-accepted'],
      ['Large.pdf', 'failed', 'assistant-file-too-large'],
      ['Empty.pdf', 'failed', 'assistant-file-upload-invalid']
    ]);
    // Nothing went to the server, so no conversation was created either.
    expect(uploadRequests()).toBe(0);
    expect(store.activeConversation()).toBeNull();

    // A Word document the browser gives no type is recognized by its extension.
    await store.attach([new File(['PK\u0003\u0004word'], 'Brief.docx'), pdf('A.pdf'), pdf('B.pdf')]);
    await until(() => !store.uploading());

    expect(store.attachments().slice(3).map(item => [item.name, item.mediaType, item.state, item.error?.code ?? null])).toEqual([
      ['Brief.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'uploaded', null],
      ['A.pdf', 'application/pdf', 'uploaded', null],
      ['B.pdf', 'application/pdf', 'failed', 'assistant-file-count-limit']
    ]);
    expect(uploadRequests()).toBe(2);
  });

  it('gives a file without a usable type the accepted type of its extension, as the server does', async () => {
    const withCsv: AgentUploads = { ...uploads, acceptedMediaTypes: [...uploads.acceptedMediaTypes, 'text/csv'], maxFiles: 5 };
    await setup(scenario({
      agents: [{ id: 'documents', version: 1, displayNameKey: 'agents.documents', descriptionKey: 'agents.documents', canMutate: false, uploads: withCsv }]
    }));

    await store.attach([
      // The browser knows no type, gives the generic one, or the one Windows names after Excel.
      new File(['project;status'], 'Projects.csv'),
      new File(['project;status'], 'Status.csv', { type: 'application/octet-stream' }),
      new File(['project;status'], 'Export.csv', { type: 'application/vnd.ms-excel' }),
      // A specific type that is not accepted stays refused; an extension names only an accepted type.
      new File(['project;status'], 'Notes.csv', { type: 'text/plain' }),
      new File(['notes'], 'Notes.txt')
    ]);
    await until(() => !store.uploading());

    expect(store.attachments().map(item => [item.name, item.mediaType, item.state, item.error?.code ?? null])).toEqual([
      ['Projects.csv', 'text/csv', 'uploaded', null],
      ['Status.csv', 'text/csv', 'uploaded', null],
      ['Export.csv', 'text/csv', 'uploaded', null],
      ['Notes.csv', 'text/plain', 'failed', 'assistant-file-type-not-accepted'],
      ['Notes.txt', 'application/octet-stream', 'failed', 'assistant-file-type-not-accepted']
    ]);
    // The upload declares the type the check accepted, so the server stores text/csv.
    expect(backend.uploads.map(item => [item.name, item.mediaType])).toEqual([
      ['Projects.csv', 'text/csv'],
      ['Status.csv', 'text/csv'],
      ['Export.csv', 'text/csv']
    ]);

    // The mock, like the server, applies the rule to an upload that arrives without a type, and
    // still checks the content of the type the extension named.
    const conversationId = store.activeConversation()!.id;
    expect(await upload(conversationId, new Blob(['project;status']), 'Direct.csv')).toEqual({ status: 200, code: null });
    expect(await upload(conversationId, new Blob(['<script>']), 'Fake.pdf')).toEqual({ status: 415, code: 'assistant-file-type-not-accepted' });
  });

  it('derives the types of the extensions the application mapped and text/xml for .xml, as the server does', async () => {
    const mapped: AgentUploads = {
      acceptedMediaTypes: ['application/acad', 'application/xml'],
      maxFileBytes: 1024,
      maxFiles: 5,
      mappedExtensions: [
        { extension: '.dwg', mediaType: 'application/acad' },
        // Not accepted: the mapping names nothing.
        { extension: '.ifc', mediaType: 'application/x-step' }
      ]
    };
    await setup(scenario({
      agents: [{ id: 'documents', version: 1, displayNameKey: 'agents.documents', descriptionKey: 'agents.documents', canMutate: false, uploads: mapped }]
    }));

    await store.attach([
      new File(['AC1032'], 'Floor plan.dwg'),
      new File(['AC1032'], 'Section.DWG', { type: 'application/octet-stream' }),
      // Browsers send text/xml for .xml; the table names application/xml.
      new File(['<plan/>'], 'Plan.xml', { type: 'text/xml' }),
      new File(['ISO-10303-21;'], 'Model.ifc'),
      new File(['AC1032'], 'Drawing.dwg', { type: 'image/vnd.dwg' })
    ]);
    await until(() => !store.uploading());

    expect(store.attachments().map(item => [item.name, item.mediaType, item.state, item.error?.code ?? null])).toEqual([
      ['Floor plan.dwg', 'application/acad', 'uploaded', null],
      ['Section.DWG', 'application/acad', 'uploaded', null],
      ['Plan.xml', 'application/xml', 'uploaded', null],
      ['Model.ifc', 'application/octet-stream', 'failed', 'assistant-file-type-not-accepted'],
      ['Drawing.dwg', 'image/vnd.dwg', 'failed', 'assistant-file-type-not-accepted']
    ]);

    // The mock derives with the mappings for an upload that arrives without a type.
    const conversationId = store.activeConversation()!.id;
    expect(await upload(conversationId, new Blob(['AC1032']), 'Direct.dwg')).toEqual({ status: 200, code: null });
    expect(backend.uploads.map(item => [item.name, item.mediaType])).toEqual([
      ['Floor plan.dwg', 'application/acad'],
      ['Section.DWG', 'application/acad'],
      ['Plan.xml', 'application/xml'],
      ['Direct.dwg', 'application/acad']
    ]);
  });

  it('refuses text whose content starts as a binary format and accepts UTF-16 text, as the server does', async () => {
    const text: AgentUploads = { acceptedMediaTypes: ['text/csv', 'text/plain'], maxFileBytes: 1024, maxFiles: 5 };
    await setup(scenario({
      agents: [{ id: 'documents', version: 1, displayNameKey: 'agents.documents', descriptionKey: 'agents.documents', canMutate: false, uploads: text }]
    }));
    const conversationId = (await firstValueFrom(api.createConversation({ agentId: 'documents' }))).id;
    // An .xls workbook renamed to .csv starts as an OLE2 compound file.
    const workbook = new Uint8Array([0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0, 0, 0, 0]);
    // Excel's "Unicode text" export: UTF-16LE with a byte order mark, so every other byte is NUL.
    const unicode = new Uint8Array([0xff, 0xfe, ...Array.from('project\tstatus').flatMap(char => [char.charCodeAt(0), 0])]);

    expect(await upload(conversationId, new Blob([workbook], { type: 'application/vnd.ms-excel' }), 'Export.csv'))
      .toEqual({ status: 415, code: 'assistant-file-type-not-accepted' });
    expect(await upload(conversationId, new Blob(['PK\u0003\u0004word/'], { type: 'text/plain' }), 'Notes.txt'))
      .toEqual({ status: 415, code: 'assistant-file-type-not-accepted' });
    expect(await upload(conversationId, new Blob([unicode], { type: 'text/plain' }), 'Projects.txt')).toEqual({ status: 200, code: null });
    expect(backend.uploads.map(item => [item.name, item.mediaType])).toEqual([['Projects.txt', 'text/plain']]);
  });

  it('stops an upload that is removed and offers no attachments for an agent that takes none', async () => {
    await setup();
    await store.attach([pdf('First.pdf')]);
    await until(() => !store.uploading());

    void store.attach([pdf('Second.pdf')]);
    store.removeAttachment(store.attachments()[1].id);
    await flush();
    await flush();

    expect(store.attachments().map(item => item.name)).toEqual(['First.pdf']);
    expect(backend.uploads.map(item => item.name)).toEqual(['First.pdf']);

    store.selectAgent('notes');
    expect(store.uploads()).toBeNull();
  });

  it('answers uploads with the server checks and codes', async () => {
    await setup();
    const documents = (await firstValueFrom(api.createConversation({ agentId: 'documents' }))).id;
    const notes = (await firstValueFrom(api.createConversation({ agentId: 'notes' }))).id;

    expect(await upload(documents, pdf(), 'Plan.pdf', false)).toEqual({ status: 400, code: 'assistant-file-upload-invalid' });
    expect(await upload(documents, new Blob(['notes'], { type: 'text/plain' }), 'Notes.txt'))
      .toEqual({ status: 415, code: 'assistant-file-type-not-accepted' });
    // Content that is not what its type says.
    expect(await upload(documents, new Blob(['<script>'], { type: 'application/pdf' }), 'Fake.pdf'))
      .toEqual({ status: 415, code: 'assistant-file-type-not-accepted' });
    expect(await upload(documents, pdf('Large.pdf', '%PDF-1.7 ' + 'x'.repeat(2048)), 'Large.pdf'))
      .toEqual({ status: 413, code: 'assistant-file-too-large' });
    expect(await upload(documents, pdf('Virus.pdf'), 'Virus.pdf')).toEqual({ status: 422, code: 'assistant-file-upload-refused' });
    expect(await upload(notes, pdf(), 'Plan.pdf')).toEqual({ status: 409, code: 'assistant-files-not-accepted' });
    expect(backend.uploads).toEqual([]);

    // The client reports a refused upload with its code.
    const refused = await lastValueFrom(api.uploadFile(documents, new Blob(['<script>'], { type: 'application/pdf' }), 'Fake.pdf'))
      .catch((error: unknown) => error);
    expect(refused).toEqual(jasmine.any(NhAssistantApiError));
    expect((refused as NhAssistantApiError).code).toBe('assistant-file-type-not-accepted');
  });

  it('shows a file the upload scanner refused or could not scan at the control with its own code', async () => {
    await setup(scenario({
      scanUpload: upload => upload.name.startsWith('Eicar') ? 'refused' : upload.name.startsWith('Offline') ? 'failed' : null
    }));

    await store.attach([pdf('Eicar.pdf'), pdf('Offline.pdf')]);
    await until(() => !store.uploading());
    await store.attach([pdf('Plan.pdf')]);
    await until(() => !store.uploading());

    expect(store.attachments().map(item => [item.name, item.state, item.error?.code ?? null])).toEqual([
      ['Eicar.pdf', 'failed', 'assistant-file-scan-refused'],
      ['Offline.pdf', 'failed', 'assistant-file-upload-failed'],
      ['Plan.pdf', 'uploaded', null]
    ]);
    // Only the file the scanner let pass reached the application's storage.
    expect(backend.uploads.map(item => item.name)).toEqual(['Plan.pdf']);
    const response = await upload(store.activeConversation()!.id, pdf('Eicar.pdf'), 'Eicar.pdf');
    expect(response).toEqual({ status: 422, code: 'assistant-file-scan-refused' });
    expect(TestBed.inject(TranslateService).instant(store.attachments()[0].error!.messageKey))
      .toBe('The file did not pass the scan for harmful content and was not stored.');
  });

  it('refuses a message whose files it cannot take, as the server does', async () => {
    await setup();
    const id = (await firstValueFrom(api.createConversation({ agentId: 'documents' }))).id;
    const reference = { id: 'unknown', name: 'Plan.pdf', mediaType: 'application/pdf', size: 10 };

    const tooMany = await firstValueFrom(api.sendMessage(id, {
      text: 'Read these.',
      clientMessageId: 'c-1',
      files: [reference, { ...reference, id: 'two' }, { ...reference, id: 'three' }]
    }).pipe(toArray()));
    const missing = await firstValueFrom(api.sendMessage(id, { text: 'Read this.', clientMessageId: 'c-2', files: [reference] }).pipe(toArray()));

    expect(tooMany).toEqual([{ type: 'error', data: { code: 'assistant-file-count-limit', messageKey: 'nh-assistant.errors.assistant-file-count-limit' } }]);
    expect(missing.map(event => event.type)).toEqual(['turn.started', 'error']);
    expect((missing[1] as Extract<NhAssistantSseEvent, { type: 'error' }>).data.code).toBe('assistant-file-not-found');
    // The message keeps only what the turn read.
    const user = (await firstValueFrom(api.getConversation(id))).messages.find(item => item.role === 'user')!;
    expect(user.parts.map(part => part.type)).toEqual(['text']);
  });

  it('shows the files of a message by name, type and size', async () => {
    await setup();
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    const messages: Message[] = [{
      id: 'm-1',
      role: 'user',
      createdAt: '2026-10-22T10:00:00Z',
      parts: [
        { type: 'text', text: 'What do these say?' },
        { type: 'file', name: 'Launch plan with a very long name that does not fit.pdf', mediaType: 'application/pdf', size: 1536 },
        { type: 'file', name: 'Scan.png', mediaType: 'image/png', size: 2 * 1024 * 1024 }
      ]
    }];
    fixture.componentRef.setInput('messages', messages);
    fixture.detectChanges();

    const list = (fixture.nativeElement as HTMLElement).querySelector('ul.files')!;
    expect(list.getAttribute('aria-label')).toBe('Attached files');
    const files = Array.from(list.querySelectorAll('li')).map(item => [
      item.querySelector('.file-name')!.textContent!.trim(),
      item.querySelector('.file-meta')!.textContent!.trim()
    ]);
    expect(files).toEqual([
      ['Launch plan with a very long name that does not fit.pdf', 'PDF, 1.5 KB'],
      ['Scan.png', 'PNG, 2 MB']
    ]);
  });

  it('shows long file names on a 375px screen without horizontal overflow', async () => {
    await setup();
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'flex';
    host.style.width = '343px';
    host.style.height = '400px';
    fixture.componentRef.setInput('messages', [{
      id: 'm-1',
      role: 'user',
      createdAt: '2026-10-22T10:00:00Z',
      parts: [
        { type: 'text', text: 'Compare these.' },
        { type: 'file', name: `Board-minutes-${'without-spaces-'.repeat(8)}.pdf`, mediaType: 'application/pdf', size: 3 * 1024 * 1024 },
        { type: 'file', name: 'Budget.xlsx', mediaType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', size: 2048 }
      ]
    }] satisfies Message[]);
    fixture.detectChanges();

    const scroller = host.querySelector<HTMLElement>('.scroller')!;
    expect(scroller.scrollWidth).toBeLessThanOrEqual(scroller.clientWidth);
    const name = host.querySelector<HTMLElement>('.file-name')!;
    expect(name.scrollWidth).toBeGreaterThan(name.clientWidth);
    const right = scroller.getBoundingClientRect().right;
    for (const file of Array.from(host.querySelectorAll<HTMLElement>('li.file'))) {
      expect(file.getBoundingClientRect().right).toBeLessThanOrEqual(right + 0.5);
    }
    expect(host.querySelectorAll('li.file')[1].querySelector('.file-meta')!.textContent!.trim()).toBe('Excel, 2 KB');
  });
});
