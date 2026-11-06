import {Component, PLATFORM_ID} from '@angular/core';
import {ComponentFixture, TestBed, fakeAsync, tick} from '@angular/core/testing';
import {provideTranslateService} from '@ngx-translate/core';
import {BehaviorSubject, Observable, Subject, defer, finalize, of} from 'rxjs';
import {
  NhBackgroundOperation,
  NhBackgroundOperationChanged,
  NhBackgroundOperationStatus,
  nhBackgroundOperationIsActive
} from '../../models/background-operation.models';
import {NhAuthorization} from '../../models/auth.models';
import {NhCommonModuleConfig} from '../../models/config.models';
import {CollectionHttpResponse} from '../../models/http.models';
import {NhAuthService} from '../../services/nh-auth.service';
import {NhBackgroundOperationLiveUpdateService} from '../../services/nh-background-operation-live-update.service';
import {NhBackgroundOperationService} from '../../services/nh-background-operation.service';
import {NhBackgroundOperationStore} from '../../services/nh-background-operation.store';
import {NhBackgroundOperationsBadgeComponent} from './component';

/** A store double that counts its watchers like the real store's `start` and `stop`. */
class FakeBackgroundOperationStore {
  readonly operations = new BehaviorSubject<readonly NhBackgroundOperation[]>([]);
  readonly error = new BehaviorSubject<unknown | undefined>(undefined);
  readonly error$ = this.error.asObservable();
  watchCalls = 0;
  watchers = 0;

  watchAll(): Observable<readonly NhBackgroundOperation[]> {
    return defer(() => {
      this.watchCalls++;
      this.watchers++;
      return this.operations.pipe(finalize(() => this.watchers--));
    });
  }
}

@Component({
  selector: 'nh-test-badge-host',
  standalone: true,
  imports: [NhBackgroundOperationsBadgeComponent],
  template: `
    @if (shown) {
      <nh-background-operations-badge [disabled]="disabled" [hideWhenIdle]="hideWhenIdle" (openRequested)="opened = opened + 1" />
    }`
})
class BadgeHostComponent {
  shown = true;
  disabled = false;
  hideWhenIdle = false;
  opened = 0;
}

let sequence = 0;

function operation(status: NhBackgroundOperationStatus, parentOperationId?: string): NhBackgroundOperation {
  sequence++;
  return {
    id: `00000000-0000-0000-0000-${String(sequence).padStart(12, '0')}`,
    creationDateTime: '2026-11-06T10:00:00Z',
    lastModifiedDateTime: `2026-11-06T10:00:${String(sequence % 60).padStart(2, '0')}Z`,
    operationType: 'sample.analysis',
    payloadSchemaVersion: 1,
    ownerUserId: '11111111-2222-3333-4444-555555555555',
    parentOperationId,
    status,
    queue: 'default',
    priority: 0,
    currentAttemptNumber: 1,
    version: 1,
    latestEventSequence: 0,
    attempts: [],
    steps: [],
    events: [],
    children: []
  };
}

