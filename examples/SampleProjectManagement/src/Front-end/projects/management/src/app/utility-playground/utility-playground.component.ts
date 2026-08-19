import { CommonModule } from '@angular/common';
import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
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
  NhFormHelper,
  NhFormDropDownSettings,
  NhHttpUtil,
  NhInternetConnectionService,
  NhCommonModule,
  NhCommonModuleConfig,
  NhMutex,
  NhSentryInitializerService,
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
  uppercaseFirst
} from '@newheap/platform-common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import {
  ProjectStatus,
  getProjectStatusOptions,
  projectStatusTranslationKey
} from 'sample-project-management-common';

@Component({
  selector: 'app-utility-playground',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, TranslateModule, NhCommonModule],
  templateUrl: './utility-playground.component.html',
  styleUrl: './utility-playground.component.scss'
})
export class UtilityPlaygroundComponent {
  private readonly asyncLock = new NhAsyncLock();
  private readonly mutex = new NhMutex();
  private readonly translateService = inject(TranslateService);
  private readonly cookieService = inject(NhCookieService);
  private readonly connectionService = inject(NhInternetConnectionService);
  private readonly sentry = inject(NhSentryInitializerService);
  private readonly moduleConfig = inject(NhCommonModuleConfig);

  readonly internetConnected = toSignal(
    this.connectionService.internetIsConnected,
    { initialValue: true }
  );
  readonly asyncLog = signal<string[]>([]);
  readonly encodedBlob = signal('');
  readonly observableResult = signal('');
  readonly cookieResult = signal('');
  readonly sentryResult = signal('');
  readonly utilityMatrixResult = signal('');
  readonly debouncedButtonRuns = signal(0);
  readonly presentationError = new TaskResult().withError('', 'The API could not load the project list.');
  readonly trustedHtml = '<strong>Trusted sample content only</strong>';
  readonly now = new Date();
  readonly enumStatusOptions = getProjectStatusOptions(this.translateService, false);
  readonly singleStatus = new FormControl<ProjectStatus[]>([ProjectStatus.Active], { nonNullable: true });
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

  runDebouncedButton(): void {
    this.debouncedButtonRuns.update(value => value + 1);
  }

  async runObservable(): Promise<void> {
    const result = await of({ id: 42, state: 'completed' }).typedResultLastValueFrom();
    this.observableResult.set(JSON.stringify(result, null, 2));
  }

  runSentryDryRun(): void {
    this.sentry.registerHookBeforeSend(event => {
      event.tags = { ...event.tags, sample: 'SPM-159' };
      return event;
    });
    this.sentryResult.set(`Hook registered; outbound logging is ${this.sentry.isEnabled ? 'active' : 'safely disabled'}.`);
  }

  private createStatusDropDownSettings(selectionLimit: number): NhFormDropDownSettings {
    return new NhFormDropDownSettings({
      lazyLoad: false,
      loadLambda: () => of(this.enumStatusOptions),
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
