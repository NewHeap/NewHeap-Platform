import { Injectable } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { ApprovalPart, AssistantStatus, Conversation, ToolCallPart } from '../models/assistant-api.models';
import { NhAssistantSseEvent } from '../models/assistant-sse.models';
import { NhAssistantAccessPolicy } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiError, NhAssistantApiService } from './nh-assistant-api.service';
import { NhAssistantStore } from './nh-assistant.store';

const status: AssistantStatus = {
  enabled: true,
  agents: [
    { id: 'general', version: 1, displayNameKey: 'agents.general', descriptionKey: 'agents.general-description', canMutate: false },
    { id: 'projects', version: 2, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }
  ],
  limits: { maxMessageChars: 100, maxToolCallsPerTurn: 8 }
};

const approval: ApprovalPart = {
  type: 'approval',
  approvalId: 'approval-1',
  proposalId: 'proposal-1',
  proposalHash: 'hash-1',
  toolId: 'sample-api.project.update-status',
  summary: 'Set project Alpha to On hold',
  argumentsPreview: '{}',
  targets: ['Project Alpha'],
  expiresAt: '2099-01-01T00:00:00Z',
  status: 'pending'
};

const usage = { inputTokens: 1, outputTokens: 1, toolCalls: 1 };

function conversation(agentId = 'projects'): Conversation {
  return {
    id: 'c1',
    agentId,
    agentVersion: 2,
    title: null,
    status: 'idle',
    createdAt: '2026-09-18T10:00:00Z',
    updatedAt: '2026-09-18T10:00:00Z',
    messages: [],
    pendingApproval: null
  };
}

const access = new BehaviorSubject(true);

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

@Injectable()
class TestAccessPolicy implements NhAssistantAccessPolicy {
  canUse(): Observable<boolean> {
    return access;
  }
}

