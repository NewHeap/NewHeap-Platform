import { CommonModule } from '@angular/common';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRouteSnapshot, Router } from '@angular/router';
import {
  DefaultMultiSelectSettings,
  DefaultMultiSelectTexts,
  HttpDownloadRequestOptions,
  HttpRequestOptions,
  HttpRequestOptionsArrayBuffer,
  NhApiService,
  NhAppService,
  NhCommonModule,
  NhCommonModuleConfig,
  NhConfigCommonService,
  NhContextMenu,
  NhContextMenuItem,
  NhContextMenuService,
  NhFormDropDownSettings,
  NhFormHelper,
  NhHeadService,
  NhHttpUtil,
  NhJsonLdDataItem,
  NhJsonLdService,
  NhMetaService,
  NhModalLoadingComponent,
  NhModalOptions,
  NhModalService,
  NhPageService,
  NhPageSettings,
  NhRouterService,
  NhSentryService,
  NhSentryTraceService,
  NhTitleService,
  PreConnectUrlItem,
  PreLoadUrlItem,
  REVIEW_AGGREGATE_RATING_KEY,
  REVIEW_KEY
} from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import { forkJoin, map, of, switchMap } from 'rxjs';
import { ProjectCodeInputComponent } from '../project-code-input/project-code-input.component';

@Component({
  selector: 'app-platform-playground',
  standalone: true,
  imports: [CommonModule, FormsModule, ReactiveFormsModule, TranslateModule, NhCommonModule, ProjectCodeInputComponent],
  templateUrl: './platform-playground.component.html',
  styleUrl: './platform-playground.component.scss'
})
export class PlatformPlaygroundComponent {
  private readonly http = inject(HttpClient);
  private readonly api = inject(NhApiService);
  private readonly modalService = inject(NhModalService);
  private readonly titleService = inject(NhTitleService);
  private readonly metaService = inject(NhMetaService);
  private readonly headService = inject(NhHeadService);
  private readonly jsonLdService = inject(NhJsonLdService);
  private readonly contextMenuService = inject(NhContextMenuService);
  private readonly configService = inject(NhConfigCommonService);
  private readonly appService = inject(NhAppService);
  private readonly pageService = inject(NhPageService);
  private readonly routerService = inject(NhRouterService);
  private readonly router = inject(Router);
  private readonly sentryService = inject(NhSentryService);
  private readonly sentryTraceService = inject(NhSentryTraceService);
  private readonly moduleConfig = inject(NhCommonModuleConfig);
  private readonly endpoint = '/api/library-samples/http';
  private readonly cacheDivisionId = '11111111-1111-1111-1111-111111111111';
  private readonly cacheEndpoint = `/api/library-samples/cache/project-summary/${this.cacheDivisionId}`;
  private sentryHooksRegistered = false;

  readonly projectCode = new FormControl('NHP', { nonNullable: true });
  readonly projectForm = new FormGroup({ projectCode: this.projectCode });
  readonly memberIds = new FormControl<string[]>([], { nonNullable: true });
  readonly memberSettings = new NhFormDropDownSettings({
    lazyLoad: false,
    loadLambda: () => of([
      { id: '11111111-1111-1111-1111-111111111111', name: 'Ada Lovelace' },
      { id: '22222222-2222-2222-2222-222222222222', name: 'Grace Hopper' },
      { id: '33333333-3333-3333-3333-333333333333', name: 'Edsger Dijkstra' }
    ]),
    multiSelectSettings: new DefaultMultiSelectSettings({
      selectionLimit: 0,
      showCheckAll: true,
      showUncheckAll: true,
      closeOnSelect: false,
      isLazyLoad: false
    }),
    multiSelectTexts: new DefaultMultiSelectTexts({
      checkAll: 'Select all',
      uncheckAll: 'Deselect all',
      checked: 'lid geselecteerd',
      checkedPlural: 'members selected',
      searchPlaceholder: 'Search members',
      searchEmptyResult: 'No members found',
      searchNoRenderText: 'Type to search',
      defaultTitle: 'Choose project members',
      allSelected: 'All members selected'
    })
  });