describe('NhBackgroundOperationsBadgeComponent', () => {
  let fixture: ComponentFixture<BadgeHostComponent>;
  let host: BadgeHostComponent;
  let store: FakeBackgroundOperationStore;

  function configure(platformId: 'browser' | 'server' = 'browser'): void {
    store = new FakeBackgroundOperationStore();
    TestBed.configureTestingModule({
      imports: [BadgeHostComponent],
      providers: [
        provideTranslateService({fallbackLang: 'en'}),
        {provide: NhBackgroundOperationStore, useValue: store},
        {provide: PLATFORM_ID, useValue: platformId}
      ]
    });

    fixture = TestBed.createComponent(BadgeHostComponent);
    host = fixture.componentInstance;
  }

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function button(): HTMLButtonElement | null {
    return element().querySelector('nh-background-operations-badge button');
  }

  function liveRegion(): HTMLElement {
    return element().querySelector('.nh-operations-badge__announcement')!;
  }

  function update(): void {
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
  }

  it('counts the active root operations and names the button with that count', () => {
    configure();
    store.operations.next([
      operation('Running'),
      operation(10),
      operation('Succeeded'),
      operation('Failed'),
      operation('Running', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee')
    ]);
    fixture.detectChanges();

    expect(button()?.getAttribute('aria-label')).toBe('Background operations: 2 running');
    expect(button()?.getAttribute('title')).toBe('Background operations: 2 running');
    const count = element().querySelector('.nh-operations-badge__count')!;
    expect(count.textContent?.trim()).toBe('2');
    expect(count.getAttribute('aria-hidden')).toBe('true');
    expect(button()?.type).toBe('button');
  });

  it('says that nothing runs and hides the count while idle', () => {
    configure();
    store.operations.next([operation('Succeeded')]);
    fixture.detectChanges();

    expect(button()?.getAttribute('aria-label')).toBe('Background operations: none running');
    expect(element().querySelector('.nh-operations-badge__count')).toBeNull();
  });

  it('renders no button while idle when hideWhenIdle is set, but keeps its live region', () => {
    configure();
    host.hideWhenIdle = true;
    fixture.detectChanges();

    expect(button()).toBeNull();
    expect(liveRegion().getAttribute('aria-live')).toBe('polite');

    store.operations.next([operation('Queued')]);
    update();

    expect(button()).not.toBeNull();
  });

  it('caps the visible count', () => {
    configure();
    store.operations.next(Array.from({length: 120}, () => operation('Running')));
    fixture.detectChanges();

    expect(element().querySelector('.nh-operations-badge__count')?.textContent?.trim()).toBe('99+');
    expect(button()?.getAttribute('aria-label')).toBe('Background operations: 120 running');
  });

  it('announces a changed count politely, but not the first snapshot', () => {
    configure();
    fixture.detectChanges();
    expect(liveRegion().textContent?.trim()).toBe('');

    store.operations.next([operation('Running'), operation('Running')]);
    update();
    expect(liveRegion().textContent?.trim()).toBe('');

    store.operations.next([operation('Running'), operation('Running'), operation('Queued')]);
    update();
    expect(liveRegion().textContent?.trim()).toBe('3 background operations running');

    store.operations.next([operation('Running')]);
    update();
    expect(liveRegion().textContent?.trim()).toBe('1 background operation running');

    store.operations.next([operation('Succeeded')]);
    update();
    expect(liveRegion().textContent?.trim()).toBe('No background operations running');
  });

  it('shows that the status is unavailable while the store reports an error', () => {
    configure();
    fixture.detectChanges();

    store.error.next(new Error('offline'));
    update();
    expect(button()?.getAttribute('aria-label')).toBe('Background operations: status unavailable');
    expect(element().querySelector('.nh-operations-badge__alert')).not.toBeNull();

    store.error.next(undefined);
    update();
    expect(button()?.getAttribute('aria-label')).toBe('Background operations: none running');
  });

  it('emits openRequested on click, but not while disabled', () => {
    configure();
    fixture.detectChanges();

    button()!.click();
    expect(host.opened).toBe(1);

    host.disabled = true;
    update();
    expect(button()!.disabled).toBeTrue();
    button()!.click();
    expect(host.opened).toBe(1);
  });

  it('watches the store once while shown and stops watching when destroyed', () => {
    configure();
    fixture.detectChanges();
    expect(store.watchCalls).toBe(1);
    expect(store.watchers).toBe(1);
    expect(store.error.observed).toBeTrue();

    host.shown = false;
    update();

    expect(store.watchers).toBe(0);
    expect(store.error.observed).toBeFalse();
    expect(store.watchCalls).toBe(1);
  });

  it('does not watch the store during server rendering', () => {
    configure('server');
    fixture.detectChanges();

    expect(store.watchCalls).toBe(0);
    expect(button()?.getAttribute('aria-label')).toBe('Background operations: none running');
  });

  it('treats only unfinished statuses as active', () => {
    const active: NhBackgroundOperationStatus[] = [
      'PendingDispatch', 'Queued', 'Running', 'WaitingForChildren', 'WaitingForSignal', 'CancelRequested', 'RetryScheduled',
      0, 10, 20, 25, 27, 30, 40
    ];
    const finished: NhBackgroundOperationStatus[] = ['Succeeded', 'Failed', 'Cancelled', 'TimedOut', 100, 110, 120, 130, 999];

    expect(active.every(status => nhBackgroundOperationIsActive(status))).toBeTrue();
    expect(finished.some(status => nhBackgroundOperationIsActive(status))).toBeFalse();
  });
});

describe('NhBackgroundOperationsBadgeComponent with the real store', () => {
  it('relies on the store polling and stops it with the last watcher', fakeAsync(() => {
    const live = {
      changed$: new Subject<NhBackgroundOperationChanged>().asObservable(),
      resync$: new Subject<void>().asObservable(),
      connectionState$: of('connected'),
      start: jasmine.createSpy('start'),
      stop: jasmine.createSpy('stop')
    } as unknown as NhBackgroundOperationLiveUpdateService;
    const api = jasmine.createSpyObj<NhBackgroundOperationService>('api', ['list', 'get', 'cancel', 'retry']);
    api.list.and.callFake(() => of(new CollectionHttpResponse<NhBackgroundOperation>({items: [operation('Running')]})));
    const auth = {
      authSubject: new BehaviorSubject<NhAuthorization | undefined>(undefined),
      getAuthorization: () => undefined
    } as unknown as NhAuthService;
    const config = new NhCommonModuleConfig();
    const store = new NhBackgroundOperationStore(api, live, config, auth);

    TestBed.configureTestingModule({
      imports: [BadgeHostComponent],
      providers: [
        provideTranslateService({fallbackLang: 'en'}),
        {provide: NhBackgroundOperationStore, useValue: store}
      ]
    });
    const fixture = TestBed.createComponent(BadgeHostComponent);
    fixture.detectChanges();
    tick();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('button')?.getAttribute('aria-label')).toBe('Background operations: 1 running');
    expect(live.start).toHaveBeenCalledTimes(1);
    expect(api.list).toHaveBeenCalledTimes(1);

    const interval = Math.max(1000, config.backgroundOperations.pollingInterval);
    tick(interval);
    expect(api.list).toHaveBeenCalledTimes(2);

    fixture.componentInstance.shown = false;
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    expect(live.stop).toHaveBeenCalledTimes(1);

    tick(interval * 3);
    expect(api.list).toHaveBeenCalledTimes(2);

    store.ngOnDestroy();
  }));
});
