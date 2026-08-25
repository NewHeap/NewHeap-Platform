import { Injectable, OnDestroy } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import {
  BehaviorSubject,
  catchError,
  defer,
  EMPTY,
  exhaustMap,
  finalize,
  map,
  Observable,
  of,
  Subscription,
  timer
} from 'rxjs';
import {
  NhBackgroundOperation,
  NhBackgroundOperationChanged,
  NhBackgroundOperationCollectionHttpRequestOptions
} from '../models/background-operation.models';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhBackgroundOperationLiveUpdateService } from './nh-background-operation-live-update.service';
import { NhBackgroundOperationService } from './nh-background-operation.service';
import { NhAuthService } from './nh-auth.service';
import { INhAuthorization } from '../models/auth.models';

@Injectable({ providedIn: 'root' })
export class NhBackgroundOperationStore implements OnDestroy {
  private readonly operationsSubject = new BehaviorSubject<ReadonlyMap<string, NhBackgroundOperation>>(new Map());
  private readonly errorSubject = new BehaviorSubject<unknown | undefined>(undefined);
  private readonly subscriptions = new Subscription();
  private readonly watchedIds = new Map<string, number>();
  private readonly refreshing = new Set<string>();
  private readonly refreshAgain = new Set<string>();
  private pollingSubscription?: Subscription;
  private consumers = 0;
  private scopeGeneration = 0;
  private scopeKey: string;

  readonly operations$: Observable<readonly NhBackgroundOperation[]> = this.operationsSubject.pipe(
    map(items =>
      [...items.values()].sort((left, right) =>
        right.lastModifiedDateTime.localeCompare(left.lastModifiedDateTime)
      )
    )
  );
  readonly error$: Observable<unknown | undefined> = this.errorSubject.asObservable();
  readonly connectionState$ = this.live.connectionState$;

  constructor(
    private readonly api: NhBackgroundOperationService,
    private readonly live: NhBackgroundOperationLiveUpdateService,
    private readonly config: NhCommonModuleConfig,
    private readonly auth: NhAuthService
  ) {
    this.scopeKey = this.getScopeKey(this.auth.getAuthorization());

    this.subscriptions.add(
      this.auth.authSubject.subscribe(authorization => this.onAuthorizationChanged(authorization))
    );
    this.subscriptions.add(
      this.live.changed$.subscribe(change => this.onLiveChange(change))
    );
    this.subscriptions.add(
      this.live.resync$.subscribe(() => {
        void this.refreshList();
        for (const id of this.watchedIds.keys()) {
          this.refreshOperation(id);
        }
      })
    );
  }

  watchAll(): Observable<readonly NhBackgroundOperation[]> {
    return defer(() => {
      this.start();
      return this.operations$.pipe(finalize(() => this.stop()));
    });
  }

  watch(operationId: string): Observable<NhBackgroundOperation | undefined> {
    return defer(() => {
      this.start();
      this.watchedIds.set(operationId, (this.watchedIds.get(operationId) ?? 0) + 1);
      this.refreshOperation(operationId);
      return this.operationsSubject.pipe(
        map(operations => operations.get(operationId)),
        finalize(() => {
          const watchers = (this.watchedIds.get(operationId) ?? 1) - 1;
          if (watchers <= 0) {
            this.watchedIds.delete(operationId);
          } else {
            this.watchedIds.set(operationId, watchers);
          }
          this.stop();
        })
      );
    });
  }

  start(): void {
    this.consumers++;
    if (this.consumers !== 1) {
      return;
    }

    this.live.start();
    this.pollingSubscription = timer(
      0,
      Math.max(1000, this.config.backgroundOperations.pollingInterval)
    )
      .pipe(exhaustMap(() => this.loadSnapshot()))
      .subscribe();
  }

  stop(): void {
    if (this.consumers === 0) {
      return;
    }

    this.consumers--;
    if (this.consumers === 0) {
      this.pollingSubscription?.unsubscribe();
      this.pollingSubscription = undefined;
      this.live.stop();
    }
  }

  refreshList(): Promise<void> {
    return new Promise(resolve => this.loadList().subscribe({ complete: resolve }));
  }

  cancel(operationId: string): Observable<NhBackgroundOperation> {
    const generation = this.scopeGeneration;
    return this.api.cancel(operationId).pipe(
      map(operation => this.mergeDetail(operation, generation))
    );
  }

  retry(operationId: string): Observable<NhBackgroundOperation> {
    const generation = this.scopeGeneration;
    return this.api.retry(operationId).pipe(
      map(operation => this.mergeDetail(operation, generation))
    );
  }

  ngOnDestroy(): void {
    this.pollingSubscription?.unsubscribe();
    this.subscriptions.unsubscribe();
    if (this.consumers > 0) {
      this.consumers = 0;
      this.live.stop();
    }
    this.operationsSubject.complete();
    this.errorSubject.complete();
  }

  private loadList(): Observable<void> {
    const generation = this.scopeGeneration;
    const request = new NhBackgroundOperationCollectionHttpRequestOptions({
      page: 1,
      itemsPerPage: this.config.backgroundOperations.listPageSize
    });
    return this.api.list(request).pipe(
      map(response => {
        if (generation !== this.scopeGeneration) {
          return;
        }

        this.replaceListSnapshot(response.items ?? []);
        this.errorSubject.next(undefined);
      }),
      catchError(error => {
        if (generation === this.scopeGeneration) {
          this.errorSubject.next(error);
        }

        return of(undefined);
      })
    );
  }

