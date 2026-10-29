import { Injectable, Provider, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, NEVER, Observable, Subject, of, throwError } from 'rxjs';
import { AgentProposal } from '../models/assistant-agent-run.models';
import { ApprovalPart, AssistantStatus, Conversation, ConversationThreadDecision, Message, ToolCallPart } from '../models/assistant-api.models';
import { LiveAgentWorkChanged, LiveConversationEvent } from '../models/assistant-live.models';
import { NhAssistantMessageEvent, NhAssistantSseEvent } from '../models/assistant-sse.models';
import { NhAssistantAccessPolicy } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { NhAssistantApiError, NhAssistantApiService } from './nh-assistant-api.service';
import { NhAssistantLiveService, NhAssistantLiveState } from './nh-assistant-live.service';
import { NH_ASSISTANT_MESSAGE_QUEUED_NOTICE_CODE, NhAssistantStore } from './nh-assistant.store';

const status: AssistantStatus = {
  enabled: true,
  agents: [
    { id: 'general', version: 1, displayNameKey: 'agents.general', descriptionKey: 'agents.general-description', canMutate: false },
    { id: 'projects', version: 2, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }
  ],
  limits: { maxMessageChars: 100, maxToolCallsPerTurn: 8 },
  canAdminister: false
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

function wait(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

/** A connected hub whose agent-work updates and conversation events the test sends. */
class FakeLiveService {
  readonly agentWork = new Subject<LiveAgentWorkChanged>();
  readonly events = new Subject<LiveConversationEvent>();
  readonly changed$ = NEVER;
  readonly read$ = NEVER;
  readonly removed$ = NEVER;
  readonly events$ = this.events.asObservable();
  readonly resync$ = NEVER;
  readonly agentWork$ = this.agentWork.asObservable();
  readonly state = signal<NhAssistantLiveState>('connected').asReadonly();

  connect(): Promise<void> {
    return Promise.resolve();
  }

  stop(): Promise<void> {
    return Promise.resolve();
  }
}

@Injectable()
class TestAccessPolicy implements NhAssistantAccessPolicy {
  canUse(): Observable<boolean> {
    return access;
  }
}

describe('NhAssistantStore', () => {
  let api: jasmine.SpyObj<NhAssistantApiService>;
  let agentRuns: jasmine.SpyObj<NhAssistantAgentRunApiService>;
  let stream: Subject<NhAssistantSseEvent>;

  function setup(
    getStateScope?: () => string | null,
    getPageContext?: () => { route: string },
    providers: Provider[] = []
  ): NhAssistantStore {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => 'token',
          accessPolicy: TestAccessPolicy,
          defaultAgentId: 'projects',
          getStateScope,
          getPageContext
        }),
        { provide: NhAssistantApiService, useValue: api },
        { provide: NhAssistantAgentRunApiService, useValue: agentRuns },
        ...providers
      ]
    });

    return TestBed.inject(NhAssistantStore);
  }

  async function waitingForApproval(store: NhAssistantStore, part: ApprovalPart = approval): Promise<void> {
    await store.initialize();
    const sent = store.send('Pause Alpha');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({
      type: 'tool.started',
      data: { invocationId: 'i1', toolId: approval.toolId, toolVersion: 1, displayName: 'Update status', argumentsPreview: '{}' }
    });
    stream.next({ type: 'approval.required', data: part });
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
    agentRuns = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', ['decide']);
  });

  async function processQueue(): Promise<void> {
    TestBed.flushEffects();
    await flush();
    TestBed.flushEffects();
    await flush();
  }

  async function startTurn(store: NhAssistantStore): Promise<void> {
    await store.initialize();
    const sent = store.submit('First');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    expect(await sent).toBeTrue();
  }

  function finishTurn(subject: Subject<NhAssistantSseEvent>, cancelled = false): void {
    subject.next({ type: 'turn.completed', data: { turnId: 't1', status: cancelled ? 'cancelled' : 'completed', usage, errorCode: null } });
    subject.complete();
  }

  it('dispatches queued messages in FIFO order only after the current stream closes', async () => {
    const store = setup();
    await startTurn(store);
    await store.submit('Second');
    await store.submit('Third');
    await processQueue();
    expect(store.queuedMessages().map(item => item.text)).toEqual(['Second', 'Third']);
    expect(api.sendMessage).toHaveBeenCalledTimes(1);

    const second = new Subject<NhAssistantSseEvent>();
    const third = new Subject<NhAssistantSseEvent>();
    api.sendMessage.and.returnValues(second, third);
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'completed', usage, errorCode: null } });
    await processQueue();
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
    stream.complete();
    await processQueue();
    expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('Second');
    expect(store.queuedMessages()[0].state).toBe('sending');
    second.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u2', assistantMessageId: 'a2' } });
    await processQueue();
    expect(store.queuedMessages().map(item => item.text)).toEqual(['Third']);
    finishTurn(second);
    await processQueue();
    expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('Third');
    expect(api.sendMessage).toHaveBeenCalledTimes(3);
  });

  it('shows a message that waits for the next run of the agent, with its files, as queued', async () => {
    const store = setup();
    await store.initialize();
    const files = [{ id: 'plan', name: 'Plan.pdf', mediaType: 'application/pdf', size: 10, hash: null }];
    // A run of the agent works in the thread: the server reads the thread as running.
    api.getConversation.and.returnValue(of({ ...conversation(), status: 'running' }));
    const answer = new Subject<NhAssistantMessageEvent>();
    api.sendMessage.and.returnValue(answer);

    const sent = store.send('Read the plan.', files);
    await flush();
    expect(store.activeConversation()!.messages.length).toBe(1);
    answer.next({ type: 'message.queued', data: { triggerId: 'queued-message', outcome: 'coalesced', runId: 'r1', conversationId: 'c1', reasonCode: null } });
    answer.complete();

    expect(await sent).toBeTrue();
    await flush();
    await flush();
    // The message waits on the server with its files; the run adds it to the thread, so it is not
    // shown twice. The notice says it waits, and the thread follows the server's state.
    expect(api.sendMessage.calls.mostRecent().args[1].files).toEqual(files);
    expect(store.notice()).toEqual({ code: NH_ASSISTANT_MESSAGE_QUEUED_NOTICE_CODE, messageKey: 'nh-assistant.notices.message-queued-with-files' });
    expect(store.error()).toBeNull();
    expect(store.activeConversation()!.messages).toEqual([]);
    expect(store.activeConversation()!.status).toBe('running');
    expect(store.queuedMessages()).toEqual([]);
  });

  it('keeps the original conversation and captured page context when the user switches away', async () => {
    let route = '/projects/alpha';
    const store = setup(undefined, () => ({ route }));
    await startTurn(store);
    await store.submit('Continue Alpha');
    route = '/projects/beta';
    store.startNewConversation();
    api.sendMessage.and.returnValue(new Subject<NhAssistantSseEvent>());
    finishTurn(stream);
    await processQueue();
    const [conversationId, request] = api.sendMessage.calls.mostRecent().args;
    expect(conversationId).toBe('c1');
    expect(request.clientContext?.route).toBe('/projects/alpha');
    expect(store.activeConversation()).toBeNull();
    expect(store.queuedMessages()).toEqual([]);
  });

  it('lets an existing conversation queue a second message while the first request is preparing', async () => {
    const store = setup();
    await store.initialize();
    await store.openConversation('c1');
    const first = store.submit('First');
    await store.submit('Second');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    await first;
    await processQueue();
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
    expect(store.queuedMessages().map(item => item.text)).toEqual(['Second']);
  });

  it('pauses a queue on Stop and resumes it only on an explicit action', async () => {
    const store = setup();
    await startTurn(store);
    await store.submit('Next');
    await store.cancel();
    api.sendMessage.and.returnValue(new Subject<NhAssistantSseEvent>());
    finishTurn(stream, true);
    await processQueue();
    expect(store.queuePaused()).toBeTrue();
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
    store.resumeQueue();
    await processQueue();
    expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('Next');
  });

  it('prioritizes steering but waits for confirmed cancellation and retains other queued messages', async () => {
    const store = setup();
    await startTurn(store);
    await store.submit('Later');
    await store.submit('Correction');
    const correction = store.queuedMessages()[1];
    await store.steerQueuedMessage(correction.id);
    await store.steerQueuedMessage(correction.id);
    await processQueue();
    expect(api.cancel).toHaveBeenCalledOnceWith('c1');
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
    const replacement = new Subject<NhAssistantSseEvent>();
    api.sendMessage.and.returnValue(replacement);
    finishTurn(stream, true);
    await processQueue();
    expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('Correction');
    replacement.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u2', assistantMessageId: 'a2' } });
    await processQueue();
    expect(store.queuedMessages().map(item => item.text)).toEqual(['Later']);
  });

  it('preserves the queue and running stream when steering cancellation fails', async () => {
    const store = setup();
    await startTurn(store);
    await store.submit('Correction');
    api.cancel.and.returnValue(throwError(() => new NhAssistantApiError(403, 'assistant-forbidden', 'nh-assistant.errors.assistant-forbidden')));
    await store.steerQueuedMessage(store.queuedMessages()[0].id);
    await processQueue();
    expect(store.streaming()).toBeTrue();
    expect(store.queuedMessages()[0].text).toBe('Correction');
    expect(store.queuePaused()).toBeTrue();
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
  });

  it('does not let a participant steer another person even when their member details are unavailable', async () => {
    const store = setup();
    await store.initialize();
    api.getConversation.and.returnValue(of({ ...conversation(), status: 'running', role: 'participant',
      activeActorId: 'colleague', currentActorId: 'self', members: [] }));
    await store.openConversation('c1');
    await store.submit('Correction');
    expect(store.canSteer()).toBeFalse();
    await store.steerQueuedMessage(store.queuedMessages()[0].id);
    expect(api.cancel).not.toHaveBeenCalled();
  });

  it('holds queued messages behind an approval without approving it automatically', async () => {
    const store = setup();
    await waitingForApproval(store);
    await store.submit('Next question');
    await processQueue();
    expect(store.queuedMessages().length).toBe(1);
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
    expect(api.decideApproval).not.toHaveBeenCalled();
    expect(store.pendingApproval()?.proposalHash).toBe('hash-1');
  });

  it('keeps a refused queued message for explicit retry without restoring over a fresh draft', async () => {
    const store = setup();
    await startTurn(store);
    await store.submit('Next');
    const refused = new Subject<NhAssistantSseEvent>();
    api.sendMessage.and.returnValue(refused);
    finishTurn(stream);
    await processQueue();
    refused.next({ type: 'error', data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' } });
    refused.complete();
    await processQueue();
    expect(store.queuedMessages()[0].state).toBe('failed');
    expect(store.restoredDraft()).toBeNull();
    expect(store.queuePaused()).toBeTrue();
    const calls = api.sendMessage.calls.count();
    await processQueue();
    expect(api.sendMessage.calls.count()).toBe(calls);
    api.sendMessage.and.returnValue(new Subject<NhAssistantSseEvent>());
    store.resumeQueue();
    await processQueue();
    expect(api.sendMessage.calls.count()).toBe(calls + 1);
  });

  it('edits and removes unsent messages, validates length, and forgets them on account change', async () => {
    const store = setup();
    await startTurn(store);
    await store.submit('Second');
    await store.submit('Third');
    const [second, third] = store.queuedMessages();
    expect(store.updateQueuedMessage(second.id, '')).toBeFalse();
    expect(store.updateQueuedMessage(second.id, 'x'.repeat(101))).toBeFalse();
    expect(store.updateQueuedMessage(second.id, '  Edited  ')).toBeTrue();
    store.removeQueuedMessage(third.id);
    expect(store.queuedMessages().map(item => item.text)).toEqual(['Edited']);
    access.next(false);
    await processQueue();
    expect(store.queuedMessages()).toEqual([]);
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

  it('keeps a refused message for the composer unless a draft replaces it', async () => {
    const store = setup();
    await store.initialize();
    const sent = store.send('Hello');
    await flush();
    stream.next({ type: 'error', data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' } });
    stream.complete();
    await sent;

    expect(store.setDraft('A prepared brief')).toBeFalse();
    expect(store.pendingDraft()).toBe('Hello');

    expect(store.setDraft('A prepared brief', { replace: true })).toBeTrue();
    expect(store.pendingDraft()).toEqual({ text: 'A prepared brief', replace: true });
    // `restoredDraft` keeps naming only a refused message, so a custom composer bound to it never
    // takes a draft as the person's own text.
    expect(store.restoredDraft()).toBeNull();
    expect(store.consumeRestoredDraft()).toBe('A prepared brief');
    expect(store.pendingDraft()).toBeNull();
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
  });

  it('puts no draft while the assistant is not available or for empty text', async () => {
    const store = setup();

    expect(store.setDraft('Too early')).toBeFalse();
    await store.initialize();
    expect(store.setDraft('   ')).toBeFalse();
    expect(store.pendingDraft()).toBeNull();

    access.next(false);
    await flush();
    expect(store.setDraft('No access')).toBeFalse();
    expect(store.pendingDraft()).toBeNull();
  });

  it('caps a draft at the server limit without splitting a character', async () => {
    const store = setup();
    await store.initialize();

    // The limit is 100 characters; the emoji takes the 100th and 101st UTF-16 code units.
    expect(store.setDraft(`${'A'.repeat(99)}\u{1F680} launch`)).toBeTrue();

    expect(store.pendingDraft()).toEqual({ text: 'A'.repeat(99), replace: false });
  });

  /** FB44: the thread is in view while any surface shows it, whichever reported last. */
  describe('the surfaces that show the thread', () => {
    it('keeps the thread in view while any registered surface shows it', () => {
      const store = setup();
      const page = store.registerThreadSurface();
      const drawer = store.registerThreadSurface();
      expect(store.threadInView()).toBeFalse();

      page.setShowing(true);
      // A drawer that opens and closes over the page leaves the page's thread in view.
      drawer.setShowing(true);
      drawer.setShowing(false);
      expect(store.threadInView()).toBeTrue();

      page.setShowing(false);
      expect(store.threadInView()).toBeFalse();
    });

    it('stops counting a surface once it unregisters, also when it reports again afterwards', () => {
      const store = setup();
      const page = store.registerThreadSurface();
      page.setShowing(true);

      page.unregister();
      expect(store.threadInView()).toBeFalse();

      page.setShowing(true);
      page.unregister();
      expect(store.threadInView()).toBeFalse();
    });

    it('counts the host\'s setViewing as one more surface, which never hides what a panel shows', () => {
      const store = setup();
      const panel = store.registerThreadSurface();

      store.setViewing(true);
      expect(store.threadInView()).toBeTrue();

      panel.setShowing(true);
      store.setViewing(false);
      expect(store.threadInView()).toBeTrue();

      panel.setShowing(false);
      expect(store.threadInView()).toBeFalse();
    });
  });

  /** FB41: what the composer holds and has not sent stays with its conversation, in this tab's memory. */
  describe('the unsent message', () => {
    function browserStorage(): string {
      const values: string[] = [];
      for (const storage of [localStorage, sessionStorage]) {
        for (let index = 0; index < storage.length; index++) {
          values.push(storage.getItem(storage.key(index)!) ?? '');
        }
      }
      return values.join('\n');
    }

    beforeEach(() => {
      api.getConversation.and.callFake(id => of({ ...conversation(), id }));
      api.deleteConversation.and.returnValue(of(undefined));
    });

    it('keeps it per conversation in memory only and shows it again when the conversation opens', async () => {
      const store = setup(() => `store-unsent-${crypto.randomUUID()}`);
      await store.initialize();
      await store.openConversation('c1');
      store.keepUnsentMessage({ text: 'Half a question about Alpha ', prepared: false });

      await store.openConversation('c2');
      expect(store.unsentMessage()).toBeNull();
      store.keepUnsentMessage({ text: 'Budget?', prepared: false });
      store.startNewConversation();
      expect(store.unsentMessage()).toBeNull();

      await store.openConversation('c1');
      // Kept as the composer held it, the trailing space included.
      expect(store.unsentMessage()).toEqual({ text: 'Half a question about Alpha ', prepared: false });
      await store.openConversation('c2');
      expect(store.unsentMessage()).toEqual({ text: 'Budget?', prepared: false });
      store.keepUnsentMessage({ text: '', prepared: false });
      expect(store.unsentMessage()).toBeNull();

      expect(browserStorage()).not.toContain('Half a question');
      expect(browserStorage()).not.toContain('Budget?');
    });

    it('takes the new chat\'s text into the conversation created for it, also when a host sends', async () => {
      const store = setup();
      await store.initialize();
      store.keepUnsentMessage({ text: 'Still typing', prepared: false });

      // A host's quick action sends its own text; what the person typed stays theirs.
      const sent = store.send('Plan the kickoff');
      await flush();

      expect(store.activeConversation()?.id).toBe('c1');
      expect(store.unsentMessage()).toEqual({ text: 'Still typing', prepared: false });
      store.startNewConversation();
      expect(store.unsentMessage()).toBeNull();
      stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
      stream.complete();
      expect(await sent).toBeTrue();
    });

    it('keeps a refused message as the person\'s own text of its conversation, also after they opened another one', async () => {
      const store = setup();
      await store.initialize();
      await store.openConversation('c1');
      const sent = store.send('Hello');
      await flush();

      await store.openConversation('c2');
      stream.next({ type: 'error', data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' } });
      stream.complete();
      expect(await sent).toBeFalse();

      // Nothing lands in the composer of the conversation the person looks at now.
      expect(store.pendingDraft()).toBeNull();
      expect(store.unsentMessage()).toBeNull();
      await store.openConversation('c1');
      expect(store.unsentMessage()).toEqual({ text: 'Hello', prepared: false });
      expect(store.setDraft('A prepared brief')).toBeFalse();
    });

    it('lets a draft in only where the person\'s own text does not keep its place', async () => {
      const store = setup();
      await store.initialize();
      store.keepUnsentMessage({ text: 'My own question', prepared: false });

      expect(store.setDraft('A brief')).toBeFalse();
      expect(store.pendingDraft()).toBeNull();
      expect(store.setDraft('A brief', { replace: true })).toBeTrue();
      expect(store.consumeRestoredDraft()).toBe('A brief');

      // The composer reports the draft it took; a draft the person did not change gives way.
      store.keepUnsentMessage({ text: 'A brief', prepared: true });
      expect(store.setDraft('The next brief')).toBeTrue();
      expect(store.pendingDraft()).toEqual({ text: 'The next brief', replace: false });
      store.consumeRestoredDraft();
      store.keepUnsentMessage({ text: '   ', prepared: false });
      expect(store.setDraft('Over blank space')).toBeTrue();
    });

    it('goes along to a new chat when the person chooses another agent, never over the new chat\'s own text', async () => {
      const store = setup();
      await store.initialize();
      await store.openConversation('c1');
      store.keepUnsentMessage({ text: 'Ask the other agent', prepared: false });

      store.selectAgent('general');
      expect(store.activeConversation()).toBeNull();
      expect(store.unsentMessage()).toEqual({ text: 'Ask the other agent', prepared: false });
      await store.openConversation('c1');
      expect(store.unsentMessage()).toBeNull();

      store.keepUnsentMessage({ text: 'Stays with c1', prepared: false });
      store.selectAgent('general');
      expect(store.unsentMessage()).toEqual({ text: 'Ask the other agent', prepared: false });
      await store.openConversation('c1');
      expect(store.unsentMessage()).toEqual({ text: 'Stays with c1', prepared: false });
    });

    it('forgets it with a deleted conversation, on sign-out and when another person signs in', async () => {
      let scope = `store-unsent-account-${crypto.randomUUID()}`;
      const store = setup(() => scope);
      await store.initialize();
      await store.openConversation('c1');
      store.keepUnsentMessage({ text: 'Goes with c1', prepared: false });
      await store.deleteConversation('c1');
      await store.openConversation('c1');
      expect(store.unsentMessage()).toBeNull();

      store.keepUnsentMessage({ text: 'The first person\'s text', prepared: false });
      store.startNewConversation();
      store.keepUnsentMessage({ text: 'The first person\'s new chat', prepared: false });
      scope += '-other';
      await store.reloadStatus();
      expect(store.unsentMessage()).toBeNull();
      await store.openConversation('c1');
      expect(store.unsentMessage()).toBeNull();

      store.keepUnsentMessage({ text: 'Before signing out', prepared: false });
      access.next(false);
      expect(store.unsentMessage()).toBeNull();
      access.next(true);
      await flush();
      await store.openConversation('c1');
      expect(store.enabled()).toBeTrue();
      expect(store.unsentMessage()).toBeNull();
    });
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

  it('leaves a consulted agent\'s proposal for the subject\'s approvers and reads the conversation again', async () => {
    const consulted: ApprovalPart = {
      ...approval,
      consultation: {
        consultationId: 'consultation-1', agentId: 'planner', subjectType: 'project', subjectId: 'p1', subjectDisplayName: 'Alpha',
        leaveRoute: 'subject-asker-rights', answer: null, movedToApprovalId: null
      }
    };
    const store = setup();
    await waitingForApproval(store, consulted);
    api.getConversation.calls.reset();
    stream = new Subject<NhAssistantSseEvent>();

    store.decide('leave');
    expect(api.decideApproval).toHaveBeenCalledOnceWith('c1', 'approval-1', { decision: 'leave', expectedProposalHash: 'hash-1' });

    stream.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u1', assistantMessageId: 'a1' } });
    const left = store.activeConversation()!.messages[1].parts[1] as ApprovalPart;
    expect(left.status).toBe('rejected');
    expect(left.consultation?.leaveRoute).toBeNull();
    expect(store.pendingApproval()).toBeNull();

    stream.next({ type: 'turn.completed', data: { turnId: 't2', status: 'completed', usage, errorCode: null } });
    stream.complete();
    await flush();

    // The conversation read again names the proposal that now waits at the subject.
    expect(api.getConversation).toHaveBeenCalledWith('c1');
  });

  it('never leaves an approval without a route at the subject', async () => {
    const store = setup();
    await waitingForApproval(store);
    stream = new Subject<NhAssistantSseEvent>();

    store.decide('leave');

    expect(api.decideApproval).not.toHaveBeenCalled();
    expect(store.pendingApproval()?.status).toBe('pending');
    expect(store.deciding()).toBeFalse();
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

  it('keeps a failed conversation open for the next message while its error stays visible', async () => {
    const store = setup();
    await store.initialize();
    const sent = store.submit('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'failed', usage, errorCode: 'assistant-model-unavailable' } });
    stream.complete();
    await sent;

    expect(store.error()?.code).toBe('assistant-model-unavailable');
    expect(store.activeConversation()!.status).toBe('failed');
    expect(store.canSend()).toBeTrue();
    expect(store.canSubmit()).toBeTrue();

    stream = new Subject<NhAssistantSseEvent>();
    const again = store.submit('Hello again');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u2', assistantMessageId: 'a2' } });

    expect(await again).toBeTrue();
    expect(store.queuedMessages()).toEqual([]);
    expect(api.createConversation).toHaveBeenCalledTimes(1);
    expect(api.sendMessage.calls.mostRecent().args[0]).toBe('c1');
    expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('Hello again');
    expect(store.error()).toBeNull();
    expect(store.activeConversation()!.status).toBe('running');
  });

  it('accepts a new message in a reopened agent thread whose last turn failed', async () => {
    const store = setup();
    await store.initialize();
    api.getConversation.and.returnValue(of({ ...conversation(), kind: 'thread', status: 'failed' }));
    await store.openConversation('c1');

    expect(store.isAgentThread()).toBeTrue();
    expect(store.error()).toBeNull();
    expect(store.canSend()).toBeTrue();

    const sent = store.submit('Is the order ready?');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });

    expect(await sent).toBeTrue();
    expect(api.sendMessage).toHaveBeenCalledOnceWith('c1', jasmine.objectContaining({ text: 'Is the order ready?' }));
  });

  it('keeps an archived conversation closed', async () => {
    const store = setup();
    await store.initialize();
    api.getConversation.and.returnValue(of({ ...conversation(), status: 'archived' }));
    await store.openConversation('c1');

    expect(store.canSend()).toBeFalse();
    expect(store.canSubmit()).toBeFalse();
    expect(await store.submit('Hello')).toBeFalse();
    expect(api.sendMessage).not.toHaveBeenCalled();
  });

  it('holds queued messages after a failed turn until the queue is resumed', async () => {
    const store = setup();
    await startTurn(store);
    await store.submit('Next');
    const next = new Subject<NhAssistantSseEvent>();
    api.sendMessage.and.returnValue(next);
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'failed', usage, errorCode: 'assistant-model-unavailable' } });
    stream.complete();
    await processQueue();

    expect(store.queuePaused()).toBeTrue();
    expect(api.sendMessage).toHaveBeenCalledTimes(1);

    store.resumeQueue();
    await processQueue();

    expect(api.sendMessage).toHaveBeenCalledTimes(2);
    expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('Next');
  });

  it('shows the code of a completed turn as a notice and keeps the conversation usable', async () => {
    const store = setup();
    await store.initialize();

    const sent = store.send('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({
      type: 'turn.completed',
      data: { turnId: 't1', status: 'completed', usage, errorCode: 'assistant-tool-call-limit-reached' }
    });
    stream.complete();
    await sent;

    expect(store.notice()).toEqual({
      code: 'assistant-tool-call-limit-reached',
      messageKey: 'nh-assistant.errors.assistant-tool-call-limit-reached'
    });
    expect(store.error()).toBeNull();
    expect(store.activeConversation()!.status).toBe('idle');
    expect(store.canSend()).toBeTrue();

    store.clearNotice();
    expect(store.notice()).toBeNull();
  });

  it('shows a no-answer notice when a completed turn produced no text and no code', async () => {
    const store = setup();
    await store.initialize();

    const sent = store.send('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'message.delta', data: { messageId: 'a1', text: '  ' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'completed', usage, errorCode: null } });
    stream.complete();
    await sent;

    expect(store.notice()).toEqual({ code: 'assistant-no-answer', messageKey: 'nh-assistant.notices.no-answer' });
    expect(store.error()).toBeNull();
    expect(store.activeConversation()!.status).toBe('idle');
    expect(store.canSend()).toBeTrue();
  });

  it('shows no notice for a completed turn with an answer and clears the notice on the next message', async () => {
    const store = setup();
    await store.initialize();

    let sent = store.send('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'completed', usage, errorCode: null } });
    stream.complete();
    await sent;
    expect(store.notice()?.code).toBe('assistant-no-answer');

    stream = new Subject<NhAssistantSseEvent>();
    sent = store.send('Again');
    await flush();
    expect(store.notice()).toBeNull();
    stream.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u2', assistantMessageId: 'a2' } });
    stream.next({ type: 'message.delta', data: { messageId: 'a2', text: 'Here you go' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't2', status: 'completed', usage, errorCode: null } });
    stream.complete();
    await sent;

    expect(store.notice()).toBeNull();
  });

  it('shows no notice for a cancelled turn and keeps failed turns on the error bar', async () => {
    const store = setup();
    await store.initialize();

    const sent = store.send('Hello');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'failed', usage, errorCode: 'assistant-budget-exhausted' } });
    stream.complete();
    await sent;

    expect(store.notice()).toBeNull();
    expect(store.error()?.code).toBe('assistant-budget-exhausted');
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

  it('restores only scoped UI pointers and fetches the conversation again after restart', async () => {
    const scope = `store-restart-${crypto.randomUUID()}`;
    const first = setup(() => scope);
    await first.initialize();
    first.selectAgent('general');
    await first.openConversation('c1');
    first.setPanelOpen(true);

    TestBed.resetTestingModule();
    const restored = setup(() => scope);
    await restored.initialize();

    expect(api.getConversation).toHaveBeenCalledWith('c1');
    expect(restored.activeConversation()?.id).toBe('c1');
    expect(restored.selectedAgentId()).toBe('projects');
    expect(restored.restorePanelOpen()).toBeTrue();

    restored.startNewConversation();
    restored.selectAgent('general');
    TestBed.resetTestingModule();
    const newThread = setup(() => scope);
    await newThread.initialize();
    expect(newThread.activeConversation()).toBeNull();
    expect(newThread.selectedAgentId()).toBe('general');
    expect(newThread.restorePanelOpen()).toBeTrue();
  });

  it('does not restore a conversation from another account or one no longer accessible', async () => {
    const scope = `store-account-${crypto.randomUUID()}`;
    const first = setup(() => scope);
    await first.initialize();
    await first.openConversation('c1');

    TestBed.resetTestingModule();
    const other = setup(() => `${scope}-other`);
    await other.initialize();
    expect(other.activeConversation()).toBeNull();

    TestBed.resetTestingModule();
    api.getConversation.and.returnValue(throwError(() => new NhAssistantApiError(404, 'assistant-conversation-not-found', 'k')));
    const missing = setup(() => scope);
    await missing.initialize();
    expect(missing.activeConversation()).toBeNull();
    expect(missing.error()).toBeNull();

    TestBed.resetTestingModule();
    api.getConversation.calls.reset();
    const again = setup(() => scope);
    await again.initialize();
    expect(api.getConversation).not.toHaveBeenCalled();
  });

  it('uses the current page hint after restoring a thread, without persisting old hints', async () => {
    const scope = `store-context-${crypto.randomUUID()}`;
    let route = '/projects/old';
    const first = setup(() => scope, () => ({ route }));
    await first.initialize();
    await first.openConversation('c1');
    await first.refreshPageContext();

    TestBed.resetTestingModule();
    route = '/projects/current';
    const restored = setup(() => scope, () => ({ route }));
    await restored.initialize();
    await restored.refreshPageContext();

    expect(restored.pageContext()?.route).toBe('/projects/current');
    const sent = restored.send('What is open?');
    await flush();
    expect(api.sendMessage.calls.mostRecent().args[1].clientContext).toEqual({ route: '/projects/current' });
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.complete();
    await sent;

    const saved = localStorage.getItem(`nh-assistant-ui:v1:${encodeURIComponent('/api/assistant')}:${encodeURIComponent(scope)}`);
    expect(saved).not.toContain('/projects/old');
    expect(saved).not.toContain('/projects/current');
    expect(saved).not.toContain('token');
  });

  it('drops the in-memory conversation when the active account changes', async () => {
    let scope = `store-switch-${crypto.randomUUID()}`;
    const store = setup(() => scope);
    await store.initialize();
    await store.openConversation('c1');

    scope += '-other';
    await store.reloadStatus();

    expect(store.activeConversation()).toBeNull();
    expect(store.selectedAgentId()).toBe('projects');
  });

  it('does not restore an old account draft after conversation creation finishes late', async () => {
    let scope = `store-late-${crypto.randomUUID()}`;
    const pending = new Subject<Conversation>();
    api.createConversation.and.returnValue(pending);
    const store = setup(() => scope);
    await store.initialize();

    const sent = store.send('Old account message');
    await flush();
    expect(api.createConversation).toHaveBeenCalled();

    scope += '-other';
    await store.reloadStatus();
    pending.next(conversation());
    pending.complete();

    expect(await sent).toBeFalse();
    expect(store.activeConversation()).toBeNull();
    expect(store.restoredDraft()).toBeNull();
  });

  describe('in an agent thread', () => {
    const intake: ApprovalPart = { ...approval, approvalId: 'intake-1', proposalHash: 'intake-hash-1' };
    const later: ApprovalPart = { ...approval, approvalId: 'intake-2', proposalHash: 'intake-hash-2' };

    function thread(decisions: ConversationThreadDecision[], approvals: ApprovalPart[] = [intake, later]): Conversation {
      return {
        ...conversation(),
        id: 't1',
        kind: 'thread',
        role: 'participant',
        currentActorId: 'lead',
        messages: [
          {
            id: 'u1',
            role: 'user',
            createdAt: '2026-10-13T10:00:00Z',
            authorActorId: 'trigger:project-created-intake',
            authorKind: 'trigger',
            parts: [{ type: 'text', text: 'New project PIETJE needs an intake review.' }]
          },
          { id: 'a1', role: 'assistant', createdAt: '2026-10-13T10:00:01Z', parts: approvals }
        ],
        thread: { subjectType: 'project', subjectId: 'p1', subjectDisplayName: 'PIETJE', role: 'approver', decisions }
      };
    }

    function decided(part: ApprovalPart, status: AgentProposal['status']): AgentProposal {
      return {
        approvalId: part.approvalId,
        proposalId: part.proposalId,
        proposalHash: part.proposalHash,
        conversationId: 't1',
        runId: 'run-1',
        agentId: 'projects',
        subjectType: 'project',
        subjectId: 'p1',
        subjectDisplayName: 'PIETJE',
        toolId: part.toolId,
        summary: part.summary,
        presentation: null,
        argumentsPreview: part.argumentsPreview,
        targets: part.targets,
        status,
        mode: 'deferred',
        policyId: 'project-intake',
        grantSource: 'agent',
        createdAt: '2026-10-13T10:00:01Z',
        expiresAt: part.expiresAt,
        initiatorActorId: 'manager',
        decidedByActorId: 'lead',
        decidedAt: '2026-10-13T10:05:00Z',
        executionResultCode: null,
        executedAt: null
      };
    }

    it('opens the thread of an agent kept out of the chat without choosing that agent for a new chat', async () => {
      api.getConversation.and.returnValue(of({ ...thread([]), agentId: 'follow-up' }));
      const store = setup();
      await store.initialize();
      const selected = store.selectedAgentId();

      await store.openConversation('t1');

      expect(store.activeConversation()?.id).toBe('t1');
      expect(store.isAgentThread()).toBeTrue();
      expect(store.selectedAgentId()).toBe(selected);
    });

    it('does not open a chat conversation of an agent the caller is not offered', async () => {
      api.getConversation.and.returnValue(of({ ...conversation(), id: 'c9', agentId: 'follow-up' }));
      const store = setup();
      await store.initialize();

      await store.openConversation('c9');

      expect(store.activeConversation()).toBeNull();
    });

    it('offers each open proposal to the approvers the server names, not to the author of a turn', async () => {
      api.getConversation.and.returnValue(of(thread([
        { approvalId: 'intake-1', runId: 'run-1', canDecide: true, reasonCode: null },
        { approvalId: 'intake-2', runId: 'run-2', canDecide: false, reasonCode: 'assistant-four-eyes-required' }
      ])));
      const store = setup();
      await store.initialize();

      await store.openConversation('t1');

      expect(store.isAgentThread()).toBeTrue();
      expect(store.approvalRights().get('intake-1')).toEqual({ canDecide: true, waitingFor: 'approver', decisionOwner: null });
      expect(store.approvalRights().get('intake-2')).toEqual({ canDecide: false, waitingFor: 'other-approver', decisionOwner: null });
      // Deferred proposals leave the thread idle, so there is no pending approval to resume.
      expect(store.canDecide()).toBeFalse();
    });

    it('says that the person who consulted the agent decides its proposal in their own chat', async () => {
      api.getConversation.and.returnValue(of(thread([
        { approvalId: 'intake-1', runId: null, canDecide: false, reasonCode: 'assistant-consult-asker-decides' }
      ], [intake])));
      const store = setup();
      await store.initialize();

      await store.openConversation('t1');

      expect(store.approvalRights().get('intake-1')).toEqual({ canDecide: false, waitingFor: 'asker', decisionOwner: null });
    });

    it('decides a run proposal with its hash through the agent-run endpoint and reloads the thread', async () => {
      api.getConversation.and.returnValues(
        of(thread([
          { approvalId: 'intake-1', runId: 'run-1', canDecide: true, reasonCode: null },
          { approvalId: 'intake-2', runId: 'run-2', canDecide: false, reasonCode: 'assistant-approval-policy-denied' }
        ])),
        of(thread(
          [{ approvalId: 'intake-2', runId: 'run-2', canDecide: false, reasonCode: 'assistant-approval-policy-denied' }],
          [{ ...intake, status: 'executing' }, later]
        ))
      );
      const pending = new Subject<AgentProposal>();
      agentRuns.decide.and.returnValue(pending);
      const store = setup();
      await store.initialize();
      await store.openConversation('t1');

      store.decide('approve', undefined, 'intake-2');
      expect(agentRuns.decide).not.toHaveBeenCalled();

      store.decide('approve', undefined, 'intake-1');
      store.decide('approve', undefined, 'intake-1');
      expect(store.deciding()).toBeTrue();
      expect(agentRuns.decide).toHaveBeenCalledOnceWith(jasmine.objectContaining({ approvalId: 'intake-1', proposalHash: 'intake-hash-1' }), 'approve', undefined);
      expect(api.decideApproval).not.toHaveBeenCalled();

      pending.next(decided(intake, 'approved'));
      pending.complete();
      await flush();

      expect(store.deciding()).toBeFalse();
      expect(api.getConversation).toHaveBeenCalledTimes(2);
      expect(store.approvalRights().has('intake-1')).toBeFalse();
      expect(store.activeConversation()?.messages[1].parts[0]).toEqual(jasmine.objectContaining({ status: 'executing' }));
      expect(store.error()).toBeNull();
    });

    it('shows a refused decision and reloads the thread', async () => {
      api.getConversation.and.returnValue(of(thread([{ approvalId: 'intake-1', runId: 'run-1', canDecide: true, reasonCode: null }], [intake])));
      agentRuns.decide.and.returnValue(throwError(() => new NhAssistantApiError(409, 'assistant-approval-not-pending', 'nh-assistant.errors.assistant-approval-not-pending')));
      const store = setup();
      await store.initialize();
      await store.openConversation('t1');

      store.decide('reject', 'Not ready', 'intake-1');
      await flush();

      expect(agentRuns.decide).toHaveBeenCalledOnceWith(jasmine.objectContaining({ approvalId: 'intake-1' }), 'reject', 'Not ready');
      expect(store.error()?.code).toBe('assistant-approval-not-pending');
      expect(store.deciding()).toBeFalse();
      expect(api.getConversation).toHaveBeenCalledTimes(2);
    });

    it('offers feedback on a proposal the user rejected in this session, and only on that one', async () => {
      api.getConversation.and.returnValues(
        of(thread([
          { approvalId: 'intake-1', runId: 'run-1', canDecide: true, reasonCode: null },
          { approvalId: 'intake-2', runId: 'run-2', canDecide: true, reasonCode: null }
        ])),
        of(thread(
          [{ approvalId: 'intake-2', runId: 'run-2', canDecide: true, reasonCode: null }],
          [{ ...intake, status: 'rejected' }, later]
        ))
      );
      agentRuns.decide.and.returnValue(of({ ...decided(intake, 'rejected'), canGiveFeedback: true }));
      const store = setup();
      await store.initialize();
      await store.openConversation('t1');
      expect(store.approvalRights().get('intake-1')?.canGiveFeedback).toBeUndefined();

      store.decide('reject', undefined, 'intake-1');
      await flush();

      expect(store.approvalRights().get('intake-1')).toEqual({
        canDecide: false,
        waitingFor: 'approver',
        decisionOwner: null,
        canGiveFeedback: true
      });
      expect(store.approvalRights().get('intake-2')?.canGiveFeedback).toBeUndefined();
    });

    it('reloads an open thread once per burst when its agent work changed elsewhere', async () => {
      const live = new FakeLiveService();
      api.getConversation.and.returnValues(
        of(thread([{ approvalId: 'intake-1', runId: 'run-1', canDecide: true, reasonCode: null }], [intake])),
        of(thread([], [{ ...intake, status: 'approved' }]))
      );
      const store = setup(undefined, undefined, [{ provide: NhAssistantLiveService, useValue: live }]);
      await store.initialize();
      await store.openConversation('t1');
      expect(store.approvalRights().get('intake-1')?.canDecide).toBeTrue();

      // Another thread, and feedback that leaves the thread unchanged, reload nothing.
      live.agentWork.next({ conversationId: 'other', reason: 'proposal', approvalId: 'other-1', runId: null, subjectType: 'project', subjectId: 'other' });
      live.agentWork.next({ conversationId: 't1', reason: 'feedback', approvalId: 'intake-1', runId: null, subjectType: 'project', subjectId: 'p-1' });
      await wait(350);
      expect(api.getConversation).toHaveBeenCalledTimes(1);

      // A colleague approved in the inbox; the run continues.
      live.agentWork.next({ conversationId: 't1', reason: 'proposal', approvalId: 'intake-1', runId: null, subjectType: 'project', subjectId: 'p-1' });
      live.agentWork.next({ conversationId: 't1', reason: 'run', approvalId: null, runId: 'run-1', subjectType: 'project', subjectId: 'p-1' });
      await wait(350);

      expect(api.getConversation).toHaveBeenCalledTimes(2);
      expect(store.approvalRights().has('intake-1')).toBeFalse();
      expect(store.activeConversation()?.messages[1].parts[0]).toEqual(jasmine.objectContaining({ status: 'approved' }));
    });

    it('offers no feedback after an approval or when the server does not allow it', async () => {
      api.getConversation.and.returnValues(
        of(thread([
          { approvalId: 'intake-1', runId: 'run-1', canDecide: true, reasonCode: null },
          { approvalId: 'intake-2', runId: 'run-2', canDecide: true, reasonCode: null }
        ])),
        of(thread([{ approvalId: 'intake-2', runId: 'run-2', canDecide: true, reasonCode: null }], [{ ...intake, status: 'approved' }, later])),
        of(thread([], [{ ...intake, status: 'approved' }, { ...later, status: 'rejected' }]))
      );
      agentRuns.decide.and.returnValues(
        of({ ...decided(intake, 'approved'), canGiveFeedback: false }),
        of(decided(later, 'rejected'))
      );
      const store = setup();
      await store.initialize();
      await store.openConversation('t1');

      store.decide('approve', undefined, 'intake-1');
      await flush();
      store.decide('reject', undefined, 'intake-2');
      await flush();

      expect(store.approvalRights().has('intake-1')).toBeFalse();
      expect(store.approvalRights().has('intake-2')).toBeFalse();
    });
  });

  describe('live messages while this tab streams a turn', () => {
    let live: FakeLiveService;

    function stored(id: string, role: Message['role'], sequence: number, authorActorId: string | null, text = id): Message {
      return { id, role, createdAt: '2026-10-23T10:00:00Z', sequence, parts: [{ type: 'text', text }], authorActorId };
    }

    function created(message: Message, actorId = message.authorActorId ?? 'application'): void {
      live.events.next({ conversationId: 'c1', actorId, type: 'message.created', data: message });
    }

    function ids(store: NhAssistantStore): string[] {
      return store.activeConversation()!.messages.map(message => message.id);
    }

    async function streaming(currentActorId: string | null = 'me'): Promise<NhAssistantStore> {
      live = new FakeLiveService();
      api.createConversation.and.callFake(request => of({ ...conversation(request.agentId), currentActorId }));
      const store = setup(undefined, undefined, [{ provide: NhAssistantLiveService, useValue: live }]);
      await store.initialize();
      const sent = store.send('First');
      await flush();
      // The server shows the sender's message to everyone, this tab too, before its stream starts the turn.
      created(stored('u1', 'user', 1, 'me', 'First'), 'me');
      stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
      expect(await sent).toBeTrue();
      stream.next({ type: 'message.delta', data: { messageId: 'a1', text: 'Checking' } });
      return store;
    }

    it('names the answer the streamed turn writes until the turn ends', async () => {
      const store = await streaming();

      expect(store.writingMessageId()).toBe('a1');
      // A message that arrives meanwhile leaves the answer as it is.
      created(stored('app-1', 'application', 3, null, 'The export finished.'));
      expect(store.writingMessageId()).toBe('a1');

      finishTurn(stream);
      await flush();
      expect(store.writingMessageId()).toBeNull();
    });

    it('forgets the answer of a streamed turn that fails', async () => {
      const store = await streaming();

      stream.next({ type: 'error', data: { code: 'assistant-model-unavailable', messageKey: 'nh-assistant.errors.assistant-model-unavailable' } });
      stream.complete();
      await flush();

      expect(store.writingMessageId()).toBeNull();
    });

    it('names the answer of a turn it follows live until that turn ends', async () => {
      live = new FakeLiveService();
      api.getConversation.and.returnValue(of({
        ...conversation(),
        currentActorId: 'me',
        messages: [stored('u1', 'user', 1, 'me', 'First'), stored('a1', 'assistant', 2, null, 'Done.')]
      }));
      const store = setup(undefined, undefined, [{ provide: NhAssistantLiveService, useValue: live }]);
      await store.initialize();
      await store.openConversation('c1');

      created(stored('u2', 'user', 3, 'sam', 'Summarize it.'));
      expect(store.writingMessageId()).toBeNull();
      live.events.next({ conversationId: 'c1', actorId: 'sam', type: 'turn.started', data: { turnId: 't2', userMessageId: 'u2', assistantMessageId: 'a2' } });

      expect(store.streaming()).toBeFalse();
      expect(store.writingMessageId()).toBe('a2');

      live.events.next({ conversationId: 'c1', actorId: 'sam', type: 'turn.completed', data: { turnId: 't2', status: 'completed', usage, errorCode: null } });
      expect(store.writingMessageId()).toBeNull();
    });

    it('shows an application\'s and a colleague\'s message after the answer it still writes, each once', async () => {
      const store = await streaming();

      created(stored('app-1', 'application', 3, null, 'The export finished.'));
      // The turn's own events come with its stream; their live copies change nothing.
      live.events.next({ conversationId: 'c1', actorId: 'me', type: 'message.delta', data: { messageId: 'a1', text: 'Checking' } });
      live.events.next({ conversationId: 'c1', actorId: 'me', type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
      created(stored('sam-1', 'user', 4, 'sam', 'I added the budget.'));
      created(stored('app-1', 'application', 3, null, 'The export finished.'));
      stream.next({ type: 'message.delta', data: { messageId: 'a1', text: ' the projects.' } });

      expect(store.streaming()).toBeTrue();
      expect(ids(store)).toEqual(['u1', 'a1', 'app-1', 'sam-1']);
      expect(store.activeConversation()!.messages[1].parts).toEqual([{ type: 'text', text: 'Checking the projects.' }]);

      finishTurn(stream);
      await flush();
      created(stored('sam-1', 'user', 4, 'sam', 'I added the budget.'));

      expect(store.streaming()).toBeFalse();
      expect(ids(store)).toEqual(['u1', 'a1', 'app-1', 'sam-1']);
      // The colleague's message does not hide the answer of this tab's turn.
      expect(store.notice()).toBeNull();
    });

    it('keeps the message once when the turn fails and the conversation is read again', async () => {
      const store = await streaming();
      created(stored('app-1', 'application', 3, null, 'The export finished.'));
      api.getConversation.and.returnValue(of({
        ...conversation(),
        currentActorId: 'me',
        status: 'failed',
        messages: [
          stored('u1', 'user', 1, 'me', 'First'),
          stored('a1', 'assistant', 2, null, 'Checking'),
          stored('app-1', 'application', 3, null, 'The export finished.')
        ]
      }));

      stream.next({ type: 'error', data: { code: 'assistant-server-error', messageKey: 'nh-assistant.errors.server' } });
      stream.complete();
      await flush();

      expect(api.getConversation).toHaveBeenCalledWith('c1');
      expect(ids(store)).toEqual(['u1', 'a1', 'app-1']);
    });

    it('never shows its own message twice, also when it does not know the current user', async () => {
      const store = await streaming(null);

      // Without the current user a user message may be this tab's own, so only the application's shows.
      created(stored('u1', 'user', 1, 'me', 'First'), 'me');
      created(stored('sam-1', 'user', 4, 'sam', 'I added the budget.'));
      created(stored('app-1', 'application', 3, null, 'The export finished.'));

      expect(ids(store)).toEqual(['u1', 'a1', 'app-1']);
    });
  });

  describe('a wait that passed its lifetime (CS9)', () => {
    const past = '2020-01-01T00:00:00Z';

    async function waitingForQuestion(store: NhAssistantStore, expiresAt: string): Promise<void> {
      await store.initialize();
      const sent = store.send('Change the status of Alpha');
      await flush();
      stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
      stream.next({
        type: 'question.asked',
        data: {
          type: 'question',
          questionId: 'question-1',
          question: 'Which status should Alpha get?',
          choices: ['Active', 'On hold'],
          addresseeActorId: 'user-1',
          expiresAt,
          status: 'pending'
        }
      });
      stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'waiting-for-approval', usage, errorCode: null } });
      stream.complete();
      await sent;
    }

    /** Submits a message and lets the store send or queue it; resolves what `submit` resolved. */
    async function submitWhileWaiting(store: NhAssistantStore, text: string): Promise<boolean> {
      stream = new Subject<NhAssistantSseEvent>();
      const submitted = store.submit(text);
      await flush();
      if (api.sendMessage.calls.count() > 1) {
        stream.next({ type: 'turn.started', data: { turnId: 't2', userMessageId: 'u2', assistantMessageId: 'a2' } });
      }
      return submitted;
    }

    it('sends a message at once when the question the chat waits for expired', async () => {
      const store = setup();
      await waitingForQuestion(store, past);

      expect(store.activeConversation()!.status).toBe('waiting-for-approval');
      expect(store.waitExpired()).toBeTrue();
      expect(store.canSend()).toBeTrue();

      expect(await submitWhileWaiting(store, 'Never mind')).toBeTrue();
      expect(api.sendMessage).toHaveBeenCalledTimes(2);
      expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('Never mind');
      expect(store.queuedMessages()).toEqual([]);
      expect(store.activeConversation()!.status).toBe('running');
    });

    it('sends a message at once when the approval the chat waits for expired', async () => {
      const store = setup();
      await waitingForApproval(store, { ...approval, expiresAt: past });

      expect(store.waitExpired()).toBeTrue();
      expect(await submitWhileWaiting(store, 'Something else')).toBeTrue();
      expect(api.sendMessage).toHaveBeenCalledTimes(2);
      expect(store.queuedMessages()).toEqual([]);
      expect(api.decideApproval).not.toHaveBeenCalled();
    });

    it('keeps a message queued while the question or the approval is still valid', async () => {
      const store = setup();
      await waitingForQuestion(store, '2099-01-01T00:00:00Z');

      expect(store.waitExpired()).toBeFalse();
      expect(store.canSend()).toBeFalse();
      expect(await submitWhileWaiting(store, 'Later')).toBeTrue();
      await processQueue();
      expect(api.sendMessage).toHaveBeenCalledTimes(1);
      expect(store.queuedMessages().map(item => item.state)).toEqual(['queued']);
    });

    it('sends what waited in the queue once the wait ends', async () => {
      const store = setup();
      await waitingForApproval(store, { ...approval, expiresAt: new Date(Date.now() + 300).toISOString() });
      stream = new Subject<NhAssistantSseEvent>();
      await store.submit('After the wait');

      // The end of the wait moves the store's clock; poll with a generous bound.
      for (let attempt = 0; attempt < 200 && api.sendMessage.calls.count() < 2; attempt++) {
        await processQueue();
        await wait(50);
      }

      expect(api.sendMessage).toHaveBeenCalledTimes(2);
      expect(api.sendMessage.calls.mostRecent().args[1].text).toBe('After the wait');
      expect(store.queuedMessages().map(item => item.state)).toEqual(['sending']);
    });

    it('handles a busy refusal after the wait as before, so the server stays the judge', async () => {
      const store = setup();
      await waitingForQuestion(store, past);
      // By its own clock the server still waits for the answer, as the conversation it returns says.
      const waiting = store.activeConversation()!;
      api.getConversation.and.returnValue(of(waiting));
      stream = new Subject<NhAssistantSseEvent>();

      const sent = store.submit('Too early by the server\'s clock');
      await flush();
      stream.next({ type: 'error', data: { code: 'assistant-conversation-busy', messageKey: 'nh-assistant.errors.assistant-conversation-busy' } });
      stream.complete();

      expect(await sent).toBeFalse();
      await flush();
      expect(store.error()?.code).toBe('assistant-conversation-busy');
      expect(store.activeConversation()!.status).toBe('waiting-for-approval');
      expect(store.activeConversation()!.pendingQuestion?.questionId).toBe('question-1');
      expect(store.queuedMessages()).toEqual([]);
      expect(store.consumeRestoredDraft()).toBe('Too early by the server\'s clock');
    });
  });

  it('keeps the chosen agent while the assistant feature is temporarily disabled', async () => {
    const scope = `store-disabled-${crypto.randomUUID()}`;
    const store = setup(() => scope);
    await store.initialize();
    store.selectAgent('general');

    api.status.and.returnValue(of({ ...status, enabled: false, agents: [] }));
    await store.reloadStatus();
    expect(store.enabled()).toBeFalse();

    api.status.and.returnValue(of(status));
    await store.reloadStatus();
    expect(store.selectedAgentId()).toBe('general');
  });
});
