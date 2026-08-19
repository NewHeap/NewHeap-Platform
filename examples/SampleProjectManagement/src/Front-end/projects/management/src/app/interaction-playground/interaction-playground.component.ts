import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import {
  ReactiveFormsModule,
  UntypedFormControl,
  UntypedFormGroup
} from '@angular/forms';
import {
  AspMvcFormServerSideFormValidator,
  CollectionHttpRequestOptions,
  CollectionHttpResponse,
  NhCollectionBaseComponent,
  NhCommonModule,
  NhServerSideFormValidationService,
  NhTaskResultFormValidationService,
  TaskResultItem
} from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import { Observable, catchError, of } from 'rxjs';
import {
  PROJECT_DEMO_DATA,
  ProjectApiService,
  ProjectCollectionRequestOptions,
  ProjectViewModel
} from 'sample-project-management-common';

@Component({
  selector: 'app-interaction-playground',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, NhCommonModule, TranslateModule],
  templateUrl: './interaction-playground.component.html',
  styleUrl: './interaction-playground.component.scss'
})
export class InteractionPlaygroundComponent
  extends NhCollectionBaseComponent<ProjectViewModel> {
  private readonly projectApi = inject(ProjectApiService);
  private readonly serverValidator = inject(NhServerSideFormValidationService);
  private readonly taskResultValidator = inject(NhTaskResultFormValidationService);
  private loadSequence = 0;

  readonly lifecycleEvents = signal<string[]>([]);
  readonly validationForm = new UntypedFormGroup({
    project: new UntypedFormGroup({
      name: new UntypedFormControl('')
    }),
    code: new UntypedFormControl(''),
    '': new UntypedFormControl('')
  });

  override getInitialRequestOptions(): CollectionHttpRequestOptions {
    return new ProjectCollectionRequestOptions({
      page: 1,
      itemsPerPage: 2
    }).orderAsc('name');
  }

  override getLocalStoragePartialKey(): string {
    return 'sample-project-lifecycle';
  }

  override async appOnInit(): Promise<void> {
    this.record('appOnInit: componentstate initialiseren');
  }

  override async appAfterViewInit(): Promise<void> {
    this.record('appAfterViewInit: view is available');
  }

  override async appOnDestroy(): Promise<void> {
    this.record('appOnDestroy: componentspecifieke cleanup');
  }

  override async onLoad(
    requestOptions: CollectionHttpRequestOptions
  ): Promise<Observable<CollectionHttpResponse<ProjectViewModel>>> {
    const request = new ProjectCollectionRequestOptions({
      page: requestOptions.page,
      itemsPerPage: requestOptions.itemsPerPage,
      search: requestOptions.search,
      orderBy: requestOptions.orderBy,
      filter: requestOptions.filter,
      statuses: []
    });
    this.record(`onLoad #${++this.loadSequence}: page=${request.page}, search=${request.search || '-'}`);

    return this.projectApi.list(request).pipe(
      catchError(() => of(this.createDemoResponse(request)))
    );
  }

  override async beforeLoad(): Promise<void> {
    this.record('beforeLoad: requeststate synchroniseren');
  }

  override async afterLoad(): Promise<void> {
    this.record(`afterLoad: ${this.items.length} items, ${this.collectionResponse.totalCount} totaal`);
  }

  searchProjects(value: string): void {
    void this.search(value);
  }

  sortProjects(direction: 'asc' | 'desc'): void {
    void this.sort({ sorts: [{ prop: 'name', dir: direction }] });
  }

  previousPage(): void {
    void this.setPage({
      page: Math.max(1, this.collectionResponse.page - 1),
      itemsPerPage: this.collectionResponse.itemsPerPage
    });
  }

  nextPage(): void {
    const maxPage = Math.max(
      1,
      Math.ceil(this.collectionResponse.totalCount / this.collectionResponse.itemsPerPage)
    );
    void this.setPage({
      page: Math.min(maxPage, this.collectionResponse.page + 1),
      itemsPerPage: this.collectionResponse.itemsPerPage
    });
  }

  showAspMvcValidation(): void {
    this.clearRemoteErrors(this.validationForm);
    this.projectApi.getNestedValidationSample().subscribe({
      error: error => this.applyAspMvcValidation(
        error?.error && typeof error.error === 'object'
          ? error
          : {
              error: {
                'project.name': ['This nested name error comes from ModelState.'],
                '': ['This general server error belongs to the form.']
              }
            }
      )
    });
  }

  showTaskResultValidation(): void {
    this.clearRemoteErrors(this.validationForm);
    this.taskResultValidator.validate(this.validationForm, [
      new TaskResultItem({
        name: 'code',
        errorMessages: ['The project code already exists.']
      }),
      new TaskResultItem({
        name: '',
        errorMessages: ['The mutation could not be processed completely.']
      })
    ]);
  }

  private createDemoResponse(
    request: ProjectCollectionRequestOptions
  ): CollectionHttpResponse<ProjectViewModel> {
    const search = (request.search ?? '').trim().toLowerCase();
    const direction = request.orderBy[0]?.direction ?? 'ASC';
    const matches = PROJECT_DEMO_DATA
      .filter(project => !search || `${project.key} ${project.name}`.toLowerCase().includes(search))
      .sort((left, right) => direction === 'DESC'
        ? right.name.localeCompare(left.name)
        : left.name.localeCompare(right.name));
    const start = (request.page - 1) * request.itemsPerPage;
    const items = matches.slice(start, start + request.itemsPerPage);

    return new CollectionHttpResponse<ProjectViewModel>({
      page: request.page,
      itemsPerPage: request.itemsPerPage,
      resultCount: items.length,
      totalCount: matches.length,
      items,
      orderBy: request.orderBy,
      filter: request.filter,
      search: request.search
    });
  }

  private record(event: string): void {
    this.lifecycleEvents.update(events => [event, ...events].slice(0, 8));
  }

  private applyAspMvcValidation(response: unknown): void {
    this.serverValidator.validate(
      AspMvcFormServerSideFormValidator,
      this.validationForm,
      response
    );
  }

  private clearRemoteErrors(control: UntypedFormGroup | UntypedFormControl): void {
    control.setErrors(null);
    if (control instanceof UntypedFormGroup) {
      Object.values(control.controls).forEach(child =>
        this.clearRemoteErrors(child as UntypedFormGroup | UntypedFormControl)
      );
    }
  }
}
