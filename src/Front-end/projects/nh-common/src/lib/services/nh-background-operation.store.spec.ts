import { fakeAsync, tick } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, firstValueFrom, of, Subject, take, throwError } from 'rxjs';
import {
  NhBackgroundOperation,
  NhBackgroundOperationChanged
} from '../models/background-operation.models';
import { NhAuthorization, NhDivision, NhUser } from '../models/auth.models';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhAuthService } from './nh-auth.service';
import { NhBackgroundOperationLiveUpdateService } from './nh-background-operation-live-update.service';
import { NhBackgroundOperationService } from './nh-background-operation.service';
import { NhBackgroundOperationStore } from './nh-background-operation.store';

describe('NhBackgroundOperationStore', () => {
  let store: NhBackgroundOperationStore;
  let api: jasmine.SpyObj<NhBackgroundOperationService>;
  let authSubject: BehaviorSubject<NhAuthorization | undefined>;

  beforeEach(() => {
    const changed = new Subject<NhBackgroundOperationChanged>();
    const resync = new Subject<void>();
    const live = {
      changed$: changed.asObservable(),
      resync$: resync.asObservable(),
      connectionState$: of('connected'),
      start: jasmine.createSpy('start'),
      stop: jasmine.createSpy('stop')
    } as unknown as NhBackgroundOperationLiveUpdateService;
    api = jasmine.createSpyObj<NhBackgroundOperationService>(
      'api',
      ['list', 'get', 'cancel', 'retry']
    );
    authSubject = new BehaviorSubject<NhAuthorization | undefined>(
      authorization('11111111-2222-3333-4444-555555555555', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee')
    );
    const auth = {
      authSubject,
      getAuthorization: () => authSubject.value
    } as unknown as NhAuthService;
    store = new NhBackgroundOperationStore(api, live, new NhCommonModuleConfig(), auth);
  });

  afterEach(() => store.ngOnDestroy());

  it('does not overwrite a newer durable version with stale polling data', async () => {
    (store as any).mergeDetail(operation(2, 30, [event(2)]));
    (store as any).mergeSummary(operation(1, 10));

    const operations = await firstValueFrom(store.operations$.pipe(take(1)));

    expect(operations[0].version).toBe(2);
    expect(operations[0].progressPercentage).toBe(30);
    expect(operations[0].events.map(item => item.sequence)).toEqual([2]);
  });

  it('merges event deltas by sequence without duplicating earlier events', async () => {
    (store as any).mergeDetail(operation(2, 30, [event(1), event(2)]));
    (store as any).mergeDetail(operation(3, 60, [event(2), event(3)]));

    const operations = await firstValueFrom(store.operations$.pipe(take(1)));

    expect(operations[0].events.map(item => item.sequence)).toEqual([1, 2, 3]);
    expect(operations[0].progressPercentage).toBe(60);
  });

  it('preserves child detail when a newer polling summary arrives', async () => {
    const detail = operation(2, 30);
    detail.children = [
      {
        id: 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff',
        parentOperationId: detail.id,
        operationType: 'test-child',
        fanOutKey: 'fan-out',
        fanOutItemKey: 'item-1',
        status: 'Running',
        progressPercentage: 50,
        creationDateTime: detail.creationDateTime,
        lastModifiedDateTime: detail.lastModifiedDateTime,
        children: []
      }
    ];

    (store as any).mergeDetail(detail);
    (store as any).mergeSummary(operation(3, 100));

    const operations = await firstValueFrom(store.operations$.pipe(take(1)));
    expect(operations[0].version).toBe(3);
    expect(operations[0].children.map(child => child.fanOutItemKey)).toEqual(['item-1']);
  });

  it('refreshes watched details on the polling fallback interval', fakeAsync(() => {
    (store as any).config.backgroundOperations.pollingInterval = 1000;
    api.list.and.returnValue(of({ items: [] } as any));
    api.get.and.returnValue(of(operation(2, 30)));

    const subscription = store.watch('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee').subscribe();
    tick();
    const requestsAfterInitialLoad = api.get.calls.count();

    tick(1000);

    expect(api.get.calls.count()).toBeGreaterThan(requestsAfterInitialLoad);
    subscription.unsubscribe();
  }));

  it('clears cached operations when the authenticated user or active division changes', async () => {
    (store as any).mergeDetail(operation(2, 30));

    authSubject.next(
      authorization('99999999-2222-3333-4444-555555555555', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee')
    );

    let operations = await firstValueFrom(store.operations$.pipe(take(1)));
    expect(operations).toEqual([]);

    (store as any).mergeDetail(operation(3, 60));
    authSubject.next(
      authorization('99999999-2222-3333-4444-555555555555', 'ffffffff-bbbb-cccc-dddd-eeeeeeeeeeee')
    );

    operations = await firstValueFrom(store.operations$.pipe(take(1)));
    expect(operations).toEqual([]);
  });

  it('ignores list and detail responses that complete after an authorization scope change', async () => {
    const listResponse = new Subject<any>();
    const detailResponse = new Subject<NhBackgroundOperation>();
    api.list.and.returnValue(listResponse);
    api.get.and.returnValue(detailResponse);

    const listSubscription = (store as any).loadList().subscribe();
    (store as any).refreshOperation('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee');

    authSubject.next(
      authorization('99999999-2222-3333-4444-555555555555', 'ffffffff-bbbb-cccc-dddd-eeeeeeeeeeee')
    );
    listResponse.next({ items: [operation(2, 30)] });
    listResponse.complete();
    detailResponse.next(operation(3, 60));
    detailResponse.complete();

    const operations = await firstValueFrom(store.operations$.pipe(take(1)));
    expect(operations).toEqual([]);
    listSubscription.unsubscribe();
  });

  it('reconciles an unwatched polling page as a snapshot', async () => {
    const retainedId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
    const removedId = 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff';
    (store as any).mergeDetail(operation(2, 30, [], retainedId));
    (store as any).mergeDetail(operation(2, 30, [], removedId));
    api.list.and.returnValue(of({ items: [operation(3, 60, [], retainedId)] } as any));

    await store.refreshList();

    const operations = await firstValueFrom(store.operations$.pipe(take(1)));
    expect(operations.map(item => item.id)).toEqual([retainedId]);
  });

  it('removes a cached detail after the server reports that it is no longer visible', async () => {
    const operationId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
    (store as any).mergeDetail(operation(2, 30, [], operationId));
    api.get.and.returnValue(throwError(() => new HttpErrorResponse({ status: 404 })));

    (store as any).refreshOperation(operationId);

    const operations = await firstValueFrom(store.operations$.pipe(take(1)));
    expect(operations).toEqual([]);
  });

  function operation(
    version: number,
    progressPercentage: number,
    events: NhBackgroundOperation['events'] = [],
    id: string = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
  ): NhBackgroundOperation {
    return {
      id,
      creationDateTime: '2026-08-24T00:00:00Z',
      lastModifiedDateTime: `2026-08-24T00:00:0${version}Z`,
      operationType: 'test-operation',
      payloadSchemaVersion: 1,
      ownerUserId: '11111111-2222-3333-4444-555555555555',
      status: 'Running',
      queue: 'default',
      priority: 0,
      currentAttemptNumber: 1,
      progressPercentage,
      version,
      latestEventSequence: events.at(-1)?.sequence ?? 0,
      attempts: [],
      steps: [],
      children: [],
      events
    };
  }

  function event(sequence: number): NhBackgroundOperation['events'][number] {
    return {
      id: `00000000-0000-0000-0000-${sequence.toString().padStart(12, '0')}`,
      sequence,
      eventType: 'Message',
      severity: 'Information',
      snapshotVersion: sequence,
      creationDateTime: `2026-08-24T00:00:0${sequence}Z`,
      isMilestone: false
    };
  }

  function authorization(userId: string, divisionId: string): NhAuthorization {
    return new NhAuthorization({
      user: new NhUser({ id: userId }),
      activeDivision: new NhDivision({ id: divisionId })
    });
  }
});