describe('NhAssistantStore', () => {
  let api: jasmine.SpyObj<NhAssistantApiService>;
  let stream: Subject<NhAssistantSseEvent>;

  function setup(): NhAssistantStore {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => 'token',
          accessPolicy: TestAccessPolicy,
          defaultAgentId: 'projects'
        }),
        { provide: NhAssistantApiService, useValue: api }
      ]
    });

    return TestBed.inject(NhAssistantStore);
  }

  async function waitingForApproval(store: NhAssistantStore): Promise<void> {
    await store.initialize();
    const sent = store.send('Pause Alpha');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({
      type: 'tool.started',
      data: { invocationId: 'i1', toolId: approval.toolId, toolVersion: 1, displayName: 'Update status', argumentsPreview: '{}' }
    });
    stream.next({ type: 'approval.required', data: approval });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'waiting-for-approval', usage, errorCode: null } });
    stream.complete();
    await sent;
  }

  beforeEach(() => {
    access.next(true);
    stream = new Subject<NhAssistantSseEvent>();
    api = jasmine.createSpyObj<NhAssistantApiService>('NhAssistantApiService', [
      'status', 'agents', 'listConversations', 'createConversation', 'getConversation',
      'deleteConversation', 'sendMessage', 'decideApproval', 'cancel'
    ]);
    api.status.and.returnValue(of(status));
    api.listConversations.and.returnValue(of({ items: [], total: 0 }));
    api.createConversation.and.callFake(request => of(conversation(request.agentId)));
    api.getConversation.and.returnValue(of(conversation()));
    api.sendMessage.and.callFake(() => stream);
    api.decideApproval.and.callFake(() => stream);
    api.cancel.and.returnValue(of(undefined));
  });

  it('is enabled when the policy allows and the server enables it, and selects the default agent', async () => {
    const store = setup();

    await store.initialize();

    expect(store.enabled()).toBeTrue();
    expect(store.selectedAgentId()).toBe('projects');
    expect(store.limits()?.maxMessageChars).toBe(100);
  });

  it('stays disabled without calling the server when the access policy denies', async () => {
    access.next(false);
    const store = setup();

    await store.initialize();

    expect(store.enabled()).toBeFalse();
    expect(api.status).not.toHaveBeenCalled();
  });

  it('follows later access policy changes', async () => {
    const store = setup();
    await store.initialize();

    access.next(false);

    expect(store.enabled()).toBeFalse();
  });

  it('is disabled when the server reports enabled false or the endpoint fails', async () => {
    api.status.and.returnValue(of({ ...status, enabled: false, agents: [] }));
    const disabled = setup();
    await disabled.initialize();
    expect(disabled.enabled()).toBeFalse();

    TestBed.resetTestingModule();
    api.status.and.returnValue(throwError(() => new NhAssistantApiError(404, 'assistant-not-found', 'k')));
    const failing = setup();
    await failing.initialize();
    expect(failing.enabled()).toBeFalse();
    expect(failing.error()).toBeNull();
  });

  it('creates a conversation, shows the user message optimistically and applies the stream', async () => {
    const store = setup();
    await store.initialize();

    const sent = store.send('  Hello  ');
    await flush();

    const optimistic = store.activeConversation()!;
    expect(api.createConversation).toHaveBeenCalledWith({ agentId: 'projects' });
    expect(optimistic.status).toBe('running');
    expect(optimistic.messages[0].parts).toEqual([{ type: 'text', text: 'Hello' }]);
    expect(store.streaming()).toBeTrue();
    expect(store.canSend()).toBeFalse();

    const request = api.sendMessage.calls.mostRecent().args[1];
    expect(request.text).toBe('Hello');
    expect(request.clientMessageId).toBe(optimistic.messages[0].id);

    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'message.delta', data: { messageId: 'a1', text: 'Hi there' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'completed', usage, errorCode: null } });
    stream.complete();

    expect(await sent).toBeTrue();
    const final = store.activeConversation()!;
    expect(final.status).toBe('idle');
    expect(final.messages.map(message => message.id)).toEqual(['u1', 'a1']);
    expect(final.messages[1].parts).toEqual([{ type: 'text', text: 'Hi there' }]);
    expect(store.streaming()).toBeFalse();
    expect(store.lastUsage()).toEqual(usage);
    expect(store.canSend()).toBeTrue();
  });

  it('removes the optimistic message and restores the draft when the server refuses the turn', async () => {
    const store = setup();
    await store.initialize();

    const sent = store.send('Hello');
    await flush();
    stream.next({ type: 'error', data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' } });
    stream.complete();

    expect(await sent).toBeFalse();
    expect(store.activeConversation()!.messages).toEqual([]);
    expect(store.activeConversation()!.status).toBe('idle');
    expect(store.error()?.code).toBe('assistant-conversation-busy');
    expect(store.consumeRestoredDraft()).toBe('Hello');
    expect(store.restoredDraft()).toBeNull();
  });

  it('rejects messages longer than the server limit', async () => {
    const store = setup();
    await store.initialize();

    expect(await store.send('x'.repeat(101))).toBeFalse();
    expect(api.createConversation).not.toHaveBeenCalled();
  });

  it('pauses for approval and approves with the expected proposal hash exactly once', async () => {
    const store = setup();
    await waitingForApproval(store);

    expect(store.pendingApproval()).toEqual(approval);
    expect(store.canSend()).toBeFalse();

    stream = new Subject<NhAssistantSseEvent>();
    store.decide('approve');
    store.decide('approve');

    expect(api.decideApproval).toHaveBeenCalledOnceWith('c1', 'approval-1', { decision: 'approve', expectedProposalHash: 'hash-1' });
    expect(store.deciding()).toBeTrue();

    stream.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u1', assistantMessageId: 'a1' } });
    const afterDecision = store.activeConversation()!.messages[1].parts;
    expect((afterDecision[0] as ToolCallPart).status).toBe('running');
    expect((afterDecision[1] as ApprovalPart).status).toBe('approved');
    expect(store.pendingApproval()).toBeNull();

    stream.next({ type: 'tool.completed', data: { invocationId: 'i1', status: 'succeeded', resultCode: null, resultPreview: '{}' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't2', status: 'completed', usage, errorCode: null } });
    stream.complete();

    expect(store.deciding()).toBeFalse();
    expect(store.activeConversation()!.status).toBe('idle');
    expect((store.activeConversation()!.messages[1].parts[0] as ToolCallPart).status).toBe('succeeded');
  });

  it('rejects an approval and marks the tool call rejected', async () => {
    const store = setup();
    await waitingForApproval(store);
    stream = new Subject<NhAssistantSseEvent>();

    store.decide('reject', 'Not now');
    stream.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't2', status: 'completed', usage, errorCode: null } });
    stream.complete();

    const parts = store.activeConversation()!.messages[1].parts;
    expect(api.decideApproval.calls.mostRecent().args[2]).toEqual({ decision: 'reject', expectedProposalHash: 'hash-1', reason: 'Not now' });
    expect((parts[0] as ToolCallPart).status).toBe('rejected');
    expect((parts[1] as ApprovalPart).status).toBe('rejected');
  });

  it('keeps the approval pending when the decision is refused', async () => {
    const store = setup();
    await waitingForApproval(store);
    stream = new Subject<NhAssistantSseEvent>();

    store.decide('approve');
    stream.next({ type: 'error', data: { code: 'assistant-approval-stale', messageKey: 'nh-assistant.errors.assistant-approval-stale' } });
    stream.complete();

    expect(store.pendingApproval()?.status).toBe('pending');
    expect(store.error()?.code).toBe('assistant-approval-stale');
    expect(store.deciding()).toBeFalse();
    expect(api.getConversation).toHaveBeenCalledWith('c1');
  });

  it('reports a failed turn with its error code', async () => {
    const store = setup();
    await store.initialize();

    const sent = store.send('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'failed', usage, errorCode: 'assistant-model-unavailable' } });
    stream.complete();
    await sent;

    expect(store.error()).toEqual({ code: 'assistant-model-unavailable', messageKey: 'nh-assistant.errors.assistant-model-unavailable' });
    expect(store.activeConversation()!.status).toBe('failed');
  });

  it('cancels a running turn through the API', async () => {
    const store = setup();
    await store.initialize();
    void store.send('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });

    await store.cancel();
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'cancelled', usage, errorCode: null } });
    stream.complete();

    expect(api.cancel).toHaveBeenCalledWith('c1');
    expect(store.activeConversation()!.status).toBe('idle');
    expect(store.streaming()).toBeFalse();
  });

  it('starts a new conversation when the user switches agents', async () => {
    const store = setup();
    await waitingForApproval(store);

    store.selectAgent('general');

    expect(store.selectedAgentId()).toBe('general');
    expect(store.activeConversation()).toBeNull();
  });
});
