import { CommonModule } from '@angular/common';
import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import {
  CollectionHttpRequestOptions,
  CollectionHttpResponse,
  DefaultMultiSelectSettings,
  DefaultMultiSelectTexts,
  IsDefined,
  NhAngularUtil,
  NhApiUtil,
  NhAsyncLock,
  NhCookieService,
  NhEncodingUtil,
  NhEnumDropDownItem,
  NhFormHelper,
  NhFormDropDownSettings,
  NhHttpUtil,
  NhInternetConnectionService,
  NhCommonModule,
  NhCommonModuleConfig,
  NhMutex,
  TaskResult,
  enumIntValuesToArray,
  enumKeysToArray,
  enumStringValuesToArray,
  enumValuesToArray,
  getEmptyGuid,
  getRandomIdentifier,
  groupBy,
  languageToCultureMap,
  nameof,
  nhFormatRelativeTime,
  NhRelativeTimePipe,
  uppercaseFirst
} from '@newheap/platform-common';
import { NhHtmlSanitizer, NhSanitizeHtmlPipe } from '@newheap/platform-common/sanitize';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import {
  ProjectStatus,
  getProjectStatusOptions,
  projectStatusTranslationKey
} from 'sample-project-management-common';

/** Rich text as an e-mail client or Word puts it on the clipboard, including what must not survive. */
const examplePastedHtml = [
  '<html><head><style>p.MsoNormal { margin: 0; }</style></head><body>',
  '<!--[if gte mso 9]><xml><o:OfficeDocumentSettings></o:OfficeDocumentSettings></xml><![endif]-->',
  '<p class="MsoNormal" style="color: #c00"><b>Release plan</b> for <span style="font-size: 11pt">Customer Portal</span></p>',
  '<ul><li>Freeze on <em>Friday</em></li><li onclick="alert(1)">Review the <a href="https://newheap.com/docs" target="_self">checklist</a></li></ul>',
  '<p><a href="javascript:alert(2)">Confirm your account</a><img src="https://tracker.example/pixel.gif" onerror="alert(3)"></p>',
  '<script>alert(4)</script><iframe src="https://example.com"></iframe>',
  '<table><tr><th scope="col">Owner</th><td colspan="2">Operations</td></tr></table>',
  '</body></html>'
].join('\n');

@Component({
  selector: 'app-utility-playground',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, TranslateModule, NhCommonModule, NhRelativeTimePipe, NhSanitizeHtmlPipe],
  templateUrl: './utility-playground.component.html',
  styleUrl: './utility-playground.component.scss'
})
export class UtilityPlaygroundComponent {
  private readonly asyncLock = new NhAsyncLock();
  private readonly mutex = new NhMutex();
  private readonly translateService = inject(TranslateService);
  private readonly cookieService = inject(NhCookieService);
  private readonly connectionService = inject(NhInternetConnectionService);
  private readonly moduleConfig = inject(NhCommonModuleConfig);
  private readonly htmlSanitizer = inject(NhHtmlSanitizer);