  readonly result = signal('Choose an action to execute the concrete library call.');
  readonly debouncedValue = signal('');
  readonly buttonDebounceCount = signal(0);
  readonly teardownProbeVisible = signal(true);
  readonly teardownProbeValue = signal('begin');
  readonly teardownEmissionCount = signal(0);
  readonly jsonLdPreview = signal('{}');
  readonly jsonLdDocument = signal<unknown>(null);
  readonly headDirectiveVisible = signal(false);
  readonly duplicateSubscribers = signal(0);
  readonly networkExecutions = signal(0);
  readonly inFlightDeduplicationEnabled = this.moduleConfig.http.deduplicateGetRequests;
  readonly navigationPreview = signal('{}');
  readonly pageStatePreview = signal('{}');
  readonly sentryPreview = signal('{}');

  loadCachedProjectSummary(): void {
    const options = new HttpRequestOptions();
    this.api.get<ProjectCacheSample>(this.cacheEndpoint, options).pipe(
      switchMap(first => this.api.get<ProjectCacheSample>(this.cacheEndpoint, options).pipe(
        map(second => ({ first, second }))
      ))
    ).subscribe({
      next: ({ first, second }) => this.result.set(JSON.stringify({
        cacheKey: first.cacheKey,
        firstGeneratedAtUtc: first.generatedAtUtc,
        secondGeneratedAtUtc: second.generatedAtUtc,
        cacheHit: first.generatedAtUtc === second.generatedAtUtc,
        projectCount: second.projectCount
      }, null, 2)),
      error: error => this.result.set(
        error?.status === 401 || error?.status === 403
          ? 'Sign in with app.project.view to run the cache hit and miss sample.'
          : this.errorText(error)
      )
    });
  }

  invalidateCachedProjectSummary(): void {
    this.api.delete<{ cacheKey: string; invalidated: boolean }>(
      this.cacheEndpoint,
      new HttpRequestOptions()
    ).subscribe({
      next: response => this.result.set(JSON.stringify(response, null, 2)),
      error: error => this.result.set(
        error?.status === 401 || error?.status === 403
          ? 'Invalidation requires app.project.manage.'
          : this.errorText(error)
      )
    });
  }

  loadProjectChunks(): void {
    this.api.get<ProjectChunksSampleResponse>(
      '/api/library-samples/database/project-chunks',
      new HttpRequestOptions({ params: new HttpParams().set('chunkSize', 2) })
    ).subscribe({
      next: response => this.result.set(JSON.stringify(response, null, 2)),
      error: error => this.result.set(
        error?.status === 401 || error?.status === 403
          ? 'Sign in with the project view permission before running the ChunkAsync sample.'
          : this.errorText(error)
      )
    });
  }

  loadText(): void {
    this.api.getText(`${this.endpoint}/text`, new HttpRequestOptions({ withCredentials: false }))
      .subscribe({
        next: value => this.result.set(value),
        error: error => this.result.set(this.errorText(error))
      });
  }

  loadBinary(): void {
    const options = new HttpRequestOptionsArrayBuffer({ withCredentials: false });
    this.http.get(`${this.endpoint}/binary`, { ...options, responseType: 'arraybuffer' as const }).subscribe({
      next: bytes => this.result.set(`${bytes.byteLength} bytes: ${new TextDecoder().decode(bytes)}`),
      error: error => this.result.set(this.errorText(error))
    });
  }

  downloadCsv(): void {
    this.api.downloadResponse(
      `${this.endpoint}/download`,
      new HttpDownloadRequestOptions({ withCredentials: false })
    ).subscribe({
      next: response => {
        const disposition = response.headers.get('content-disposition') ?? '';
        const filename = NhHttpUtil.filenameFromContentDisposition(disposition) || 'project-export.csv';
        const href = URL.createObjectURL(response.body!);
        const anchor = document.createElement('a');
        anchor.href = href;
        anchor.download = filename;
        anchor.click();
        URL.revokeObjectURL(href);
        this.result.set(`Download started: ${filename} (${response.body?.size ?? 0} bytes)`);
      },
      error: error => this.result.set(this.errorText(error))
    });
  }

