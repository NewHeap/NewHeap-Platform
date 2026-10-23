import { TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { mockStatusOf, mockStatusTable } from '../../../testing/src/nh-assistant-mock-status';
import { NH_ASSISTANT_SERVER_ERROR_CODES } from '../i18n/nh-assistant-error-codes';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';

/**
 * The error contract of the mock API: every failure code has the one HTTP status the server's table
 * gives it, error bodies carry field errors with any code, and the mock refuses what the server
 * refuses: MCP connections a server does not have, refused server fields, too many account ids, a
 * refused run filter before an unknown agent, starts of paused, stopped and rate-limited agents, run
 * cancellation and retries by role, threads opened without a run, and proposals of the whole
 * application for anybody but its approvers.
 */
function scenario(overrides: Partial<NhAssistantMockScenario> = {}): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project' },
      subjects: [
        { id: 'p-1', displayName: 'PRJ-1 Pietje', role: 'approver' },
        { id: 'p-2', displayName: 'PRJ-2 Puk' },
        { id: 'p-3', displayName: 'PRJ-3 Read only', role: 'reader' },
        { id: 'p-4', displayName: 'PRJ-4 Secret', readable: false }
      ]
    }],
    admin: {
      policies: ['app.files'],
      agents: [
        {
          source: 'admin', id: 'projects', displayName: 'Project assistant', description: 'Helps.', instructions: 'Help.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true, viewPolicy: 'app.view'
        },
        {
          source: 'admin', id: 'paused', displayName: 'Paused agent', description: 'Waits.', instructions: 'Wait.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
        },
        {
          source: 'admin', id: 'stopped', displayName: 'Stopped agent', description: 'Stopped.', instructions: 'Stop.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
        },
        {
          source: 'admin', id: 'tasks-only', displayName: 'Task agent', description: 'Tasks.', instructions: 'Tasks.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['task'], worksOnAllSubjects: false, worksOnApplication: false
        }
      ],
      mcpServers: [
        { id: 'files', displayName: 'Files', url: 'https://files.example.com/mcp', authMode: 'bearer', headerName: null, secret: 'secret', requiredPolicy: null, isEnabled: true },
        {
          id: 'crm', displayName: 'CRM', url: 'https://crm.example.com/mcp', authMode: 'oauth', connectionScope: 'organization', headerName: null,
          requiredPolicy: null, isEnabled: true, oauth: { organizationAccountLabel: 'crm-service', confirmAuthorization: true }
        },
        {
          id: 'calendar', displayName: 'Calendar', url: 'https://calendar.example.com/mcp', authMode: 'oauth', connectionScope: 'user', headerName: null,
          requiredPolicy: null, isEnabled: true, oauth: { confirmAuthorization: true }, personalConnected: true
        }
      ],
      accounts: Array.from({ length: 120 }, (_, index) => ({ actorId: `account-${index}`, displayName: `Account ${index}`, detail: null }))
    },
    runNow: { projects: { result: 'Done.', dailyLimit: 2 } },
    callerPolicies: ['app.view'],
    ...overrides
  };
}

async function configure(script = scenario(), administers = true): Promise<NhAssistantMockBackend> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  const backend = TestBed.inject(NhAssistantMockBackend);
  backend.setCanAdminister(administers);
  return backend;
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

/** Puts an agent of the administration in a state, as an administrator does. */
async function setState(agentId: string, state: 'paused' | 'stopped' | 'active'): Promise<void> {
  expect((await call('POST', `admin/agents/${agentId}/state`, { state, reason: null })).status).toBe(200);
}

