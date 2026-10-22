import { TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NH_ASSISTANT_FETCH, NhAssistantFetch } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from './nh-assistant-admin-api.service';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { nhAssistantMcpAuthorizationFromFragment } from './nh-assistant-mcp-authorization';
import { NhAssistantMcpConnectService } from './nh-assistant-mcp-connect.service';
import { NhAssistantApiError, nhAssistantIsSafeCode } from './nh-assistant-transport';

/**
 * Failures of the transport-level calls reach callers as coded `NhAssistantApiError`s: downloads, the
 * account names asked for in parts, and the completion of a redirect-mode MCP authorization, which
 * reports every outcome in `returned` instead of erroring.
 */
function errorBody(code: string): Blob {
  return new Blob([JSON.stringify({ code, messageKey: `nh-assistant.errors.${code}` })], { type: 'application/octet-stream' });
}

async function rejection(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(() => { throw new Error('Expected a failure.'); }, error => error);
}

describe('Downloads of the assistant API', () => {
  let fetchSpy: jasmine.Spy<NhAssistantFetch>;

  beforeEach(() => {
    fetchSpy = jasmine.createSpy<NhAssistantFetch>('fetch');
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NH_ASSISTANT_FETCH, useFactory: () => fetchSpy }
      ]
    });
  });

  it('reject with the coded error body of a refused download, also when it is sent as a file', async () => {
    const runs = TestBed.inject(NhAssistantAgentRunApiService);
    fetchSpy.and.callFake(async () => new Response(errorBody('assistant-artifact-not-found'), { status: 404 }));

    for (const download of [() => runs.downloadRunArtifact('run-1', 'file-1'), () => runs.downloadArtifact('thread-1', 'file-1')]) {
      const failure = await rejection(download());
      expect(failure).toBeInstanceOf(NhAssistantApiError);
      expect(failure).toEqual(jasmine.objectContaining({
        status: 404,
        code: 'assistant-artifact-not-found',
        messageKey: 'nh-assistant.errors.assistant-artifact-not-found'
      }));
    }
    expect(fetchSpy.calls.argsFor(0)[0]).toBe('/api/assistant/runs/run-1/artifacts/file-1/content');
  });

  it('turn a lost connection, a body without a code and a broken file into codes, never raw errors', async () => {
    const runs = TestBed.inject(NhAssistantAgentRunApiService);

    fetchSpy.and.returnValue(Promise.reject(new TypeError('Failed to fetch')));
    expect(await rejection(runs.downloadRunArtifact('run-1', 'file-1'))).toEqual(jasmine.objectContaining({ status: 0, code: 'assistant-network' }));

    fetchSpy.and.callFake(async () => new Response('Internal error with a stack trace', { status: 500 }));
    const server = await rejection(runs.downloadRunArtifact('run-1', 'file-1')) as NhAssistantApiError;
    expect(server.code).toBe('assistant-server');
    expect(server.message).not.toContain('stack trace');

    const broken = { ok: true, status: 200, blob: () => Promise.reject(new TypeError('The stream broke off.')) } as unknown as Response;
    fetchSpy.and.returnValue(Promise.resolve(broken));
    expect(await rejection(runs.downloadRunArtifact('run-1', 'file-1'))).toEqual(jasmine.objectContaining({ code: 'assistant-stream-interrupted' }));

    fetchSpy.and.callFake(async () => new Response(new Blob(['file content']), { status: 200 }));
    expect(await (await runs.downloadRunArtifact('run-1', 'file-1')).text()).toBe('file content');
  });

  it('give the administration exports the same coded failures', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    fetchSpy.and.callFake(async () => new Response(errorBody('assistant-forbidden'), { status: 403 }));

    expect(await rejection(admin.exportAgentFeedback('projects'))).toEqual(jasmine.objectContaining({ status: 403, code: 'assistant-forbidden' }));
  });

  it('ask for account names in parts of 50 distinct ids and fail when a part fails', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    fetchSpy.and.callFake(async (url: string) => {
      const ids = new URL(url, 'http://localhost').searchParams.get('ids')!.split(',');
      return new Response(JSON.stringify(ids.map(actorId => ({ actorId, displayName: actorId.toUpperCase(), detail: null }))), { status: 200 });
    });
    const ids = Array.from({ length: 120 }, (_, index) => `account-${index}`);

    const accounts = await firstValueFrom(admin.getAccounts([...ids, ...ids.slice(0, 10), '', 'x'.repeat(257)]));

    expect(accounts.map(account => account.actorId)).toEqual(ids);
    const parts = fetchSpy.calls.allArgs().map(([url]) => new URL(url, 'http://localhost').searchParams.get('ids')!.split(','));
    expect(parts.map(part => part.length)).toEqual([50, 50, 20]);
    expect(await firstValueFrom(admin.getAccounts([]))).toEqual([]);
    expect(fetchSpy.calls.count()).toBe(3);

    fetchSpy.and.callFake(async (url: string) => url.includes('account-50')
      ? new Response(JSON.stringify({ code: 'assistant-validation', messageKey: 'nh-assistant.errors.assistant-validation', errors: { ids: ['invalid'] } }), { status: 400 })
      : new Response('[]', { status: 200 }));
    const failure = await rejection(firstValueFrom(admin.getAccounts(ids))) as NhAssistantApiError;
    expect(failure.code).toBe('assistant-validation');
    expect(failure.fieldErrors).toEqual({ ids: ['invalid'] });
  });

  it('keep the field errors of a list item by its index and drop field names that are not safe', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    fetchSpy.and.callFake(async () => new Response(JSON.stringify({
      code: 'assistant-validation',
      errors: {
        'readers[0].toolName': ['not-found'],
        'readers[12]': ['invalid'],
        'oauth.scopes': ['invalid'],
        'readers[x].toolName': ['invalid'],
        'readers[0].<b>': ['invalid'],
        '[0].toolName': ['invalid']
      }
    }), { status: 400 }));

    const failure = await rejection(firstValueFrom(admin.updateMcpServer('files', {
      id: 'files', displayName: 'Files', url: 'https://files.example.com/mcp', authMode: 'none', headerName: null, requiredPolicy: null, isEnabled: true
    }))) as NhAssistantApiError;

    expect(failure.fieldErrors).toEqual({
      'readers[0].toolName': ['not-found'],
      'readers[12]': ['invalid'],
      'oauth.scopes': ['invalid']
    });
  });
});

