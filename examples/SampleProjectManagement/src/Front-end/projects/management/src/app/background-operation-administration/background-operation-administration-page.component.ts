import { DatePipe, DecimalPipe, DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  CollectionHttpResponse,
  NhBackgroundOperationAdministration,
  NhBackgroundOperationAdministrationCollectionHttpRequestOptions,
  NhBackgroundOperationAdministrationService,
  NhBackgroundOperationProgressComponent,
  NhBackgroundOperationStatus,
  NhCommonModuleConfig,
  nhBackgroundOperationStatusName,
  nhBackgroundOperationTranslationSegment
} from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import {
  catchError,
  combineLatest,
  debounceTime,
  distinctUntilChanged,
  filter as filterEvents,
  finalize,
  fromEvent,
  map,
  merge,
  Observable,
  of,
  startWith,
  Subject,
  switchMap,
  timer
} from 'rxjs';

export type BackgroundOperationAdministrationFilter = 'all' | 'attention' | 'active' | 'succeeded';

type LoadFailure = 'forbidden' | 'unavailable' | 'failed';

interface ListState {
  status: 'loading' | 'loaded' | LoadFailure;
  operations: NhBackgroundOperationAdministration[];
  totalCount: number;
}

interface DetailState {
  status: 'loading' | 'loaded' | 'not-found' | LoadFailure;
  operation?: NhBackgroundOperationAdministration;
}

/**
 * Page size of the administration list. The server caps pages at
 * `NhBackgroundOperationAdministrationController.MaxItemsPerPage`.
 */
const PAGE_SIZE = 25;

const ATTENTION_STATUSES: NhBackgroundOperationStatus[] = ['Failed', 'TimedOut', 'Cancelled'];
const ACTIVE_STATUSES: NhBackgroundOperationStatus[] = [
  'PendingDispatch',
  'Queued',
  'Running',
  'WaitingForChildren',
  'WaitingForSignal',
  'CancelRequested',
  'RetryScheduled'
];

/**
 * Administrator overview of every user's background operations in the active
 * division. It uses the cross-owner administration endpoints and polls, because
 * live updates are delivered per owner.
 */
