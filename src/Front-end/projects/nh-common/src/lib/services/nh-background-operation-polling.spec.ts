import {fakeAsync, tick} from '@angular/core/testing';
import {
  BackgroundOperationsNhCommonModuleConfig, CollectionHttpResponse, NhAuthService,
  NhBackgroundOperationConnectionState, NhBackgroundOperationLiveUpdateService,
  NhBackgroundOperationService, NhBackgroundOperationStore, NhCommonModuleConfig
} from '@newheap/platform-common';
import {BehaviorSubject, of, Subject} from 'rxjs';

describe('Background operation polling', () => {
  let state: BehaviorSubject<NhBackgroundOperationConnectionState>;
  let resync: Subject<void>;
  let api: jasmine.SpyObj<NhBackgroundOperationService>;
  let live: jasmine.SpyObj<NhBackgroundOperationLiveUpdateService>;
  let store: NhBackgroundOperationStore;

  beforeEach(() => {
    state = new BehaviorSubject<NhBackgroundOperationConnectionState>('disconnected');
    resync = new Subject<void>();
    api = jasmine.createSpyObj('api', ['list', 'get']);
    api.list.and.returnValue(of(new CollectionHttpResponse({items: []})));
    live = jasmine.createSpyObj('live', ['start', 'stop'], {
      connectionState$: state, changed$: new Subject(), resync$: resync
    });
    const auth = jasmine.createSpyObj<NhAuthService>('auth', ['getAuthorization'], {
      authSubject: new BehaviorSubject<ReturnType<NhAuthService['getAuthorization']>>(undefined)
    });
    store = new NhBackgroundOperationStore(api, live, new NhCommonModuleConfig({
      backgroundOperations: new BackgroundOperationsNhCommonModuleConfig({pollingInterval: 15000})
    }), auth);
  });

  afterEach(() => store.ngOnDestroy());

  it('loads immediately offline, then polls every 15 seconds without overlapping requests', fakeAsync(() => {
    const pending = new Subject<CollectionHttpResponse<any>>();
    api.list.and.returnValues(of(new CollectionHttpResponse({items: []})), pending);
    const subscription = store.watchAll().subscribe();
    tick(0);
    expect(api.list).toHaveBeenCalledTimes(1);
    tick(14999);
    expect(api.list).toHaveBeenCalledTimes(1);
    tick(1);
    expect(api.list).toHaveBeenCalledTimes(2);
    tick(30000);
    expect(api.list).toHaveBeenCalledTimes(2);
    subscription.unsubscribe();
  }));

  it('stops polling when connected, but still refreshes on reconnect', fakeAsync(() => {
    const subscription = store.watchAll().subscribe();
    tick(0);
    state.next('connected');
    resync.next();
    expect(api.list).toHaveBeenCalledTimes(2);
    tick(60000);
    expect(api.list).toHaveBeenCalledTimes(2);
    state.next('reconnecting');
    tick(0);
    expect(api.list).toHaveBeenCalledTimes(3);
    tick(15000);
    expect(api.list).toHaveBeenCalledTimes(4);
    state.next('connected');
    resync.next();
    tick(30000);
    expect(api.list).toHaveBeenCalledTimes(5);
    subscription.unsubscribe();
  }));

  it('does not reset fallback polling on repeated connection attempts', fakeAsync(() => {
    const subscription = store.watchAll().subscribe();
    tick(0);
    tick(5000);
    state.next('connecting');
    state.next('disconnected');
    tick(5000);
    state.next('connecting');
    state.next('disconnected');
    tick(5000);
    expect(api.list).toHaveBeenCalledTimes(2);
    subscription.unsubscribe();
  }));

  it('does not start polling if the connection is already available', fakeAsync(() => {
    state.next('connected');
    const subscription = store.watchAll().subscribe();
    tick(30000);
    expect(api.list).not.toHaveBeenCalled();
    resync.next();
    expect(api.list).toHaveBeenCalledTimes(1);
    subscription.unsubscribe();
  }));

  it('shares one timer between consumers and stops it when the last consumer leaves', fakeAsync(() => {
    const first = store.watchAll().subscribe();
    const second = store.watchAll().subscribe();
    tick(0);
    expect(api.list).toHaveBeenCalledTimes(1);
    first.unsubscribe();
    tick(15000);
    expect(api.list).toHaveBeenCalledTimes(2);
    second.unsubscribe();
    state.next('connected');
    state.next('disconnected');
    tick(30000);
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(live.start).toHaveBeenCalledTimes(1);
    expect(live.stop).toHaveBeenCalledTimes(1);
  }));

  it('keeps polling when live updates are disabled', fakeAsync(() => {
    state.next('disabled');
    const subscription = store.watchAll().subscribe();
    tick(30000);
    expect(api.list).toHaveBeenCalledTimes(3);
    subscription.unsubscribe();
  }));
});