  readonly internetConnected = toSignal(
    this.connectionService.internetIsConnected,
    { initialValue: true }
  );
  readonly asyncLog = signal<string[]>([]);
  readonly encodedBlob = signal('');
  readonly observableResult = signal('');
  readonly cookieResult = signal('');
  readonly utilityMatrixResult = signal('');
  readonly debouncedButtonRuns = signal(0);
  readonly presentationError = new TaskResult().withError('', 'The API could not load the project list.');
  readonly trustedHtml = '<strong>Trusted sample content only</strong>';
  readonly now = new Date();
  readonly pastedHtml = signal(examplePastedHtml);
  readonly sanitizedMarkup = computed(() => this.htmlSanitizer.sanitize(this.pastedHtml()));
  readonly pastedFromClipboard = signal(false);
  // A fixed reference makes the second column deterministic, as a test or server render would pass it.
  readonly openedAt = this.now.getTime();
  readonly activity = [
    { key: 'project.relative-time-comment-added', offset: -20 * 1000 },
    { key: 'project.relative-time-task-moved', offset: -5 * 60 * 1000 },
    { key: 'project.relative-time-status-changed', offset: -3 * 60 * 60 * 1000 },
    { key: 'project.relative-time-project-created', offset: -9 * 24 * 60 * 60 * 1000 },
    { key: 'project.relative-time-review-due', offset: 2 * 24 * 60 * 60 * 1000 },
    { key: 'project.relative-time-contract-renewal', offset: 400 * 24 * 60 * 60 * 1000 }
  ].map(item => ({
    key: item.key,
    at: this.openedAt + item.offset,
    iso: new Date(this.openedAt + item.offset).toISOString()
  }));
  readonly enumStatusOptions = getProjectStatusOptions(this.translateService, false);
  // Single-select yields one value; multi-select yields an array.
  readonly singleStatus = new FormControl<ProjectStatus | null>(ProjectStatus.Active);
  readonly singleStatusValueChanges = signal(0);
  readonly singleStatusEvents = signal<string[]>([]);
  readonly multipleStatuses = new FormControl<ProjectStatus[]>([ProjectStatus.Draft, ProjectStatus.Active], { nonNullable: true });
  readonly singleStatusSettings = this.createStatusDropDownSettings(1);
  readonly multipleStatusSettings = this.createStatusDropDownSettings(0);
  readonly deferredProjectIds = new FormControl<string[]>(['project-a'], { nonNullable: true });
  readonly deferredCollectionRequests = signal(0);
  readonly deferredSelectedRequests = signal(0);
  readonly deferredLazyLoadEnabled = this.moduleConfig.formDropdown.deferLazyLoadUntilOpened;
  readonly deferredProjectSettings = this.createDeferredProjectDropDownSettings();

  readonly values = [
    { name: 'getEmptyGuid()', value: getEmptyGuid() },
    { name: 'getRandomIdentifier()', value: getRandomIdentifier() },
    { name: 'uppercaseFirst("project")', value: uppercaseFirst('project') },
    { name: 'nameof<Project>("name")', value: nameof<{ name: string }>('name') },
    { name: 'IsDefined(null)', value: String(IsDefined(null)) },
    { name: 'enumIntValuesToArray(ProjectStatus)', value: JSON.stringify(enumIntValuesToArray(ProjectStatus)) },
    { name: 'enumKeysToArray(ProjectStatus)', value: JSON.stringify(enumKeysToArray(ProjectStatus)) },
    { name: 'enumValuesToArray(ProjectStatus)', value: JSON.stringify(enumValuesToArray(ProjectStatus)) },
    { name: 'enumStringValuesToArray(ProjectStatus)', value: JSON.stringify(enumStringValuesToArray(ProjectStatus)) },
    { name: 'languageToCultureMap.nl', value: languageToCultureMap.nl },
    {
      name: 'nhFormatRelativeTime(now - 3 h, now, "nl")',
      value: nhFormatRelativeTime(this.openedAt - 3 * 60 * 60 * 1000, this.openedAt, 'nl') ?? '-'
    },
    { name: 'NhAngularUtil.idTrackBy()', value: NhAngularUtil.idTrackBy(0, { id: 'NHP' }) },
    { name: '[1, 2, 3].firstOrDefault()', value: String([1, 2, 3].firstOrDefault()) },
    { name: '[1, 2, 3].lastOrDefault()', value: String([1, 2, 3].lastOrDefault()) },
    { name: '[].any()', value: String([].any()) },
    {
      name: 'groupBy(projects, status)',
      value: JSON.stringify(groupBy(
        [{ status: 'active' }, { status: 'active' }, { status: 'draft' }],
        (item: { status: string }) => item.status
      ))
    }
  ];