@Component({
  selector: 'app-background-operation-administration-page',
  standalone: true,
  imports: [
    DatePipe,
    DecimalPipe,
    FormsModule,
    RouterLink,
    TranslateModule,
    NhBackgroundOperationProgressComponent
  ],
  templateUrl: './background-operation-administration-page.component.html',
  styleUrl: './background-operation-administration-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class BackgroundOperationAdministrationPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly administration = inject(NhBackgroundOperationAdministrationService);
  private readonly config = inject(NhCommonModuleConfig);
  private readonly document = inject(DOCUMENT);
  private readonly refresh$ = new Subject<void>();

  readonly filters: BackgroundOperationAdministrationFilter[] = ['attention', 'active', 'succeeded', 'all'];
  readonly filter = signal<BackgroundOperationAdministrationFilter>('attention');
  readonly search = signal('');
  readonly page = signal(1);
  readonly actionPending = signal(false);
  readonly actionFailed = signal(false);

  readonly selectedOperationId = toSignal(
    this.route.paramMap.pipe(map(parameters => parameters.get('id') ?? undefined)),
    { initialValue: undefined }
  );

  readonly list = toSignal(
    combineLatest([
      toObservable(this.filter),
      toObservable(this.search).pipe(debounceTime(250), distinctUntilChanged()),
      toObservable(this.page)
    ]).pipe(
      switchMap(([filter, search, page]) => this.poll(
        () => this.administration.list(this.request(filter, search, page)),
        (response: CollectionHttpResponse<NhBackgroundOperationAdministration>): ListState => ({
          status: 'loaded',
          operations: response.items ?? [],
          totalCount: response.totalCount ?? 0
        }),
        (error): ListState => ({ status: this.failure(error), operations: [], totalCount: 0 })
      ).pipe(startWith<ListState>({ status: 'loading', operations: [], totalCount: 0 })))
    ),
    { initialValue: { status: 'loading', operations: [], totalCount: 0 } as ListState }
  );

  readonly detail = toSignal(
    toObservable(this.selectedOperationId).pipe(
      distinctUntilChanged(),
      switchMap(id => {
        if (!id) {
          return of<DetailState | undefined>(undefined);
        }

        return this.poll(
          () => this.administration.get(id),
          (operation): DetailState => ({ status: 'loaded', operation }),
          (error): DetailState => ({
            status: error instanceof HttpErrorResponse && error.status === 404 ? 'not-found' : this.failure(error)
          })
        ).pipe(startWith<DetailState>({ status: 'loading' }));
      })
    ),
    { initialValue: undefined }
  );

  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.list().totalCount / PAGE_SIZE)));

  setFilter(filter: BackgroundOperationAdministrationFilter): void {
    this.filter.set(filter);
    this.page.set(1);
  }

  setSearch(search: string): void {
    this.search.set(search);
    this.page.set(1);
  }

  goToPage(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pageCount()));
  }

  refresh(): void {
    this.refresh$.next();
  }

  cancel(operationId: string): void {
    this.runAction(this.administration.cancel(operationId));
  }

  retry(operationId: string): void {
    this.runAction(this.administration.retry(operationId));
  }

  statusKey(status: NhBackgroundOperationStatus): string {
    return `nh-background-operations.status.${nhBackgroundOperationTranslationSegment(nhBackgroundOperationStatusName(status))}`;
  }

  statusTone(status: NhBackgroundOperationStatus): 'danger' | 'active' | 'success' | 'neutral' {
    const name = nhBackgroundOperationStatusName(status) as NhBackgroundOperationStatus;
    if (ATTENTION_STATUSES.includes(name)) {
      return 'danger';
    }
    if (ACTIVE_STATUSES.includes(name)) {
      return 'active';
    }
    return name === 'Succeeded' ? 'success' : 'neutral';
  }

  percentage(value?: number): number {
    return Math.max(0, Math.min(100, value ?? 0));
  }

  ownerLabel(operation: NhBackgroundOperationAdministration): string {
    return operation.ownerDisplayName || operation.ownerUserId;
  }

  private request(
    filter: BackgroundOperationAdministrationFilter,
    search: string,
    page: number
  ): NhBackgroundOperationAdministrationCollectionHttpRequestOptions {
    const request = new NhBackgroundOperationAdministrationCollectionHttpRequestOptions({
      page,
      itemsPerPage: PAGE_SIZE,
      search: search.trim() || undefined
    });
    if (filter === 'attention') {
      request.isIn('status', ATTENTION_STATUSES);
    } else if (filter === 'active') {
      request.isIn('status', ACTIVE_STATUSES);
    } else if (filter === 'succeeded') {
      request.equals('status', 'Succeeded');
    }

    return request.orderDesc('lastModifiedDateTime');
  }

  /**
   * Loads immediately, on every polling interval while the page is visible, when it
   * becomes visible again and on explicit refresh. A failed request becomes a state
   * instead of ending the polling stream.
   */
  private poll<T, TState>(
    load: () => Observable<T>,
    toState: (value: T) => TState,
    toFailure: (error: unknown) => TState
  ): Observable<TState> {
    const visible = () => this.document.visibilityState !== 'hidden';
    return merge(
      timer(0, this.config.backgroundOperations.pollingInterval).pipe(filterEvents(visible)),
      fromEvent(this.document, 'visibilitychange').pipe(filterEvents(visible)),
      this.refresh$
    ).pipe(
      switchMap(() => load().pipe(
        map(toState),
        catchError(error => of(toFailure(error)))
      ))
    );
  }

  private failure(error: unknown): LoadFailure {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401 || error.status === 403) {
        return 'forbidden';
      }
      if (error.status === 404) {
        return 'unavailable';
      }
    }

    return 'failed';
  }

  private runAction(action: Observable<NhBackgroundOperationAdministration>): void {
    if (this.actionPending()) {
      return;
    }

    this.actionPending.set(true);
    this.actionFailed.set(false);
    action.pipe(finalize(() => this.actionPending.set(false))).subscribe({
      next: () => this.refresh(),
      error: () => this.actionFailed.set(true)
    });
  }
}
