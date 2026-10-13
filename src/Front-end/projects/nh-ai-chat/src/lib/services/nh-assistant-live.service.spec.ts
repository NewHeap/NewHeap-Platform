import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { HubConnection } from '@microsoft/signalr';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantLiveService } from './nh-assistant-live.service';

describe('NhAssistantLiveService', () => {
  let requests: string[];

  beforeEach(() => {
    requests = [];
    // The SignalR client negotiates with fetch; failing it ends the attempt before any socket opens.
    spyOn(globalThis, 'fetch').and.callFake((input: RequestInfo | URL) => {
      requests.push(input instanceof Request ? input.url : String(input));
      return Promise.reject(new TypeError('offline'));
    });
  });

  afterEach(async () => {
    await TestBed.inject(NhAssistantLiveService).stop();
  });

  it('adds the host query parameters to the hub URL, because WebSockets carry no custom headers', async () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: 'HOST_DIVISION', useValue: 'division-1' },
        provideNhAssistant({
          apiBaseUrl: 'https://api.example.test/api/assistant',
          getAccessToken: () => 'token-1',
          translations: 'host',
          getHubQueryParameters: () => ({
            divisionId: inject('HOST_DIVISION' as never) as string,
            empty: '',
            access_token: 'forged',
            ID: 'forged'
          })
        })
      ]
    });
    const live = TestBed.inject(NhAssistantLiveService);

    await live.connect('/hub/assistant');

    const negotiate = new URL(requests[0]);
    expect(negotiate.origin + negotiate.pathname).toBe('https://api.example.test/hub/assistant/negotiate');
    expect(negotiate.searchParams.get('divisionId')).toBe('division-1');
    expect(negotiate.searchParams.has('empty')).toBeFalse();
    expect(negotiate.searchParams.has('access_token')).toBeFalse();
    expect(negotiate.searchParams.has('ID')).toBeFalse();
    expect(live.state()).toBe('disconnected');
  });

  it('connects without the parameters when the getter fails', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => 'token-1',
          translations: 'host',
          hubBaseUrl: '/api',
          getHubQueryParameters: () => {
            throw new Error('No session yet.');
          }
        })
      ]
    });
    const live = TestBed.inject(NhAssistantLiveService);

    await live.connect('/hub/assistant');

    const negotiate = new URL(requests[0]);
    expect(negotiate.pathname).toBe('/api/hub/assistant/negotiate');
    expect(negotiate.search).toBe('?negotiateVersion=1');
  });
});

describe('NhAssistantLiveService hub messages', () => {
  it('emits agent work from the AgentWorkChanged hub method and completes it on destroy', async () => {
    const handlers = new Map<string, (message: unknown) => void>();
    const connection = {
      on: (name: string, handler: (message: unknown) => void) => handlers.set(name, handler),
      onreconnecting: () => undefined,
      onreconnected: () => undefined,
      onclose: () => undefined,
      start: () => Promise.resolve(),
      stop: () => Promise.resolve()
    };
    const signalR = await import('@microsoft/signalr');
    spyOn(signalR.HubConnectionBuilder.prototype, 'build').and.returnValue(connection as unknown as HubConnection);
    TestBed.configureTestingModule({
      providers: [provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' })]
    });
    const live = TestBed.inject(NhAssistantLiveService);
    const received: LiveAgentWorkChanged[] = [];
    let resyncs = 0;
    let completed = false;
    live.agentWork$.subscribe({ next: change => received.push(change), complete: () => (completed = true) });
    live.resync$.subscribe(() => resyncs++);

    await live.connect('/hub/assistant');
    const change: LiveAgentWorkChanged = {
      conversationId: 'thread-1',
      reason: 'proposal',
      approvalId: 'approval-1',
      runId: null,
      subjectType: 'project',
      subjectId: 'project-1'
    };
    handlers.get('AgentWorkChanged')!(change);

    expect(live.state()).toBe('connected');
    expect(resyncs).toBe(1);
    expect(received).toEqual([change]);

    TestBed.resetTestingModule();
    expect(completed).toBeTrue();
  });
});
