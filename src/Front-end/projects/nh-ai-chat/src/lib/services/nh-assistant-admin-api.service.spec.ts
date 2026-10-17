import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { NH_ASSISTANT_FETCH, NhAssistantFetch } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from './nh-assistant-admin-api.service';
import { NhAssistantApiError, NhAssistantApiService } from './nh-assistant-api.service';

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
}

describe('NhAssistantAdminApiService', () => {
  let fetchSpy: jasmine.Spy<NhAssistantFetch>;
  let admin: NhAssistantAdminApiService;
  let api: NhAssistantApiService;

  beforeEach(() => {
    fetchSpy = jasmine.createSpy<NhAssistantFetch>('fetch');
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'admin-token' }),
        { provide: NH_ASSISTANT_FETCH, useFactory: () => fetchSpy }
      ]
    });
    admin = TestBed.inject(NhAssistantAdminApiService);
    api = TestBed.inject(NhAssistantApiService);
  });

  function lastCall(): { url: string; method: string; headers: Record<string, string>; body: unknown } {
    const [url, init] = fetchSpy.calls.mostRecent().args;
    return {
      url,
      method: init.method ?? 'GET',
      headers: init.headers as Record<string, string>,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined
    };
  }

  it('calls every admin endpoint with the documented method and path', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({})));
    const agent = {
      id: 'agent-1', displayName: 'Agent', description: '', instructions: '', toolSelectors: [], mcpServerIds: [],
      requiredPolicy: null, autonomy: 'explain' as const, isEnabled: true
    };
    const server = { id: 'files', displayName: 'Files', url: 'https://mcp.example', authMode: 'none' as const, headerName: null, requiredPolicy: null, isEnabled: true };
    const calls: [() => Promise<unknown>, string, string][] = [
      [() => firstValueFrom(admin.getContext()), 'GET', 'admin/context'],
      [() => firstValueFrom(admin.updateContext({ text: 'x', expectedVersion: 3 })), 'PUT', 'admin/context'],
      [() => firstValueFrom(admin.getContextVersions()), 'GET', 'admin/context/versions'],
      [() => firstValueFrom(admin.getTools()), 'GET', 'admin/tools'],
      [() => firstValueFrom(admin.getAgents()), 'GET', 'admin/agents'],
      [() => firstValueFrom(admin.createAgent(agent)), 'POST', 'admin/agents'],
      [() => firstValueFrom(admin.updateAgent('agent 1', { ...agent, expectedVersion: 2 })), 'PUT', 'admin/agents/agent%201'],
      [() => firstValueFrom(admin.resetAgent('agent-1')), 'POST', 'admin/agents/agent-1/reset'],
      [() => firstValueFrom(admin.getAgentState('agent 1')), 'GET', 'admin/agents/agent%201/state'],
      [() => firstValueFrom(admin.setAgentState('agent-1', { state: 'paused', reason: 'Import' })), 'POST', 'admin/agents/agent-1/state'],
      [() => firstValueFrom(admin.setSkillState('agent-1', 'deadline-risk', { state: 'stopped' })), 'POST', 'admin/agents/agent-1/skills/deadline-risk/state'],
      [() => firstValueFrom(admin.setAgentShadow('agent 1', { mode: 'shadow', reason: 'Evaluate' })), 'POST', 'admin/agents/agent%201/shadow'],
      [() => firstValueFrom(admin.setSkillShadow('agent-1', 'deadline-risk', { mode: 'code' })), 'POST', 'admin/agents/agent-1/skills/deadline-risk/shadow'],
      [() => firstValueFrom(admin.setServiceAccount('agent-1', { actorId: 'service-2' })), 'POST', 'admin/agents/agent-1/service-account'],
      [() => firstValueFrom(admin.deleteAgent('agent-1'), { defaultValue: undefined }), 'DELETE', 'admin/agents/agent-1'],
      [() => firstValueFrom(admin.getMcpServers()), 'GET', 'admin/mcp-servers'],
      [() => firstValueFrom(admin.createMcpServer(server)), 'POST', 'admin/mcp-servers'],
      [() => firstValueFrom(admin.updateMcpServer('files', server)), 'PUT', 'admin/mcp-servers/files'],
      [() => firstValueFrom(admin.deleteMcpServer('files'), { defaultValue: undefined }), 'DELETE', 'admin/mcp-servers/files'],
      [() => firstValueFrom(admin.testMcpServer('files')), 'POST', 'admin/mcp-servers/files/test'],
      [() => firstValueFrom(admin.syncMcpServer('files')), 'POST', 'admin/mcp-servers/files/sync'],
      [() => firstValueFrom(admin.getMcpTools('files')), 'GET', 'admin/mcp-servers/files/tools'],
      [() => firstValueFrom(admin.updateMcpTool('files', 'read/file', { isEnabled: true, effect: 'read-only', descriptionOverride: null })),
        'PUT', 'admin/mcp-servers/files/tools/read%2Ffile'],
      [() => firstValueFrom(api.getPreferences()), 'GET', 'preferences'],
      [() => firstValueFrom(api.updatePreferences({ style: 'direct', addressForm: 'formal', responseLength: 'short', customInstructions: null })),
        'PUT', 'preferences']
    ];

    for (const [call, method, path] of calls) {
      await call();
      const request = lastCall();
      expect(request.method).withContext(path).toBe(method);
      expect(request.url).withContext(path).toBe(`/api/assistant/${path}`);
      expect(request.headers['Authorization']).withContext(path).toBe('Bearer admin-token');
    }
  });

  it('reads the feedback report of an agent with the filters as query parameters', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({})));
    const period = { from: '2026-09-13T00:00:00.000Z', to: '2026-10-13T00:00:00.000Z' };

    await firstValueFrom(admin.getAgentFeedbackSummary('intake reviewer'));
    expect(lastCall().url).toBe('/api/assistant/admin/agents/intake%20reviewer/feedback/summary');

    await firstValueFrom(admin.getAgentFeedbackSummary('intake', { ...period, toolId: 'projects.change-status', agentVersion: 0 }));
    expect(lastCall().method).toBe('GET');
    expect(lastCall().url).toBe('/api/assistant/admin/agents/intake/feedback/summary'
      + '?from=2026-09-13T00%3A00%3A00.000Z&to=2026-10-13T00%3A00%3A00.000Z&toolId=projects.change-status&agentVersion=0');

    await firstValueFrom(admin.getAgentFeedback('intake', {
      ...period, scopeKey: 'division:1', toolId: undefined, category: 'not-now', page: 2, itemsPerPage: 50
    }));
    expect(lastCall().url).toBe('/api/assistant/admin/agents/intake/feedback'
      + '?from=2026-09-13T00%3A00%3A00.000Z&to=2026-10-13T00%3A00%3A00.000Z&scopeKey=division%3A1&category=not-now&page=2&itemsPerPage=50');
    expect(lastCall().headers['Authorization']).toBe('Bearer admin-token');
  });

  it('downloads the feedback export as JSON Lines', async () => {
    fetchSpy.and.returnValue(Promise.resolve(new Response('{"feedback":{}}\n', {
      status: 200,
      headers: { 'content-type': 'application/x-ndjson' }
    })));

    const blob = await admin.exportAgentFeedback('intake', { category: 'wrong-value', limit: 500 });

    expect(lastCall().method).toBe('GET');
    expect(lastCall().url).toBe('/api/assistant/admin/agents/intake/feedback/export?category=wrong-value&limit=500');
    expect(lastCall().headers['Accept']).toBe('application/x-ndjson');
    expect(lastCall().headers['Authorization']).toBe('Bearer admin-token');
    expect(await blob.text()).toBe('{"feedback":{}}\n');
  });

  it('rejects a refused export with a stable code instead of response text', async () => {
    let calls = 0;
    fetchSpy.and.callFake(() => ++calls === 1
      ? Promise.resolve(jsonResponse({ code: 'assistant-forbidden', messageKey: 'nh-assistant.errors.assistant-forbidden' }, 403))
      : Promise.reject(new TypeError('Failed to fetch')));

    const refused = await admin.exportAgentFeedback('intake').catch((error: unknown) => error);
    const offline = await admin.exportAgentFeedback('intake').catch((error: unknown) => error);

    expect(refused).toEqual(jasmine.any(NhAssistantApiError));
    expect((refused as NhAssistantApiError).code).toBe('assistant-forbidden');
    expect((offline as NhAssistantApiError).status).toBe(0);
    expect((offline as NhAssistantApiError).messageKey).toBe(`nh-assistant.errors.${(offline as NhAssistantApiError).code}`);
  });

  it('omits an unchanged secret and sends an empty string to clear it', async () => {
    fetchSpy.and.callFake(() => Promise.resolve(jsonResponse({})));
    const server = { id: 'files', displayName: 'Files', url: 'https://mcp.example', authMode: 'bearer' as const, headerName: null, requiredPolicy: null, isEnabled: true };

    await firstValueFrom(admin.updateMcpServer('files', server));
    expect(Object.keys(lastCall().body as object)).not.toContain('secret');

    await firstValueFrom(admin.updateMcpServer('files', { ...server, secret: '' }));
    expect((lastCall().body as { secret: string }).secret).toBe('');
  });

  it('maps an admin 409 to a version conflict', async () => {
    fetchSpy.and.returnValue(Promise.resolve(new Response(null, { status: 409 })));

    const error = await firstValueFrom(admin.updateContext({ text: 'x', expectedVersion: 1 })).catch(caught => caught as NhAssistantApiError);

    expect((error as NhAssistantApiError).code).toBe('assistant-version-conflict');
    expect((error as NhAssistantApiError).messageKey).toBe('nh-assistant.errors.assistant-version-conflict');
  });

  it('prefers a safe code from the error body and ignores unsafe text', async () => {
    fetchSpy.and.returnValues(
      Promise.resolve(jsonResponse({ code: 'assistant-mcp-unreachable' }, 502)),
      Promise.resolve(jsonResponse({ code: '<b>Stack trace at Foo.Bar()</b>' }, 400))
    );

    const first = await firstValueFrom(admin.syncMcpServer('files')).catch(caught => caught as NhAssistantApiError);
    const second = await firstValueFrom(admin.createAgent({} as never)).catch(caught => caught as NhAssistantApiError);

    expect((first as NhAssistantApiError).code).toBe('assistant-mcp-unreachable');
    expect((second as NhAssistantApiError).code).toBe('assistant-validation');
  });
});
