import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { HubConnection } from '@microsoft/signalr';
import { Subject } from 'rxjs';
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

describe('NhAssistantLiveService authentication refresh', () => {
  /** A hub connection that starts at once, presents its token as SignalR does, and records refreshes. */
  class FakeHubConnection {
    refreshFailed?: (context: unknown) => void;
    readonly refreshAuthentication = jasmine.createSpy('refreshAuthentication').and.callFake(async () => {
      await this.accessTokenFactory();
      return 3600;
    });
    stopped = false;

    constructor(private readonly accessTokenFactory: () => Promise<string>) {}

    on(): void {}
    onreconnecting(): void {}
    onreconnected(): void {}
    onclose(): void {}

    onAuthenticationRefreshFailed(callback: (context: unknown) => void): void {
      this.refreshFailed = callback;
    }

    async start(): Promise<void> {
      await this.accessTokenFactory();
    }

    stop(): Promise<void> {
      this.stopped = true;
      return Promise.resolve();
    }
  }

  let token: string;
  let scope: string;
  let changes: Subject<void>;
  let connections: FakeHubConnection[];
  let withAuthenticationRefresh: jasmine.Spy;

  beforeEach(async () => {
    token = 'token-1';
    scope = 'user-1';
    changes = new Subject<void>();
    connections = [];
    const signalR = await import('@microsoft/signalr');
    let accessTokenFactory: () => Promise<string> = () => Promise.resolve('');
    const withUrl = signalR.HubConnectionBuilder.prototype.withUrl;
    // Keeps the token factory the service passes, so the fake connection presents tokens as SignalR does.
    (spyOn(signalR.HubConnectionBuilder.prototype, 'withUrl') as jasmine.Spy).and.callFake(function (this: unknown, url: string, options: unknown) {
      accessTokenFactory = (options as { accessTokenFactory: () => Promise<string> }).accessTokenFactory;
      return withUrl.call(this as never, url, options as never);
    });
    spyOn(signalR.HubConnectionBuilder.prototype, 'build').and.callFake(() => {
      const connection = new FakeHubConnection(accessTokenFactory);
      connections.push(connection);
      return connection as unknown as HubConnection;
    });
    withAuthenticationRefresh = spyOn(signalR.HubConnectionBuilder.prototype, 'withAuthenticationRefresh').and.callThrough();
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({
          apiBaseUrl: '/api/assistant',
          getAccessToken: () => token,
          accessTokenChanges: () => changes.asObservable(),
          getStateScope: () => scope,
          translations: 'host'
        })
      ]
    });
  });

  afterEach(async () => {
    await TestBed.inject(NhAssistantLiveService).stop();
  });

  it('presents a renewed token of the same account on the open connection instead of reconnecting', async () => {
    const live = TestBed.inject(NhAssistantLiveService);
    let resyncs = 0;
    live.resync$.subscribe(() => resyncs++);
    await live.connect('/hub/assistant', false, { authenticationRefresh: true });

    // The same token again refreshes nothing.
    changes.next();
    await settle();
    token = 'token-2';
    changes.next();
    await settle();

    expect(withAuthenticationRefresh).toHaveBeenCalledOnceWith({ enableAutoRefresh: false });
    expect(connections.length).toBe(1);
    expect(connections[0].refreshAuthentication).toHaveBeenCalledTimes(1);
    expect(live.state()).toBe('connected');
    expect(resyncs).toBe(1);
  });

  it('leaves the token of another account to the restart of the account switch', async () => {
    const live = TestBed.inject(NhAssistantLiveService);
    await live.connect('/hub/assistant', false, { authenticationRefresh: true });

    token = 'token-of-another-account';
    scope = 'user-2';
    changes.next();
    await settle();
    // The store restarts the connection when the account changed.
    await live.connect('/hub/assistant', true, { authenticationRefresh: true });

    expect(connections[0].refreshAuthentication).not.toHaveBeenCalled();
    expect(connections[0].stopped).toBeTrue();
    expect(connections.length).toBe(2);
  });

  it('restarts and reloads snapshots when the hub refuses the renewed token', async () => {
    const live = TestBed.inject(NhAssistantLiveService);
    let resyncs = 0;
    live.resync$.subscribe(() => resyncs++);
    await live.connect('/hub/assistant', false, { authenticationRefresh: true });
    connections[0].refreshAuthentication.and.callFake(() => {
      connections[0].refreshFailed?.({ error: new Error('403') });
      return Promise.reject(new Error('403'));
    });

    token = 'token-2';
    changes.next();
    await settle();
    await settle();

    expect(connections[0].stopped).toBeTrue();
    expect(connections.length).toBe(2);
    expect(live.state()).toBe('connected');
    expect(resyncs).toBe(2);
  });

  it('refreshes nothing when the server does not accept authentication refresh', async () => {
    const live = TestBed.inject(NhAssistantLiveService);
    await live.connect('/hub/assistant');

    token = 'token-2';
    changes.next();
    await settle();

    expect(withAuthenticationRefresh).not.toHaveBeenCalled();
    expect(connections[0].refreshAuthentication).not.toHaveBeenCalled();
  });

  function settle(): Promise<void> {
    return new Promise(resolve => setTimeout(resolve));
  }
});
