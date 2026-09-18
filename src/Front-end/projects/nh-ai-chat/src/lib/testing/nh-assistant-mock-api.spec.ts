import { TestBed } from '@angular/core/testing';
import {
  NhAssistantApiService,
  NhAssistantSseEvent,
  NhAssistantStore,
  provideNhAssistant
} from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, toArray } from 'rxjs';

/** The data fields of every event as contract section 6 defines them. */
const contractFields: Record<NhAssistantSseEvent['type'], string[]> = {
  'turn.started': ['assistantMessageId', 'turnId', 'userMessageId'],
  'message.delta': ['messageId', 'text'],
  'tool.started': ['argumentsPreview', 'displayName', 'invocationId', 'toolId', 'toolVersion'],
  'tool.completed': ['invocationId', 'resultCode', 'resultPreview', 'status'],
  'approval.required': ['approvalId', 'argumentsPreview', 'expiresAt', 'proposalHash', 'proposalId', 'status', 'summary', 'targets', 'toolId', 'type'],
  'turn.completed': ['errorCode', 'status', 'turnId', 'usage'],
  'error': ['code', 'messageKey']
};

const scenario: NhAssistantMockScenario = {
  agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
  limits: { maxMessageChars: 500 },
  timing: { firstEventDelayMs: 0, eventDelayMs: 0, chunkSize: 5 },
  turns: [
    {
      match: 'pause',
      steps: [
        { text: 'Checking.' },
        {
          approval: {
            toolId: 'sample-api.project.update-status',
            displayName: 'Update project status',
            summary: 'Set Alpha on hold',
            argumentsPreview: '{"status":"on-hold"}',
            targets: ['Project Alpha'],
            resultPreview: '{"status":"on-hold"}',
            approved: [{ text: 'Alpha is on hold.' }],
            rejected: [{ text: 'Nothing changed.' }]
          }
        }
      ]
    },
    { match: /broken/i, steps: [{ error: { code: 'assistant-server', messageKey: 'nh-assistant.errors.assistant-server' } }] },
    { match: 'slow', steps: [{ text: 'one two three four five six seven eight nine ten' }] },
    {
      steps: [
        { tool: { toolId: 'sample-api.project.list', displayName: 'List projects', argumentsPreview: '{}', resultPreview: '[]' } },
        { text: 'There are **no** projects.' }
      ]
    }
  ]
};