  private loadSnapshot(): Observable<void> {
    return this.loadList().pipe(
      map(() => {
        for (const id of this.watchedIds.keys()) {
          this.refreshOperation(id);
        }
      })
    );
  }

  private onLiveChange(change: NhBackgroundOperationChanged): void {
    const existing = this.operationsSubject.value.get(change.operationId);
    if (existing && existing.version >= change.version && existing.latestEventSequence >= change.latestEventSequence) {
      return;
    }
    this.refreshOperation(change.operationId);
  }

  private refreshOperation(operationId: string): void {
    const generation = this.scopeGeneration;
    const refreshKey = `${generation}:${operationId}`;
    if (this.refreshing.has(refreshKey)) {
      this.refreshAgain.add(refreshKey);
      return;
    }

    this.refreshing.add(refreshKey);
    const existing = this.operationsSubject.value.get(operationId);
    this.api
      .get(operationId, existing?.latestEventSequence)
      .pipe(
        catchError(error => {
          if (generation === this.scopeGeneration) {
            if (this.shouldRemoveAfterError(error)) {
              this.removeOperation(operationId);
            }

            this.errorSubject.next(error);
          }

          return EMPTY;
        }),
        finalize(() => {
          this.refreshing.delete(refreshKey);
          if (this.refreshAgain.delete(refreshKey) && generation === this.scopeGeneration) {
            this.refreshOperation(operationId);
          }
        })
      )
      .subscribe(operation => {
        if (generation === this.scopeGeneration) {
          this.mergeDetail(operation, generation);
          this.errorSubject.next(undefined);
        }
      });
  }

  private mergeSummary(incoming: NhBackgroundOperation): NhBackgroundOperation {
    const existing = this.operationsSubject.value.get(incoming.id);
    if (existing && incoming.version < existing.version) {
      return existing;
    }

    const merged = existing
      ? {
          ...incoming,
          attempts: existing.attempts,
          steps: existing.steps,
          children: existing.children,
          events: existing.events
        }
      : incoming;
    this.setOperation(merged);
    return merged;
  }

  private mergeDetail(
    incoming: NhBackgroundOperation,
    generation: number = this.scopeGeneration
  ): NhBackgroundOperation {
    if (generation !== this.scopeGeneration) {
      return incoming;
    }

    const existing = this.operationsSubject.value.get(incoming.id);
    if (existing && incoming.version < existing.version) {
      return existing;
    }

    const events = new Map<number, typeof incoming.events[number]>();
    for (const event of existing?.events ?? []) {
      events.set(event.sequence, event);
    }
    for (const event of incoming.events ?? []) {
      events.set(event.sequence, event);
    }
    const merged = {
      ...incoming,
      events: [...events.values()].sort((left, right) => left.sequence - right.sequence)
    };
    this.setOperation(merged);
    return merged;
  }

  private setOperation(operation: NhBackgroundOperation): void {
    const operations = new Map(this.operationsSubject.value);
    operations.set(operation.id, operation);
    this.operationsSubject.next(operations);
  }

  private replaceListSnapshot(incoming: readonly NhBackgroundOperation[]): void {
    const current = this.operationsSubject.value;
    const next = new Map<string, NhBackgroundOperation>();

    for (const watchedId of this.watchedIds.keys()) {
      const watched = current.get(watchedId);
      if (watched) {
        next.set(watchedId, watched);
      }
    }

    for (const operation of incoming) {
      const existing = current.get(operation.id);
      if (existing && operation.version < existing.version) {
        next.set(operation.id, existing);
        continue;
      }

      next.set(
        operation.id,
        existing
          ? {
              ...operation,
              attempts: existing.attempts,
              steps: existing.steps,
              children: existing.children,
              events: existing.events
            }
          : operation
      );
    }

    this.operationsSubject.next(next);
  }

  private onAuthorizationChanged(authorization: INhAuthorization | undefined): void {
    const nextScopeKey = this.getScopeKey(authorization);
    if (nextScopeKey === this.scopeKey) {
      return;
    }

    this.scopeKey = nextScopeKey;
    this.scopeGeneration++;
    this.operationsSubject.next(new Map());
    this.errorSubject.next(undefined);

    if (this.consumers > 0) {
      void this.refreshList();
      for (const id of this.watchedIds.keys()) {
        this.refreshOperation(id);
      }
    }
  }

  private getScopeKey(authorization: INhAuthorization | undefined): string {
    const userId = authorization?.user?.id ?? 'anonymous';
    const divisionId = authorization?.activeDivision?.id ?? 'global';
    return `${userId}:${divisionId}`;
  }

  private shouldRemoveAfterError(error: unknown): boolean {
    return error instanceof HttpErrorResponse
      && [401, 403, 404].includes(error.status);
  }

  private removeOperation(operationId: string): void {
    if (!this.operationsSubject.value.has(operationId)) {
      return;
    }

    const operations = new Map(this.operationsSubject.value);
    operations.delete(operationId);
    this.operationsSubject.next(operations);
  }
}
