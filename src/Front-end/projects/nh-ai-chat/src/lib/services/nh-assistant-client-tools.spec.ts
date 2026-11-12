import { Injectable, InjectionToken, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, Observable, Subject, of } from 'rxjs';
import { AssistantStatus, ClientToolRequest, Conversation } from '../models/assistant-api.models';
import { NhAssistantSseEvent } from '../models/assistant-sse.models';
import { NhAssistantAccessPolicy } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { NhAssistantApiService } from './nh-assistant-api.service';
import {
  NhAssistantClientToolHandler,
  NhAssistantClientToolInvocation,
  NhAssistantClientTools,
  nhAssistantBoundClientToolResult,
  provideNhAssistantClientTools
} from './nh-assistant-client-tools';
import { applyNhAssistantEvent } from './nh-assistant-reducer';
import { NhAssistantStore } from './nh-assistant.store';

const config = {
  apiBaseUrl: '/api/assistant',
  getAccessToken: () => 'token',
  defaultAgentId: 'projects'
};

const OPEN = 'projects.open';
const CREATE = 'projects.create-task';

function invocation(toolId: string, args: Record<string, unknown> = {}): NhAssistantClientToolInvocation {
  return { invocationId: 'i1', toolId, conversationId: 'c1', arguments: args, expiresAt: '2099-01-01T00:00:00Z' };
}

describe('NhAssistantClientTools', () => {
  it('lists the tool ids of every registration and runs their handlers in the assistant scope', async () => {
    const GREETING = new InjectionToken<string>('greeting');
    const ran: NhAssistantClientToolInvocation[] = [];
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant(config),
        { provide: GREETING, useValue: 'hello' },
        provideNhAssistantClientTools({
          toolId: OPEN,
          run: call => {
            ran.push(call);
            // The handler starts in the assistant's injection context.
            return { status: 'completed', outcome: inject(GREETING) === 'hello' ? 'navigated' : 'closed' };
          }
        }),
        provideNhAssistantClientTools({ toolId: CREATE, run: () => Promise.resolve({ status: 'completed', outcome: 'saved', id: 'task-1' }) })
      ]
    });
    const tools = TestBed.inject(NhAssistantClientTools);

    expect(tools.ids()).toEqual([OPEN, CREATE]);
    expect(tools.has(CREATE)).toBeTrue();
    expect(await tools.run(invocation(OPEN, { projectId: 'p-1' }))).toEqual({ status: 'completed', outcome: 'navigated' });
    expect(ran[0].arguments).toEqual({ projectId: 'p-1' });
    expect(await tools.run(invocation(CREATE))).toEqual({ status: 'completed', outcome: 'saved', id: 'task-1' });
  });

  it('reports a tool without a handler as unavailable and a handler that throws as failed', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant(config),
        provideNhAssistantClientTools({ toolId: OPEN, run: () => { throw new Error('The page is gone.'); } })
      ]
    });
    const tools = TestBed.inject(NhAssistantClientTools);

    expect(await tools.run(invocation('projects.unknown'))).toEqual({ status: 'unavailable', outcome: 'no-handler' });
    expect(await tools.run(invocation(OPEN))).toEqual({ status: 'failed' });
  });

  it('refuses a tool id with two handlers', () => {
    const handler: NhAssistantClientToolHandler = { toolId: OPEN, run: () => ({ status: 'completed' }) };
    TestBed.configureTestingModule({
      providers: [provideNhAssistant(config), provideNhAssistantClientTools(handler, handler)]
    });

    expect(() => TestBed.inject(NhAssistantClientTools)).toThrowError(/projects\.open/);
  });

  it('keeps a result within the bounds the server accepts', () => {
    expect(nhAssistantBoundClientToolResult({ status: 'completed', outcome: 'Saved it!', id: 'id with spaces' })).toEqual({ status: 'completed' });
    expect(nhAssistantBoundClientToolResult({ status: 'completed', outcome: 'saved', id: 'task-1', data: { taskId: 't-1' } }))
      .toEqual({ status: 'completed', outcome: 'saved', id: 'task-1', data: { taskId: 't-1' } });
    expect(nhAssistantBoundClientToolResult({ status: 'done' as never })).toEqual({ status: 'failed' });
    expect(nhAssistantBoundClientToolResult(undefined)).toEqual({ status: 'failed' });
  });
});