  constructor() {
    // The form reports a value written from code once and only user choices mark it dirty.
    this.singleStatus.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(value => {
        this.singleStatusValueChanges.update(count => count + 1);
        this.logSingleStatusEvent(`value ${value ?? '-'}`);
      });
  }

  async runMutex(): Promise<void> {
    this.asyncLog.set([]);
    await Promise.all([1, 2, 3].map(item => this.asyncLock.runExclusive(async () => {
      this.asyncLog.update(log => [...log, `start ${item}`]);
      await new Promise(resolve => setTimeout(resolve, 25));
      this.asyncLog.update(log => [...log, `end ${item}`]);
    })));
  }

  async runMutexPrimitive(): Promise<void> {
    this.asyncLog.set([]);
    await Promise.all([1, 2, 3].map(async item => {
      const release = await this.mutex.lock();
      try {
        this.asyncLog.update(log => [...log, `lock ${item}`]);
        await new Promise(resolve => setTimeout(resolve, 20));
        this.asyncLog.update(log => [...log, `release ${item}`]);
      } finally {
        release();
      }
    }));
  }

  async runEncoding(): Promise<void> {
    const original = 'Sample Project Management - café';
    const result = await NhEncodingUtil.convertBlobToBase64(
      new Blob([original], { type: 'text/plain' })
    );
    const encoded = String(result);
    const bytes = Uint8Array.from(atob(encoded.split(',')[1]), value => value.charCodeAt(0));
    const roundTrip = new TextDecoder().decode(bytes);
    this.encodedBlob.set(JSON.stringify({ encoded, roundTrip, equal: original === roundTrip }, null, 2));
  }

  runUtilityMatrix(): void {
    const form = new FormGroup({
      name: new FormControl('', Validators.required),
      nested: new FormGroup({
        code: new FormControl('', Validators.required)
      })
    });
    form.updateValueAndValidity();
    const validationErrorsBefore = form.controls.name.errors;
    NhFormHelper.clearErrors(form);
    const request = new CollectionHttpRequestOptions()
      .equals('status', ProjectStatus.Active)
      .orderAsc('name');
    const parsed = NhApiUtil.ParseCollectionRequestOptions(JSON.stringify(request));
    const formData = NhHttpUtil.objectToFormData({ project: { name: 'Platform' }, tags: ['sample', 'cache'] });
    const formDataEntries: Array<{ key: string; value: string }> = [];
    formData.forEach((value, key) => formDataEntries.push({ key, value: String(value) }));
    const taskResult = NhApiUtil.taskResultFromResponse(
      new HttpErrorResponse({
        error: { errors: { name: ['Name is required'] } },
        headers: new HttpHeaders({ 'Content-Type': 'application/json' }),
        status: 400
      })
    );
    const enumDropDown = NhFormHelper.getEnumDropDownByEnum(
      ProjectStatus,
      this.translateService,
      'project.status-',
      false,
      [],
      projectStatusTranslationKey
    );
    this.utilityMatrixResult.set(JSON.stringify({
      validationErrorsBefore,
      validationErrorsAfter: form.controls.name.errors,
      formData: formDataEntries,
      parsedFilter: parsed.filter,
      parsedOrder: parsed.orderBy,
      apiErrors: taskResult.items,
      enumDropDown
    }, null, 2));
  }

  logSingleStatusEvent(event: string): void {
    this.singleStatusEvents.update(events => [...events, event].slice(-6));
  }

  setSingleStatusFromCode(): void {
    this.singleStatus.setValue(ProjectStatus.Completed);
  }

  resetSingleStatus(): void {
    this.singleStatus.reset(ProjectStatus.Active);
  }

  selectProjectFromCode(): void {
    // The selected-option lookup of this sample answers synchronously, as a cached store does.
    this.deferredProjectIds.setValue(['project-c']);
  }

  onRichTextPaste(event: ClipboardEvent): void {
    // Pasting rich text into a textarea keeps only its plain text; read the HTML the source put on the clipboard.
    const html = event.clipboardData?.getData('text/html');
    if (!html) {
      return;
    }

    event.preventDefault();
    this.pastedHtml.set(html);
    this.pastedFromClipboard.set(true);
  }