describe('provideNhAssistantMockApi', () => {
  let api: NhAssistantApiService;
  let backend: NhAssistantMockBackend;

  function setup(script: NhAssistantMockScenario = scenario): void {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'mock-token' }),
        provideNhAssistantMockApi(script)
      ]
    });
    api = TestBed.inject(NhAssistantApiService);
    backend = TestBed.inject(NhAssistantMockBackend);
  }

  async function newConversation(): Promise<string> {
    return (await firstValueFrom(api.createConversation({ agentId: 'projects' }))).id;
  }

  function expectContractShape(events: NhAssistantSseEvent[]): void {
    for (const event of events) {
      expect(Object.keys(event.data).sort()).withContext(event.type).toEqual(contractFields[event.type]);
    }
  }

  beforeEach(() => setup());

  it('serves status and agents with the scripted limits', async () => {
    const status = await firstValueFrom(api.status());

    expect(status.enabled).toBeTrue();
    expect(status.agents.map(agent => agent.id)).toEqual(['projects']);
    expect(status.limits.maxMessageChars).toBe(500);
    expect(await firstValueFrom(api.agents())).toEqual(scenario.agents);
  });

  it('streams a turn with contract events through a real event stream', async () => {
    const id = await newConversation();

    const events = await firstValueFrom(api.sendMessage(id, { text: 'Show projects', clientMessageId: 'client-1' }).pipe(toArray()));

    expect(events.map(event => event.type)).toEqual([
      'turn.started', 'tool.started', 'tool.completed',
      'message.delta', 'message.delta', 'message.delta', 'message.delta', 'turn.completed'
    ]);
    expectContractShape(events);
    expect(backend.requests.find(request => request.path.endsWith('/messages'))?.headers['Authorization']).toBe('Bearer mock-token');

    const stored = await firstValueFrom(api.getConversation(id));
    expect(stored.status).toBe('idle');
    expect(stored.title).toBe('Show projects');
    expect(stored.messages.map(message => message.role)).toEqual(['user', 'assistant']);
  });

  it('pauses for approval and resumes on approve', async () => {
    const id = await newConversation();

    const first = await firstValueFrom(api.sendMessage(id, { text: 'Pause Alpha', clientMessageId: 'c' }).pipe(toArray()));
    const approval = first.find(event => event.type === 'approval.required')!.data as { approvalId: string; proposalHash: string };
    const completed = first[first.length - 1];

    expectContractShape(first);
    expect(completed).toEqual(jasmine.objectContaining({ type: 'turn.completed' }));
    expect((completed.data as { status: string }).status).toBe('waiting-for-approval');
    expect((await firstValueFrom(api.getConversation(id))).pendingApproval?.approvalId).toBe(approval.approvalId);

    const resumed = await firstValueFrom(api.decideApproval(id, approval.approvalId, {
      decision: 'approve',
      expectedProposalHash: approval.proposalHash
    }).pipe(toArray()));

    expectContractShape(resumed);
    expect(resumed.map(event => event.type)).toEqual([
      'turn.started', 'tool.completed', 'message.delta', 'message.delta', 'message.delta', 'message.delta', 'turn.completed'
    ]);
    const stored = await firstValueFrom(api.getConversation(id));
    expect(stored.status).toBe('idle');
    expect(stored.pendingApproval).toBeNull();
  });

  it('refuses a decision with a stale proposal hash', async () => {
    const id = await newConversation();
    const first = await firstValueFrom(api.sendMessage(id, { text: 'Pause Alpha', clientMessageId: 'c' }).pipe(toArray()));
    const approval = first.find(event => event.type === 'approval.required')!.data as { approvalId: string };

    const events = await firstValueFrom(api.decideApproval(id, approval.approvalId, {
      decision: 'approve',
      expectedProposalHash: 'sha256:stale'
    }).pipe(toArray()));

    expect(events).toEqual([{ type: 'error', data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' } }]);
  });

  it('ends a scripted failure with an error event and keeps the conversation usable', async () => {
    const id = await newConversation();

    const events = await firstValueFrom(api.sendMessage(id, { text: 'Broken request', clientMessageId: 'c' }).pipe(toArray()));

    expect(events.map(event => event.type)).toEqual(['turn.started', 'error']);
    expect((await firstValueFrom(api.getConversation(id))).status).toBe('idle');
  });

  it('cancels a running turn with turn.completed cancelled', async () => {
    TestBed.resetTestingModule();
    setup({ ...scenario, timing: { firstEventDelayMs: 0, eventDelayMs: 20, chunkSize: 7 } });
    const id = await newConversation();

    const collected = firstValueFrom(api.sendMessage(id, { text: 'slow answer', clientMessageId: 'c' }).pipe(toArray()));
    await new Promise(resolve => setTimeout(resolve, 70));
    await firstValueFrom(api.cancel(id), { defaultValue: undefined });
    const events = await collected;

    const last = events[events.length - 1];
    expect(last.type).toBe('turn.completed');
    expect((last.data as { status: string }).status).toBe('cancelled');
    expect(events.filter(event => event.type === 'message.delta').length).toBeLessThan(20);
  });

  it('answers 404 except for status while the feature flag is off', async () => {
    backend.setEnabled(false);

    const status = await firstValueFrom(api.status());
    const error = await firstValueFrom(api.listConversations()).catch(caught => caught as { code: string });

    expect(status).toEqual(jasmine.objectContaining({ enabled: false, agents: [] }));
    expect((error as { code: string }).code).toBe('assistant-not-found');
  });

  it('drives the store end to end: send, approve and reject', async () => {
    const store = TestBed.inject(NhAssistantStore);
    await store.initialize();

    await store.send('Pause Alpha');
    await waitFor(() => store.pendingApproval() !== null);
    store.decide('reject');
    await waitFor(() => !store.streaming());

    const parts = store.activeConversation()!.messages[1].parts;
    expect(parts.map(part => part.type)).toEqual(['text', 'tool-call', 'approval', 'text']);
    expect(parts[2]).toEqual(jasmine.objectContaining({ status: 'rejected' }));
    expect(store.activeConversation()!.status).toBe('idle');
  });
});

async function waitFor(condition: () => boolean, timeoutMs = 2000): Promise<void> {
  const start = Date.now();
  while (!condition()) {
    if (Date.now() - start > timeoutMs) {
      throw new Error('Condition not met in time.');
    }
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}