  echoEncodedQuery(): void {
    const value = `project ${this.projectCode.value} & planning/roadmap`;
    const options = new HttpRequestOptions({
      withCredentials: false,
      params: new HttpParams().set('value', value)
    });
    this.api.get<{ value: string; length: number }>(`${this.endpoint}/query`, options)
      .subscribe({
        next: response => this.result.set(JSON.stringify(response, null, 2)),
        error: error => this.result.set(this.errorText(error))
      });
  }

  demonstrateDeduplication(): void {
    this.duplicateSubscribers.set(0);
    this.networkExecutions.set(0);
    const url = `${this.endpoint}/deduplicated?sample=${Date.now()}`;
    const options = new HttpRequestOptions({ withCredentials: false });

    forkJoin([
      this.api.get<DeduplicatedGetSample>(url, options),
      this.api.get<DeduplicatedGetSample>(url, options)
    ]).subscribe({
      next: responses => {
        this.duplicateSubscribers.set(responses.length);
        this.networkExecutions.set(new Set(responses.map(response => response.executionId)).size);
        this.result.set(JSON.stringify({
          subscribers: responses.length,
          networkExecutions: this.networkExecutions(),
          executionIds: responses.map(response => response.executionId),
          deduplicated: this.networkExecutions() === 1
        }, null, 2));
      },
      error: error => this.result.set(this.errorText(error))
    });
  }

  loadObservability(): void {
    this.api.get<unknown>(
      '/api/library-samples/observability',
      new HttpRequestOptions({ withCredentials: false })
    ).subscribe({
      next: response => this.result.set(JSON.stringify(response, null, 2)),
      error: error => this.result.set(this.errorText(error))
    });
  }

  async showLoadingModal(): Promise<void> {
    const modal = this.modalService.open(
      NhModalLoadingComponent,
      new NhModalOptions({ title: 'Processing projects', closeable: false }),
      { information: 'The bulk update is running safely…' }
    );
    await this.delay(650);
    modal.close();
    this.result.set('The loading modal also closed after completion.');
  }

  applySeo(): void {
    this.clearSeo();
    this.titleService.setTitle('Sample Project Management | NewHeap');
    this.metaService.updateTag({
      name: 'description',
      content: 'Executable examples for the NewHeap Platform libraries.'
    });
    this.headService.addLinkTag(
      'canonical',
      `${window.location.origin}/management/cases`,
      false,
      undefined,
      [{ key: 'data-sample-project-management', value: 'true' }]
    );
    this.headService.addPreConnectUrl(new PreConnectUrlItem({
      url: window.location.origin,
      withCrossOrigin: false,
      additionalAttributes: [{ key: 'data-sample-project-management', value: 'true' }]
    }));
    this.headService.addPreLoadUrl(new PreLoadUrlItem({
      url: '/assets/sample-project-management.css',
      as: 'style',
      type: 'text/css',
      additionalAttributes: [{ key: 'data-sample-project-management', value: 'true' }]
    }));

    const projectId = 'sample-project-management';
    const aggregateRatingKey = REVIEW_AGGREGATE_RATING_KEY(projectId);
    const reviewKey = REVIEW_KEY(projectId);
    this.jsonLdService.addItem(new NhJsonLdDataItem({
      id: projectId,
      placeholderKeys: [aggregateRatingKey, reviewKey],
      data: {
        '@type': 'SoftwareApplication',
        name: 'Sample Project Management',
        applicationCategory: 'DeveloperApplication',
        operatingSystem: 'Web',
        aggregateRating: aggregateRatingKey,
        review: reviewKey
      }
    }));
    this.jsonLdService.addItem(new NhJsonLdDataItem({
      id: 'sample-aggregate-rating',
      resolvePlaceholderKey: aggregateRatingKey,
      data: { '@type': 'AggregateRating', ratingValue: 5, reviewCount: 1 }
    }));
    this.jsonLdService.addItem(new NhJsonLdDataItem({
      id: 'sample-review',
      resolvePlaceholderKey: reviewKey,
      data: { '@type': 'Review', reviewBody: 'Every library case has executable documentation.' }
    }));

    const document = this.jsonLdService.build();
    this.jsonLdDocument.set(document);
    this.jsonLdPreview.set(JSON.stringify(document, null, 2));
    this.headDirectiveVisible.set(true);
    this.result.set('Services, the nhToHead directive, and the nh-json-ld component updated the document head.');
  }

