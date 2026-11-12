import type { HubConnection } from '@microsoft/signalr';
import { BehaviorSubject, filter, firstValueFrom } from 'rxjs';
import { NhAuthorization, NhDivision, NhUser } from '../models/auth.models';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhAppService } from './nh-app.service';
import { NhAuthService } from './nh-auth.service';
import { NhBackgroundOperationLiveUpdateService } from './nh-background-operation-live-update.service';

const userId = '11111111-2222-3333-4444-555555555555';
const divisionId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

/** A hub connection that starts at once and records what the service asks of it. */
class FakeHubConnection {
  state: string = 'Disconnected';
  refreshFailed?: (context: unknown) => void;
  readonly refreshAuthentication = jasmine.createSpy('refreshAuthentication').and.resolveTo(3600);

  on(): void {}
  onreconnecting(): void {}
  onreconnected(): void {}
  onclose(): void {}

  onAuthenticationRefreshFailed(callback: (context: unknown) => void): void {
    this.refreshFailed = callback;
  }

  start(): Promise<void> {
    this.state = 'Connected';
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.state = 'Disconnected';
    return Promise.resolve();
  }
}

describe('NhBackgroundOperationLiveUpdateService authentication refresh', () => {
  let authSubject: BehaviorSubject<NhAuthorization | undefined>;
  let connections: FakeHubConnection[];
  let withAuthenticationRefresh: jasmine.Spy;
  let service: NhBackgroundOperationLiveUpdateService | undefined;
  let resyncs: number;

  beforeEach(async () => {
    authSubject = new BehaviorSubject<NhAuthorization | undefined>(authorization(userId, divisionId, 'token-1'));
    connections = [];
    resyncs = 0;
    const signalR = await import('@microsoft/signalr');
    spyOn(signalR.HubConnectionBuilder.prototype, 'build').and.callFake(() => {
      const connection = new FakeHubConnection();
      connections.push(connection);
      return connection as unknown as HubConnection;
    });
    withAuthenticationRefresh = spyOn(signalR.HubConnectionBuilder.prototype, 'withAuthenticationRefresh').and.callThrough();
  });

  afterEach(() => {
    service?.ngOnDestroy();
    service = undefined;
  });

  async function connected(authenticationRefresh: boolean): Promise<NhBackgroundOperationLiveUpdateService> {
    const config = new NhCommonModuleConfig();
    config.baseUrl = 'https://api.example.test';
    config.backgroundOperations.authenticationRefresh = authenticationRefresh;
    const auth = {
      authSubject,
      getAuthorization: () => authSubject.value
    } as unknown as NhAuthService;
    const app = { isPlatformServer: () => false } as unknown as NhAppService;
    service = new NhBackgroundOperationLiveUpdateService(config, auth, app);
    service.resync$.subscribe(() => resyncs++);
    service.start();
    await firstValueFrom(service.connectionState$.pipe(filter(state => state === 'connected')));
    return service;
  }

  it('refreshes the open connection after the session renewed its token instead of reconnecting', async () => {
    await connected(true);

    authSubject.next(authorization(userId, divisionId, 'token-2'));
    await settle();

    expect(withAuthenticationRefresh).toHaveBeenCalledOnceWith({ enableAutoRefresh: false });
    expect(connections.length).toBe(1);
    expect(connections[0].refreshAuthentication).toHaveBeenCalledTimes(1);
    expect(resyncs).toBe(1);
  });

  it('restarts and resynchronizes when the hub refuses the renewed token', async () => {
    const live = await connected(true);
    connections[0].refreshAuthentication.and.callFake(() => {
      connections[0].refreshFailed?.({ error: new Error('403') });
      return Promise.reject(new Error('403'));
    });

    authSubject.next(authorization(userId, divisionId, 'token-2'));
    await firstValueFrom(live.connectionState$.pipe(filter(state => state === 'connected' && connections.length === 2)));

    expect(connections[0].state).toBe('Disconnected');
    expect(connections[1].state).toBe('Connected');
    expect(resyncs).toBe(2);
  });

  it('restarts for another user or division without refreshing the open connection', async () => {
    const live = await connected(true);

    authSubject.next(authorization('99999999-2222-3333-4444-555555555555', divisionId, 'token-2'));
    await firstValueFrom(live.connectionState$.pipe(filter(state => state === 'connected' && connections.length === 2)));
    authSubject.next(authorization('99999999-2222-3333-4444-555555555555', 'ffffffff-bbbb-cccc-dddd-eeeeeeeeeeee', 'token-3'));
    await firstValueFrom(live.connectionState$.pipe(filter(state => state === 'connected' && connections.length === 3)));

    expect(connections[0].refreshAuthentication).not.toHaveBeenCalled();
    expect(connections[1].refreshAuthentication).not.toHaveBeenCalled();
    expect(resyncs).toBe(3);
  });

  it('keeps today\'s behavior without the opt-in: the connection closes on expiry and reconnects', async () => {
    await connected(false);

    authSubject.next(authorization(userId, divisionId, 'token-2'));
    await settle();

    expect(withAuthenticationRefresh).not.toHaveBeenCalled();
    expect(connections.length).toBe(1);
    expect(connections[0].refreshAuthentication).not.toHaveBeenCalled();
  });
});

function settle(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

function authorization(user: string, division: string, token: string): NhAuthorization {
  return new NhAuthorization({
    token,
    user: new NhUser({ id: user }),
    activeDivision: new NhDivision({ id: division } as Partial<NhDivision>)
  });
}
