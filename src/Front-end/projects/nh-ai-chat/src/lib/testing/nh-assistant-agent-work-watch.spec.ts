import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, flushMicrotasks, tick } from '@angular/core/testing';
import type { HubConnection } from '@microsoft/signalr';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import {
  NhAssistantMockBackend,
  NhAssistantMockLiveService,
  NhAssistantMockScenario,
  provideNhAssistantMockApi
} from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantLiveService } from '../services/nh-assistant-live.service';
import { NhAssistantAgentInboxComponent } from '../../../agent-runs/src/public-api';

/**
 * Live updates for the approvers of the whole application: it lists no members, so the screens that
 * show its agents' work, the approval inbox and the boards, watch that work on the hub while they are
 * shown, and the hub sends it to the people the server decides may read it.
 */
interface FakeConnection {
  invoked: string[];
  answers: Array<Promise<unknown>>;
  handlers: Map<string, (message: unknown) => void>;
  reconnected?: () => void;
}

async function connectFake(answer: () => Promise<unknown>): Promise<{ live: NhAssistantLiveService; fake: FakeConnection }> {
  const fake: FakeConnection = { invoked: [], answers: [], handlers: new Map() };
  const connection = {
    on: (name: string, handler: (message: unknown) => void) => fake.handlers.set(name, handler),
    onreconnecting: () => undefined,
    onreconnected: (handler: () => void) => (fake.reconnected = handler),
    onclose: () => undefined,
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
    invoke: (method: string) => {
      fake.invoked.push(method);
      return method === 'WatchAgentWork' ? answer() : Promise.resolve();
    }
  };
  const signalR = await import('@microsoft/signalr');
  spyOn(signalR.HubConnectionBuilder.prototype, 'build').and.returnValue(connection as unknown as HubConnection);
  TestBed.configureTestingModule({
    providers: [provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' })]
  });
  return { live: TestBed.inject(NhAssistantLiveService), fake };
}

async function flushPromises(): Promise<void> {
  for (let round = 0; round < 6; round++) {
    await Promise.resolve();
  }
}

describe('Watching the work of agents for the whole application on the hub', () => {
  afterEach(() => {
    jasmine.clock().uninstall();
  });

  it('asks the hub while screens watch, renews when the server says and after a reconnect, and stops with the last screen', async () => {
    const { live, fake } = await connectFake(() => Promise.resolve({ watched: 2, refreshAfterSeconds: 120 }));
    jasmine.clock().install();

    // Nothing is asked before a screen watches.
    await live.connect('/hub/assistant');
    expect(fake.invoked).toEqual([]);

    const releaseInbox = live.watchAgentWork();
    const releaseBoard = live.watchAgentWork();
    await flushPromises();
    expect(fake.invoked).toEqual(['WatchAgentWork']);

    // The server's renewal lets it check the person's rights again.
    jasmine.clock().tick(120_000);
    await flushPromises();
    expect(fake.invoked).toEqual(['WatchAgentWork', 'WatchAgentWork']);

    // A reconnected connection belongs to no watch yet.
    fake.reconnected!();
    await flushPromises();
    expect(fake.invoked.filter(method => method === 'WatchAgentWork').length).toBe(3);

    // The watch ends with the last screen, once.
    releaseInbox();
    releaseInbox();
    expect(fake.invoked).not.toContain('UnwatchAgentWork');
    releaseBoard();
    expect(fake.invoked.at(-1)).toBe('UnwatchAgentWork');
    jasmine.clock().tick(600_000);
    expect(fake.invoked.filter(method => method === 'WatchAgentWork').length).toBe(3);
    await live.stop();
  });

  it('keeps watching quietly when the hub cannot, and asks again later', async () => {
    const { live, fake } = await connectFake(() => Promise.reject(new Error('Method does not exist.')));
    jasmine.clock().install();
    await live.connect('/hub/assistant');

    live.watchAgentWork();
    await flushPromises();
    expect(fake.invoked).toEqual(['WatchAgentWork']);
    expect(live.state()).toBe('connected');

    jasmine.clock().tick(300_000);
    await flushPromises();
    expect(fake.invoked).toEqual(['WatchAgentWork', 'WatchAgentWork']);
    await live.stop();
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantAgentInboxComponent],
  template: `@if (shown()) { <nh-assistant-agent-inbox /> }`
})
class InboxHost {
  readonly shown = signal(true);
}

function scenario(callerPolicies?: string[]): NhAssistantMockScenario {
  return {
    agents: [],
    turns: [],
    callerPolicies,
    proposals: [{
      approvalId: 'approval-1', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', toolId: 'projects.change-status'
    }],
    admin: {
      agents: [{
        source: 'admin', id: 'follow-up', displayName: 'Project follow-up', description: 'Counts the open projects.', instructions: 'Count.',
        toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true, offeredInChat: false,
        worksOnSubjectTypes: [], worksOnAllSubjects: false, worksOnApplication: true, viewPolicy: 'app.project.view'
      }]
    }
  };
}

const proposed: LiveAgentWorkChanged = {
  conversationId: 'follow-up-thread',
  reason: 'proposal',
  approvalId: 'approval-2',
  runId: 'run-2',
  subjectType: 'nh-system',
  subjectId: 'global'
};

describe('The approval inbox and the work of the whole application', () => {
  async function configure(callerPolicies?: string[]): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        provideNhAssistantMockApi(scenario(callerPolicies))
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    await TestBed.inject(NhAssistantLiveService).connect('/hub/assistant-mock');
  }

  function inboxRequests(): number {
    return TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.path.startsWith('approvals/inbox')).length;
  }

  it('watches it while shown, so a proposal of a run for the whole application reloads the inbox', fakeAsync(() => {
    void configure();
    flushMicrotasks();
    const live = TestBed.inject(NhAssistantLiveService) as unknown as NhAssistantMockLiveService;
    const fixture: ComponentFixture<InboxHost> = TestBed.createComponent(InboxHost);
    fixture.detectChanges();
    tick(500);
    expect(live.watching()).toBeTrue();
    const before = inboxRequests();

    // A run in the background places a proposal of the whole application: the approver's inbox reloads.
    TestBed.inject(NhAssistantMockBackend).publishAgentWork(proposed, 'follow-up');
    tick(500);
    expect(inboxRequests()).toBe(before + 1);

    // Closing the inbox ends the watch: the whole application's work no longer arrives.
    fixture.componentInstance.shown.set(false);
    fixture.detectChanges();
    expect(live.watching()).toBeFalse();
    const closed = inboxRequests();
    TestBed.inject(NhAssistantMockBackend).publishAgentWork(proposed, 'follow-up');
    tick(500);
    expect(inboxRequests()).toBe(closed);
  }));

  it('does not reach a person who does not pass the agent view policy', fakeAsync(() => {
    void configure([]);
    flushMicrotasks();
    const fixture: ComponentFixture<InboxHost> = TestBed.createComponent(InboxHost);
    fixture.detectChanges();
    tick(500);
    const before = inboxRequests();

    TestBed.inject(NhAssistantMockBackend).publishAgentWork(proposed, 'follow-up');
    tick(500);
    expect(inboxRequests()).toBe(before);
  }));
});