  clearSeo(): void {
    this.metaService.removeTag("name='description'");
    document.head
      .querySelectorAll('[data-sample-project-management="true"]')
      .forEach(element => element.remove());
    this.jsonLdService.clear();
    this.jsonLdDocument.set(null);
    this.jsonLdPreview.set('{}');
    this.headDirectiveVisible.set(false);
  }

  async inspectNavigationModel(): Promise<void> {
    let leaf: ActivatedRouteSnapshot = this.router.routerState.snapshot.root;
    while (leaf.firstChild) leaf = leaf.firstChild;

    const breadcrumb = await this.pageService.getBreadcrumb(leaf);
    const sitemap = this.routerService.createSitemap();
    this.navigationPreview.set(JSON.stringify({
      activeUrl: this.router.url,
      casesUrl: this.routerService.createUrlForNavigationItem({ id: 'cases' }),
      breadcrumb: breadcrumb.items.map(item => item.text),
      sitemap: sitemap.entries
    }, null, 2));
  }

  async demonstratePageState(): Promise<void> {
    const settings = new NhPageSettings({
      title: 'Platform lifecycle sample',
      description: 'NhPageService manages page metadata and transferable page state.',
      pageData: { projectCode: this.projectCode.value, loadedAtUtc: new Date().toISOString() }
    });
    this.pageService.activePageSettings = settings;
    await this.pageService.flushMeta(settings);
    this.appService.setStateTransferData('sample-project-management.playground', settings.pageData);
    this.pageService.updateTransferState();

    this.pageStatePreview.set(JSON.stringify({
      pageData: this.appService.getStateTransferData('sample-project-management.playground'),
      platformBrowser: this.appService.isPlatformBrowser(),
      originatedFromServer: this.appService.originatedFromServer(),
      browserInitial: this.appService.isPlatformBrowserInitial(),
      appStable: this.appService.isAppStable(),
      activeTitle: this.pageService.activePageSettings.title
    }, null, 2));
  }

  async clearPageState(): Promise<void> {
    await this.pageService.clear();
    this.pageStatePreview.set(JSON.stringify({ cleared: true, activePageSettings: this.pageService.activePageSettings }, null, 2));
  }

  demonstrateFormHelpers(): void {
    this.projectCode.setErrors({ sampleServerError: true });
    const before = this.projectCode.invalid;
    NhFormHelper.clearErrors(this.projectForm);
    this.result.set(JSON.stringify({
      provider: 'MakeProvider(ProjectCodeInputComponent)',
      accessor: 'AbstractValueAccessor',
      invalidBeforeClear: before,
      invalidAfterClear: this.projectCode.invalid,
      disabledStateSupported: true
    }, null, 2));
  }