describe('The status table of the mock API', () => {
  it('answers each code with the one status of the server table', () => {
    const representatives: [string, number][] = [
      ['assistant-validation', 400],
      ['assistant-subject-invalid', 400],
      ['assistant-run-filter-invalid', 400],
      ['assistant-webhook-signature-invalid', 401],
      ['assistant-approval-policy-denied', 403],
      ['assistant-context-unavailable', 403],
      ['assistant-agent-not-found', 404],
      ['assistant-subject-not-found', 404],
      ['assistant-trigger-rate-limited', 409],
      ['assistant-consultation-not-found', 404],
      ['assistant-consultation-in-code', 409],
      ['assistant-consultations-not-allowed', 409],
      ['assistant-mcp-connection-required', 409],
      ['assistant-mcp-connection-not-supported', 409],
      ['assistant-mcp-secret-unreadable', 409],
      ['assistant-webhook-payload-too-large', 413],
      ['assistant-mcp-too-many-authorizations', 429],
      ['assistant-mcp-unreachable', 502],
      ['assistant-trigger-rolled-back', 503],
      ['assistant-mcp-secret-unavailable', 503]
    ];

    expect(representatives.map(([code]) => [code, mockStatusOf(code)])).toEqual(representatives);
    // A code the table does not name answers 400, as on the server.
    expect(mockStatusOf('assistant-application-specific')).toBe(400);
  });

  it('names only codes the library registers and translates in every language', () => {
    const table = [...mockStatusTable().keys()];

    expect(table.filter(code => !NH_ASSISTANT_SERVER_ERROR_CODES.includes(code))).toEqual([]);
    for (const language of ['en', 'nl'] as const) {
      const errors = (NH_ASSISTANT_TRANSLATIONS[language] as { 'nh-assistant': { errors: Record<string, string> } })['nh-assistant'].errors;
      expect(table.filter(code => !errors[code])).withContext(language).toEqual([]);
    }
    // The codes the server added with the shared table are all there.
    expect(table).toEqual(jasmine.arrayContaining([
      'assistant-trigger-rate-limited', 'assistant-trigger-rolled-back', 'assistant-subject-invalid',
      'assistant-webhook-payload-too-large', 'assistant-mcp-connection-not-supported',
      'assistant-mcp-secret-unreadable', 'assistant-mcp-secret-unavailable',
      'assistant-consultation-in-code', 'assistant-consultation-not-found', 'assistant-consultations-not-allowed'
    ]));
    expect(NH_ASSISTANT_SERVER_ERROR_CODES).not.toContain('rate-limited');
  });

  it('answers decisions with the statuses of the server', async () => {
    await configure(scenario({
      proposals: [
        { approvalId: 'denied', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', toolId: 'projects.close', canDecide: false },
        {
          approvalId: 'four-eyes', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', toolId: 'projects.close',
          canDecide: false, reasonCode: 'assistant-four-eyes-required'
        },
        { approvalId: 'closed', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', toolId: 'projects.close', status: 'approved' }
      ]
    }));
    const decide = (approvalId: string, hash = `sha256:${approvalId}`) =>
      call('POST', `approvals/${approvalId}/decisions`, { decision: 'approve', expectedProposalHash: hash });

    expect([(await decide('denied')).status, (await decide('denied')).body.code]).toEqual([403, 'assistant-approval-policy-denied']);
    expect((await decide('four-eyes')).status).toBe(403);
    expect([(await decide('closed')).status, (await decide('closed')).body.code]).toEqual([409, 'assistant-approval-not-pending']);
    expect((await decide('denied', 'sha256:other')).status).toBe(409);
    expect((await decide('unknown')).status).toBe(404);
  });
});

describe('MCP connections of the mock API', () => {
  it('answers the connection codes of a test or sync with their own status', async () => {
    await configure();

    // The organization of an OAuth server has not connected: a conflict, not a remote failure.
    const sync = await call('POST', 'admin/mcp-servers/crm/sync');
    expect([sync.status, sync.body.code]).toEqual([409, 'assistant-mcp-connection-required']);
    await call('PUT', 'admin/mcp-servers/files', {
      id: 'files', displayName: 'Files', url: 'https://unreachable.example.com/mcp', authMode: 'bearer', headerName: null, requiredPolicy: null, isEnabled: true
    });
    const unreachable = await call('POST', 'admin/mcp-servers/files/sync');
    expect([unreachable.status, unreachable.body.code]).toEqual([502, 'assistant-mcp-unreachable']);
  });

  it('refuses connections a server does not have with assistant-mcp-connection-not-supported', async () => {
    await configure();
    const notSupported = [409, 'assistant-mcp-connection-not-supported'];
    const answer = async (method: string, path: string, body?: unknown) => {
      const result = await call(method, path, body);
      return [result.status, result.body?.code];
    };

    // A server with a static secret has no OAuth connection at all.
    expect(await answer('GET', 'admin/mcp-servers/files/connection')).toEqual(notSupported);
    expect(await answer('DELETE', 'admin/mcp-servers/files/connection')).toEqual(notSupported);
    expect(await answer('POST', 'admin/mcp-servers/files/connection/authorize', {})).toEqual(notSupported);
    // Personal connections exist only for a server each person connects to.
    expect(await answer('GET', 'admin/mcp-servers/crm/connections/summary')).toEqual(notSupported);
    expect(await answer('DELETE', 'admin/mcp-servers/crm/connections')).toEqual(notSupported);
    expect(await answer('GET', 'admin/mcp-servers/calendar/connections/summary')).toEqual([200, undefined]);

    // For a server each person connects to, the organization endpoints act on the administrator's own connection.
    expect((await call('GET', 'admin/mcp-servers/calendar/connection')).body.status).toBe('active');
    expect((await call('DELETE', 'admin/mcp-servers/calendar/connection')).status).toBe(204);
    expect((await call('GET', 'admin/mcp-servers/calendar/connection')).body.status).toBe('not-connected');
  });

  it('refuses the personal authorization of a server that is not a personal OAuth server', async () => {
    await configure();
    await call('POST', 'admin/mcp-servers', {
      id: 'planning', displayName: 'Planning', url: 'https://planning.example.com/mcp', authMode: 'forward-user-token',
      headerName: null, requiredPolicy: null, isEnabled: true, headlessUse: 'personal'
    });
    const created = await call('POST', 'admin/agents', {
      id: 'helper', displayName: 'Helper', description: 'Helps.', instructions: 'Help.', toolSelectors: [],
      mcpServerIds: ['files', 'calendar', 'planning'], requiredPolicy: null, autonomy: 'explain'
    });
    expect(created.status).toBe(201);

    // A forwarded sign-in and a static secret have no personal connection to authorize or remove.
    for (const serverId of ['planning', 'files']) {
      const authorize = await call('POST', `mcp-connections/${serverId}/authorize`, {});
      expect([authorize.status, authorize.body.code]).withContext(serverId).toEqual([409, 'assistant-mcp-connection-not-supported']);
      const disconnect = await call('DELETE', `mcp-connections/${serverId}`);
      expect([disconnect.status, disconnect.body.code]).withContext(serverId).toEqual([409, 'assistant-mcp-connection-not-supported']);
    }
    expect((await call('POST', 'mcp-connections/calendar/authorize', {})).status).toBe(200);
    expect((await call('POST', 'mcp-connections/unknown/authorize', {})).status).toBe(404);
  });

  it('reports every refused field of a server at its field name', async () => {
    await configure();
    const refused = await call('POST', 'admin/mcp-servers', {
      id: 'x'.repeat(41),
      displayName: 'N'.repeat(201),
      url: 'ftp://files.example.com',
      authMode: 'api-key',
      headerName: 'X Api Key',
      secret: 's'.repeat(2001),
      requiredPolicy: 'app.unknown',
      clientCertificateReference: 'missing',
      transport: 'carrier-pigeon',
      isEnabled: true
    });

    expect(refused.status).toBe(400);
    expect(refused.body.code).toBe('assistant-validation');
    expect(refused.body.errors).toEqual({
      id: ['too-long'],
      displayName: ['too-long'],
      url: ['invalid'],
      headerName: ['invalid'],
      transport: ['invalid'],
      requiredPolicy: ['not-found'],
      secret: ['too-long'],
      clientCertificateReference: ['not-found']
    });

    const reference = await call('POST', 'admin/mcp-servers', {
      id: 'vault', displayName: 'Vault', url: 'https://vault.example.com/mcp', authMode: 'bearer', headerName: null,
      requiredPolicy: null, isEnabled: true, secretReference: 'vault:token'
    });
    expect(reference.body.errors).toEqual({ secretReference: ['not-found'] });

    const oauth = await call('POST', 'admin/mcp-servers', {
      id: 'erp', displayName: 'ERP', url: 'https://erp.example.com/mcp', authMode: 'oauth-client-credentials', headerName: null,
      requiredPolicy: null, isEnabled: true, oauth: { clientId: 'c'.repeat(513), clientSecretReference: 'vault:erp', confirmAuthorization: false }
    });
    expect(oauth.body.errors).toEqual({
      'oauth.clientId': ['too-long'],
      'oauth.clientSecretReference': ['not-found'],
      'oauth.confirmAuthorization': ['required']
    });
  });
});

describe('Accounts of the mock API', () => {
  it('reads at most 50 ids per request, which getAccounts respects by asking in parts', async () => {
    const backend = await configure();
    const ids = Array.from({ length: 51 }, (_, index) => `account-${index}`);

    const refused = await call('GET', `admin/accounts?ids=${ids.join(',')}`);
    expect([refused.status, refused.body.code, refused.body.errors]).toEqual([400, 'assistant-validation', { ids: ['invalid'] }]);
    expect((await call('GET', `admin/accounts?ids=${'a'.repeat(257)}`)).body.errors).toEqual({ ids: ['invalid'] });

    const wanted = Array.from({ length: 120 }, (_, index) => `account-${index}`);
    const accounts = await firstValueFrom(TestBed.inject(NhAssistantAdminApiService).getAccounts([...wanted, 'account-0', 'unknown-9']));
    const parts = backend.requests.filter(request => request.path === 'admin/accounts');

    expect(accounts.map(account => account.actorId)).toEqual(wanted);
    // The two refused calls above and three parts of at most 50 distinct ids.
    expect(parts.length).toBe(5);
    expect(parts.slice(2).every(request => request.method === 'GET')).toBeTrue();
  });
});

describe('Run histories and runs of the mock API', () => {
  it('reports the refused parts of a run filter at their fields, before an unknown agent', async () => {
    await configure();

    const refused = await call('GET', 'runs?page=0&itemsPerPage=51&agentId=%20&subjectId=%20p-1&status=done&from=2026-10-02&to=2026-10-01');
    expect(refused.status).toBe(400);
    expect(refused.body.code).toBe('assistant-run-filter-invalid');
    expect(refused.body.errors).toEqual({
      page: ['invalid'],
      itemsPerPage: ['invalid'],
      agentId: ['invalid'],
      subjectType: ['required'],
      subjectId: ['invalid'],
      status: ['invalid'],
      to: ['invalid']
    });

    // The administration checks the filter first, then the agent.
    const first = await call('GET', 'admin/agents/unknown/runs?status=done');
    expect([first.status, first.body.code, first.body.errors]).toEqual([400, 'assistant-run-filter-invalid', { status: ['invalid'] }]);
    const then = await call('GET', 'admin/agents/unknown/runs');
    expect([then.status, then.body.code]).toEqual([404, 'assistant-agent-not-found']);
  });

  it('starts a paused agent as waiting and refuses a stopped or rate-limited one with its code', async () => {
    await configure();
    const start = (agentId: string, subjectId = 'p-2', subjectType = 'project') =>
      call('POST', 'threads/start', { agentId, subjectType, subjectId });

    await setState('paused', 'paused');
    const waiting = await start('paused');
    expect(waiting.status).toBe(202);
    expect(waiting.body).toEqual(jasmine.objectContaining({ outcome: 'waiting', runId: null, reasonCode: 'assistant-agent-paused' }));
    expect(waiting.body.conversationId).not.toBeNull();

    await setState('stopped', 'stopped');
    const stopped = await start('stopped');
    expect([stopped.status, stopped.body.code]).toEqual([409, 'assistant-agent-stopped']);

    expect((await start('projects')).body.outcome).toBe('started');
    expect((await start('projects')).body.outcome).toBe('started');
    const limited = await start('projects');
    expect([limited.status, limited.body.code]).toEqual([409, 'assistant-trigger-rate-limited']);

    const malformed = await start('projects', ' p-1');
    expect([malformed.status, malformed.body.code]).toEqual([400, 'assistant-subject-invalid']);
    const reader = await start('projects', 'p-3');
    expect([reader.status, reader.body.code]).toEqual([403, 'assistant-subject-access-denied']);
    const elsewhere = await start('tasks-only');
    expect([elsewhere.status, elsewhere.body.code]).toEqual([409, 'assistant-agent-does-not-work-on']);
  });

  it('answers a deferred start without a run or thread', async () => {
    await configure(scenario({ runNow: { projects: { outcome: 'deferred' } } }));

    const deferred = await call('POST', 'threads/start', { agentId: 'projects', subjectType: 'project', subjectId: 'p-2' });
    expect(deferred.status).toBe(202);
    expect(deferred.body).toEqual({ triggerId: 'manual', outcome: 'deferred', runId: null, conversationId: null, reasonCode: null });
  });

  it('cancels and retries a run with the roles of the server', async () => {
    await configure(scenario({ runNow: { projects: { status: 'queued' } } }));
    const started = (await call('POST', 'threads/start', { agentId: 'projects', subjectType: 'project', subjectId: 'p-1' })).body;
    const onParticipant = (await call('POST', 'threads/start', { agentId: 'projects', subjectType: 'project', subjectId: 'p-2' })).body;

    // A completed run cannot be retried; an open one can be cancelled by a participant.
    expect((await call('POST', `runs/${started.runId}/retry`, { mode: 'resume' })).body.code).toBe('assistant-run-not-retryable');
    expect((await call('POST', `runs/${started.runId}/cancel`)).status).toBe(202);
    const cancelled = (await call('GET', `runs/${started.runId}`)).body.run;
    expect(cancelled.status).toBe('cancelled');

    // Only an approver retries: a participant is refused.
    expect((await call('POST', `runs/${onParticipant.runId}/cancel`)).status).toBe(202);
    const refused = await call('POST', `runs/${onParticipant.runId}/retry`, { mode: 'restart' });
    expect([refused.status, refused.body.code]).toEqual([403, 'assistant-subject-access-denied']);

    const resumed = await call('POST', `runs/${started.runId}/retry`, { mode: 'resume' });
    expect([resumed.status, resumed.body.outcome, resumed.body.runId]).toEqual([202, 'queued', started.runId]);
    await call('POST', `runs/${started.runId}/cancel`);
    const restarted = await call('POST', `runs/${started.runId}/retry`, { mode: 'restart' });
    expect([restarted.status, restarted.body.outcome]).toEqual([202, 'started']);
    expect(restarted.body.runId).not.toBe(started.runId);

    expect((await call('POST', `runs/${started.runId}/retry`, { mode: 'again' })).body.code).toBe('assistant-run-not-retryable');
    expect((await call('POST', 'runs/unknown/cancel')).status).toBe(404);
  });

  it('opens an agent thread without a run for whoever may start the agent', async () => {
    await configure();
    const open = (agentId: string, subjectId: string, subjectType = 'project') =>
      call('POST', 'threads/open', { agentId, subjectType, subjectId });

    const opened = await open('projects', 'p-2');
    expect(opened.status).toBe(200);
    expect(opened.body.thread.conversationId).toBeTruthy();
    expect(opened.body.thread.activeRun).toBeNull();
    expect(opened.body.canStart).toBeTrue();
    expect((await call('GET', `threads/${opened.body.thread.conversationId}/runs`)).body.total).toBe(0);

    // A reader may not create the thread; nothing is created for an agent that does not work on the object.
    expect([(await open('projects', 'p-3')).status, (await open('projects', 'p-3')).body.code]).toEqual([403, 'assistant-subject-access-denied']);
    expect((await open('tasks-only', 'p-2')).body.code).toBe('assistant-agent-does-not-work-on');
    expect((await open('projects', 'p-4')).body.code).toBe('assistant-subject-not-found');
    expect((await open('projects', '')).body.code).toBe('assistant-subject-invalid');
    expect((await open('unknown', 'p-2')).status).toBe(404);
    // Once the thread exists, a reader of the object opens it too.
    expect((await open('projects', 'p-2')).body.thread.conversationId).toBe(opened.body.thread.conversationId);
  });
});

describe('Proposals of the whole application in the mock API', () => {
  const applicationProposal = (agentId: string) => ({
    approvalId: `application-${agentId}`,
    agentId,
    subjectType: 'nh-system',
    subjectId: 'global',
    subjectDisplayName: 'The whole application',
    toolId: 'projects.close'
  });

  it('exist only for approvers of the whole application who pass the agent view policy', async () => {
    await configure(scenario({ proposals: [applicationProposal('projects')] }), false);
    const decide = () => call('POST', 'approvals/application-projects/decisions', {
      decision: 'approve',
      expectedProposalHash: 'sha256:application-projects'
    });

    // Somebody who is no approver of the whole application finds nothing.
    expect((await call('GET', 'approvals/inbox')).body.items).toEqual([]);
    expect((await call('GET', 'approvals/application-projects')).status).toBe(404);
    expect((await call('POST', 'approvals/lookup', { approvalIds: ['application-projects'] })).body.items).toEqual([]);
    const refused = await decide();
    expect([refused.status, refused.body.code]).toEqual([404, 'assistant-approval-not-found']);

    // An approver who passes the agent's view policy sees and decides it.
    TestBed.inject(NhAssistantMockBackend).setCanAdminister(true);
    expect((await call('GET', 'approvals/inbox')).body.items.map((item: { approvalId: string }) => item.approvalId)).toEqual(['application-projects']);
    expect((await decide()).status).toBe(200);
  });

  it('stay hidden from an approver who does not pass the agent view policy', async () => {
    await configure(scenario({ proposals: [applicationProposal('projects')], callerPolicies: [] }), true);

    expect((await call('GET', 'approvals/inbox')).body.total).toBe(0);
    expect((await call('GET', 'approvals/application-projects')).body.code).toBe('assistant-approval-not-found');
  });
});