  editPastedHtml(value: string): void {
    this.pastedHtml.set(value);
    this.pastedFromClipboard.set(false);
  }

  resetPastedHtml(): void {
    this.pastedHtml.set(examplePastedHtml);
    this.pastedFromClipboard.set(false);
  }

  runDebouncedButton(): void {
    this.debouncedButtonRuns.update(value => value + 1);
  }

  async runObservable(): Promise<void> {
    const result = await of({ id: 42, state: 'completed' }).typedResultLastValueFrom();
    this.observableResult.set(JSON.stringify(result, null, 2));
  }

  private createStatusDropDownSettings(selectionLimit: number): NhFormDropDownSettings {
    return new NhFormDropDownSettings({
      lazyLoad: false,
      loadLambda: () => of(this.enumStatusOptions),
      // An archived status stays selectable but is shown as inactive.
      classesGetLambda: (option: NhEnumDropDownItem<ProjectStatus | ''>) =>
        option.id === ProjectStatus.Archived ? 'is-inactive' : undefined,
      multiSelectSettings: new DefaultMultiSelectSettings({
        selectionLimit,
        closeOnSelect: selectionLimit === 1,
        showCheckAll: selectionLimit === 0,
        showUncheckAll: selectionLimit === 0,
        isLazyLoad: false
      }),
      multiSelectTexts: new DefaultMultiSelectTexts({
        defaultTitle: this.translateService.instant('project.select-status'),
        checked: this.translateService.instant('project.status-selected'),
        checkedPlural: this.translateService.instant('project.statuses-selected')
      })
    });
  }

  private createDeferredProjectDropDownSettings(): NhFormDropDownSettings {
    const projects = [
      { id: 'project-a', name: 'Authorization Alpha' },
      { id: 'project-b', name: 'Authorization Beta' },
      { id: 'project-c', name: 'Customer Portal' }
    ];

    return new NhFormDropDownSettings({
      lazyLoad: true,
      translateOptionValue: false,
      requestOptions: new CollectionHttpRequestOptions({ itemsPerPage: 20 }),
      selectedRequestOptions: new CollectionHttpRequestOptions({ itemsPerPage: 20 }),
      lazyLoadLambda: request => {
        this.deferredCollectionRequests.update(count => count + 1);
        const search = (request.search ?? '').trim().toLowerCase();
        const items = projects.filter(project =>
          !search || project.name.toLowerCase().includes(search)
        );
        return of(new CollectionHttpResponse({
          page: 1,
          itemsPerPage: 20,
          resultCount: items.length,
          totalCount: items.length,
          items
        }));
      },
      selectedLazyLoadLambda: (_request, value) => {
        this.deferredSelectedRequests.update(count => count + 1);
        const selectedIds = new Set(Array.isArray(value) ? value : [value]);
        const items = projects.filter(project => selectedIds.has(project.id));
        return of(new CollectionHttpResponse({
          page: 1,
          itemsPerPage: 20,
          resultCount: items.length,
          totalCount: items.length,
          items
        }));
      },
      multiSelectSettings: new DefaultMultiSelectSettings({
        selectionLimit: 0,
        showCheckAll: true,
        showUncheckAll: true,
        closeOnSelect: false,
        isLazyLoad: true
      }),
      multiSelectTexts: new DefaultMultiSelectTexts({
        defaultTitle: this.translateService.instant('project.select-projects')
      })
    });
  }

  runCookie(): void {
    const key = 'sample-project-management-consent';
    this.cookieService.set(key, 'accepted', { path: '/', sameSite: 'Lax' });
    const value = this.cookieService.get(key);
    this.cookieService.delete(key, '/');
    this.cookieResult.set(`set: ${value}; delete: ${this.cookieService.get(key) ?? 'removed'}`);
  }
}