describe('The safe code check of the assistant', () => {
  it('accepts lowercase codes and refuses free text', () => {
    expect(nhAssistantIsSafeCode('assistant-mcp-authorization-failed')).toBeTrue();
    expect(nhAssistantIsSafeCode('Bad <b>code</b>')).toBeFalse();
    expect(nhAssistantIsSafeCode('a'.repeat(81))).toBeFalse();
    expect(nhAssistantIsSafeCode(42)).toBeFalse();
  });

  it('reads a redirect fragment with its message key by the code alone', () => {
    expect(nhAssistantMcpAuthorizationFromFragment('#nh-mcp-error=assistant-mcp-authorization-failed&nh-mcp-message-key=nh-assistant.errors.assistant-mcp-authorization-failed'))
      .toEqual({ flowId: null, code: 'assistant-mcp-authorization-failed' });
    expect(nhAssistantMcpAuthorizationFromFragment('#nh-mcp-error=%3Cscript%3E&nh-mcp-message-key=x'))
      .toEqual({ flowId: null, code: 'assistant-mcp-authorization-failed' });
  });
});

describe('Completing a redirect-mode MCP authorization', () => {
  const remembered = 'nh-assistant.mcp-connect.server';
  let original: string;

  function scenario(): NhAssistantMockScenario {
    return {
      agents: [{ id: 'projects', version: 1, displayNameKey: 'Projects', descriptionKey: 'Projects', canMutate: true }],
      turns: [],
      admin: {
        agents: [{
          source: 'code', id: 'projects', displayName: 'Projects', description: '', instructions: '', toolSelectors: [],
          mcpServerIds: ['calendar'], requiredPolicy: null, autonomy: 'execute'
        }],
        mcpServers: [{
          id: 'calendar', displayName: 'Team calendar', url: 'https://calendar.example.com/mcp', authMode: 'oauth', headerName: null,
          requiredPolicy: null, isEnabled: true, connectionScope: 'user', oauth: { clientId: 'assistant' }
        }]
      }
    };
  }

  beforeEach(() => {
    original = location.href;
    sessionStorage.removeItem(remembered);
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', mcpConnectMode: 'redirect' }),
        provideNhAssistantMockApi(scenario())
      ]
    });
  });

  afterEach(() => {
    history.replaceState(history.state, '', original);
    sessionStorage.removeItem(remembered);
  });

  function returnWith(fragment: string): void {
    history.replaceState(history.state, '', `${location.pathname}${location.search}#${fragment}`);
  }

  it('remembers the server it leaves for and reports a failed return at that server', async () => {
    const connector = TestBed.inject(NhAssistantMcpConnectService);
    const navigated: string[] = [];
    spyOn(connector, 'navigate').and.callFake((url: string) => navigated.push(url));

    const leaving = connector.connect('calendar').subscribe();
    for (let attempt = 0; attempt < 10 && navigated.length === 0; attempt++) {
      await new Promise(resolve => setTimeout(resolve));
    }
    leaving.unsubscribe();
    expect(sessionStorage.getItem(remembered)).toBe('calendar');

    returnWith('nh-mcp-error=assistant-mcp-authorization-failed&nh-mcp-message-key=nh-assistant.errors.assistant-mcp-authorization-failed');
    const outcome = await firstValueFrom(connector.completeReturn());

    expect(outcome).toEqual({ code: 'assistant-mcp-authorization-failed', serverId: 'calendar' });
    expect(connector.returned()).toEqual(outcome);
    expect(location.hash).toBe('');
    expect(sessionStorage.getItem(remembered)).toBeNull();
  });

  it('reports a completion the server refuses in returned instead of erroring', async () => {
    const connector = TestBed.inject(NhAssistantMcpConnectService);
    returnWith(`nh-mcp-flow=0f0c0000-0000-4000-8000-000000000001&nh-mcp-code=${'a'.repeat(24)}`);

    const outcome = await firstValueFrom(connector.completeReturn());

    expect(outcome).toEqual({ code: 'assistant-mcp-authorization-expired', serverId: null });
    expect(connector.returned()?.code).toBe('assistant-mcp-authorization-expired');
    expect(await firstValueFrom(connector.completeReturn())).toBeNull();
  });
});