describe('NhAssistantStore with client tools', () => {
  const status: AssistantStatus = {
    enabled: true,
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
    limits: { maxMessageChars: 100, maxToolCallsPerTurn: 8 },
    canAdminister: false
  };
  const usage = { inputTokens: 1, outputTokens: 1, toolCalls: 1 };
  const access = new BehaviorSubject(true);

  @Injectable()
  class TestAccessPolicy implements NhAssistantAccessPolicy {
    canUse(): Observable<boolean> {
      return access;
    }
  }

  let api: jasmine.SpyObj<NhAssistantApiService>;
  let stream: Subject<NhAssistantSseEvent>;
  let resumed: Subject<NhAssistantSseEvent>;
  let ran: NhAssistantClientToolInvocation[];

  function conversation(): Conversation {
    return {
      id: 'c1',
      agentId: 'projects',
      agentVersion: 1,
      title: null,
      status: 'idle',
      createdAt: '2026-11-11T10:00:00Z',
      updatedAt: '2026-11-11T10:00:00Z',
      messages: [],
      pendingApproval: null,
      currentActorId: 'user-1'
    };
  }

  function request(actorId = 'user-1', withArguments = true): ClientToolRequest {
    return {
      invocationId: 'i1',
      toolId: CREATE,
      actorId,
      expiresAt: '2099-01-01T00:00:00Z',
      status: 'pending',
      ...(withArguments ? { arguments: { projectId: 'p-1', title: 'Book the venue' } } : {})
    };
  }

  function flush(): Promise<void> {
    return new Promise(resolve => setTimeout(resolve));
  }

  function setup(): NhAssistantStore {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ ...config, accessPolicy: TestAccessPolicy }),
        provideNhAssistantClientTools({
          toolId: CREATE,
          run: call => {
            ran.push(call);
            return { status: 'completed', outcome: 'saved', id: 'task-7' };
          }
        }),
        { provide: NhAssistantApiService, useValue: api },
        { provide: NhAssistantAgentRunApiService, useValue: jasmine.createSpyObj('NhAssistantAgentRunApiService', ['decide']) }
      ]
    });
    return TestBed.inject(NhAssistantStore);
  }

  async function sendAndWaitForClient(store: NhAssistantStore, requested: ClientToolRequest): Promise<void> {
    await store.initialize();
    const sent = store.send('Create a task to book the venue.');
    await flush();
    stream.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a1' } });
    stream.next({
      type: 'tool.started',
      data: { invocationId: 'i1', toolId: CREATE, toolVersion: 1, displayName: 'Create task', argumentsPreview: null }
    });
    stream.next({ type: 'client-tool.requested', data: requested });
    stream.next({ type: 'turn.completed', data: { turnId: 't1', status: 'waiting-for-approval', usage, errorCode: null } });
    stream.complete();
    await sent;
    await flush();
    await flush();
  }

  beforeEach(() => {
    access.next(true);
    ran = [];
    stream = new Subject<NhAssistantSseEvent>();
    resumed = new Subject<NhAssistantSseEvent>();
    api = jasmine.createSpyObj<NhAssistantApiService>('NhAssistantApiService', [
      'status', 'agents', 'listConversations', 'createConversation', 'getConversation', 'sendMessage', 'reportClientTool', 'cancel'
    ]);
    api.status.and.returnValue(of(status));
    api.listConversations.and.returnValue(of({ items: [], total: 0 }));
    api.createConversation.and.returnValue(of(conversation()));
    api.getConversation.and.returnValue(of(conversation()));
    api.sendMessage.and.callFake(() => stream);
    api.reportClientTool.and.callFake(() => resumed);
  });

  it('names its client tools, runs the handler after its own turn waits and reports the result', async () => {
    const store = setup();

    await sendAndWaitForClient(store, request());

    expect(api.sendMessage.calls.mostRecent().args[1].clientTools).toEqual([CREATE]);
    expect(ran.length).toBe(1);
    expect(ran[0]).toEqual(jasmine.objectContaining({ invocationId: 'i1', toolId: CREATE, conversationId: 'c1' }));
    expect(ran[0].arguments).toEqual({ projectId: 'p-1', title: 'Book the venue' });
    const [conversationId, invocationId, body] = api.reportClientTool.calls.mostRecent().args;
    expect([conversationId, invocationId]).toEqual(['c1', 'i1']);
    expect(body).toEqual({ status: 'completed', outcome: 'saved', id: 'task-7', clientTools: [CREATE] });
    // The conversation waits until the server accepted the report, and runs again then.
    expect(store.activeConversation()!.status).toBe('waiting-for-approval');

    resumed.next({ type: 'turn.started', data: { turnId: 't1', userMessageId: 'u1', assistantMessageId: 'a2' } });
    expect(store.activeConversation()!.status).toBe('running');
    expect(store.activeConversation()!.pendingClientTool).toBeNull();
    resumed.next({ type: 'tool.completed', data: { invocationId: 'i1', status: 'succeeded', resultCode: null, resultPreview: null } });
    resumed.next({ type: 'turn.completed', data: { turnId: 't1', status: 'completed', usage, errorCode: null } });
    resumed.complete();
    await flush();
    expect(store.activeConversation()!.status).toBe('idle');
  });

  it('never runs a handler for another person or without the arguments of its own stream', async () => {
    const store = setup();

    await sendAndWaitForClient(store, request('user-2'));
    expect(ran).toEqual([]);
    expect(api.reportClientTool).not.toHaveBeenCalled();
    expect(store.activeConversation()!.pendingClientTool?.actorId).toBe('user-2');

    // What live updates and other sessions see carries no arguments, so no handler runs for it.
    const shared = applyNhAssistantEvent(conversation(), { type: 'client-tool.requested', data: request('user-1', false) });
    expect(shared.pendingClientTool).toEqual(jasmine.objectContaining({ invocationId: 'i1', actorId: 'user-1' }));
    expect(shared.status).toBe('waiting-for-approval');
  });

  it('keeps the arguments out of the conversation state', () => {
    const waiting = applyNhAssistantEvent(conversation(), { type: 'client-tool.requested', data: request() });

    expect(waiting.pendingClientTool).toEqual({
      invocationId: 'i1', toolId: CREATE, actorId: 'user-1', expiresAt: '2099-01-01T00:00:00Z', status: 'pending'
    });
  });
});