  demonstrateSentry(): void {
    if (!this.sentryHooksRegistered) {
      this.sentryService.registerHookBeforeSend(event => ({ ...event, tags: { ...event.tags, sample: 'project-management' } }));
      this.sentryService.registerHookBeforeSendLog(log => log);
      this.sentryService.registerHookBeforeSendSpan(span => ({ ...span, data: { ...span.data, sample_case: 'SPM-159' } }));
      this.sentryService.registerHookBeforeSendTransaction(event => event);
      this.sentryService.registerHookBeforeBreadcrumb(breadcrumb => ({ ...breadcrumb, category: 'sample-project-management' }));
      this.sentryHooksRegistered = true;
    }

    this.sentryService.sentry.addBreadcrumb({
      category: 'sample-project-management',
      message: 'Frontend observability sample executed',
      level: 'info'
    });
    this.sentryService.sentry.startSpan(
      { name: 'platform-playground.sentry', op: 'ui.sample', attributes: { 'sample.case': 'SPM-159' } },
      () => undefined
    );
    const capturedEventId = this.sentryService.sentry.captureException(
      new Error('Intentional local SampleProjectManagement observability error'),
      { tags: { sample_case: 'SPM-159' } }
    );

    const sentryConfig = this.moduleConfig.errorLogging.sentry;
    this.sentryPreview.set(JSON.stringify({
      errorHandlerRegisteredByNhCommonModule: true,
      hooksRegistered: 5,
      traceServiceActive: !!this.sentryTraceService.sentryTraceService,
      release: sentryConfig.options.release,
      environment: sentryConfig.options.environment,
      transportEnabled: sentryConfig.options.enabled,
      capturedEventId,
      authEnrichmentConfigured: sentryConfig.beforeSendAddAuthServiceInformation,
      note: 'Transport is deliberately disabled in the sample; hooks, user enrichment, and spans still use the same configuration.'
    }, null, 2));
  }

  openContextMenu(event: MouseEvent): void {
    event.preventDefault();
    this.contextMenuService.open(
      NhContextMenu.fromEvent(event).withItems([
      new NhContextMenuItem({ title: 'Open project', onClick: async () => this.result.set('Context action: open project') }),
        new NhContextMenuItem({ type: 'divider' }),
      new NhContextMenuItem({ title: 'Copy project code', onClick: async () => {
          await navigator.clipboard?.writeText(this.projectCode.value);
        this.result.set(`Context action: copied ${this.projectCode.value}`);
        } })
      ])
    );
  }

  async switchLanguage(): Promise<void> {
    const current = this.configService.getConfig();
    const languageCode = current.languageCode === 'nl' ? 'en' : 'nl';
    await this.configService.changeLanguage(languageCode);
    this.result.set(`Runtime configuration changed to ${languageCode}; NhTranslateBrowserLoader uses TransferState and HTTP fallback.`);
  }

  toggleControl(): void {
    this.projectCode.disabled ? this.projectCode.enable() : this.projectCode.disable();
  }

  debounce(value: string): void {
    this.debouncedValue.set(value);
  }

  buttonDebounced(): void {
    this.buttonDebounceCount.update(value => value + 1);
  }

  recordTeardownEmission(): void {
    this.teardownEmissionCount.update(value => value + 1);
  }

  async demonstrateDebounceTeardown(): Promise<void> {
    this.teardownProbeVisible.set(true);
    await this.delay(0);
    const emissionsBefore = this.teardownEmissionCount();
    this.teardownProbeValue.set(`scheduled-${Date.now()}`);
    await this.delay(50);
    this.teardownProbeVisible.set(false);
    await this.delay(450);
    const cancelled = this.teardownEmissionCount() === emissionsBefore;
    this.result.set(JSON.stringify({ directiveDestroyedBefore350Ms: true, delayedEmissionCancelled: cancelled }, null, 2));
  }

  private delay(milliseconds: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
  }

  private errorText(error: any): string {
    return `Request failed (${error?.status ?? 'offline'}). Start the solution through Aspire to use the live endpoint.`;
  }
}

interface DeduplicatedGetSample {
  executionId: string;
  executedAtUtc: string;
}

interface ProjectChunksSampleResponse {
  chunkSize: number;
  totalCount: number;
  chunks: Array<{
    chunkNumber: number;
    count: number;
    rows: Array<{ id: string; key: string; name: string }>;
  }>;
}

interface ProjectCacheSample {
  cacheKey: string;
  divisionId: string;
  projectCount: number;
  generatedAtUtc: string;
}
