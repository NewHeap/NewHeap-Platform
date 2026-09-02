import { AsyncPipe, DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  NhApiService,
  NhBackgroundOperation,
  NhBackgroundOperationProgressComponent,
  NhBackgroundOperationStatus,
  NhBackgroundOperationStore,
  NhCommonModuleConfig,
  nhBackgroundOperationStatusName,
  nhBackgroundOperationTranslationSegment
} from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import {
  distinctUntilChanged,
  finalize,
  map,
  Observable,
  of,
  switchMap
} from 'rxjs';

interface ProjectPortfolioAnalysisMutateModel {
  idempotencyKey: string;
  passes: number;
  delayPerItemMilliseconds: number;
  failFirstAttempt: boolean;
}

@Component({
  selector: 'app-background-operations-page',
  standalone: true,
  imports: [
    AsyncPipe,
    DatePipe,
    DecimalPipe,
    FormsModule,
    RouterLink,
    TranslateModule,
    NhBackgroundOperationProgressComponent
  ],
  templateUrl: './background-operations-page.component.html',
  styleUrl: './background-operations-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class BackgroundOperationsPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(NhApiService);
  private readonly config = inject(NhCommonModuleConfig);
  readonly store = inject(NhBackgroundOperationStore);

  readonly operations$ = this.store.watchAll();
  readonly connectionState$ = this.store.connectionState$;
  readonly selectedOperation$: Observable<NhBackgroundOperation | undefined> = this.route.paramMap.pipe(
    map(parameters => parameters.get('id')),
    distinctUntilChanged(),
    switchMap(id => (id ? this.store.watch(id) : of(undefined)))
  );
  readonly hasSelectedOperation$ = this.route.paramMap.pipe(map(parameters => parameters.has('id')));
  readonly starting = signal(false);
  readonly actionPending = signal(false);
  readonly actionFailed = signal(false);
  readonly failFirstAttempt = signal(false);

  startAnalysis(): void {
    if (this.starting()) {
      return;
    }

    this.starting.set(true);
    this.actionFailed.set(false);
    const model: ProjectPortfolioAnalysisMutateModel = {
      idempotencyKey: globalThis.crypto?.randomUUID?.() ?? `sample-${Date.now()}`,
      passes: 20,
      delayPerItemMilliseconds: 60,
      failFirstAttempt: this.failFirstAttempt()
    };
    this.api
      .post<NhBackgroundOperation>(
        this.joinUrl(
          this.config.apiBaseUrl,
          '/operations-samples/background-operations/project-portfolio-analysis'
        ),
        model
      )
      .pipe(finalize(() => this.starting.set(false)))
      .subscribe({
        next: operation => {
          void this.store.refreshList();
          void this.router.navigate([
            this.applicationRoot(),
            'background-operations',
            operation.id
          ]);
        },
        error: () => this.actionFailed.set(true)
      });
  }

  cancel(operationId: string): void {
    this.runAction(this.store.cancel(operationId));
  }

  retry(operationId: string): void {
    this.runAction(this.store.retry(operationId));
  }

  statusKey(status: NhBackgroundOperationStatus): string {
    return `nh-background-operations.status.${nhBackgroundOperationTranslationSegment(nhBackgroundOperationStatusName(status))}`;
  }

  percentage(value?: number): number {
    return Math.max(0, Math.min(100, value ?? 0));
  }

  private runAction(action: Observable<NhBackgroundOperation>): void {
    if (this.actionPending()) {
      return;
    }

    this.actionPending.set(true);
    this.actionFailed.set(false);
    action.pipe(finalize(() => this.actionPending.set(false))).subscribe({
      error: () => this.actionFailed.set(true)
    });
  }

  private applicationRoot(): 'management' | 'workspace' {
    return this.router.url.startsWith('/workspace') ? 'workspace' : 'management';
  }

  private joinUrl(base: string, suffix: string): string {
    return `${base.replace(/\/$/, '')}/${suffix.replace(/^\//, '')}`;
  }
}
