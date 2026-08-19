import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  FilterRequestOptions,
  NhCommonModule
} from '@newheap/platform-common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import {
  PROJECT_DEMO_DATA,
  ProjectApiService,
  ProjectCollectionRequestOptions,
  ProjectStatus,
  ProjectViewModel,
  getProjectStatusOptions,
  projectStatusTranslationKey
} from 'sample-project-management-common';

@Component({
  selector: 'app-collection-playground',
  standalone: true,
  imports: [CommonModule, FormsModule, NhCommonModule, TranslateModule],
  templateUrl: './collection-playground.component.html',
  styleUrl: './collection-playground.component.scss'
})
export class CollectionPlaygroundComponent {
  private readonly projectApi = inject(ProjectApiService);
  private readonly translateService = inject(TranslateService);

  readonly search = signal('');
  readonly textOperator = signal<'contains' | 'starts-with' | 'ends-with'>('contains');
  readonly statuses = signal<Set<ProjectStatus>>(new Set());
  readonly deadlineFrom = signal('');
  readonly deadlineTo = signal('');
  readonly withoutDeadline = signal(false);
  readonly activeOrCompleted = signal(false);
  readonly unsafeField = signal(false);
  readonly order = signal('status-name');
  readonly page = signal(1);
  readonly itemsPerPage = signal(2);
  readonly libraryPageSize = signal(10);
  readonly apiResult = signal('');
  readonly resolverPath = signal('');
  readonly resolverExpression = signal('');
  readonly resolverMatchCount = signal(0);
  readonly resolverSupported = signal(true);
  readonly resolverLimitation = signal('');
  readonly statusOptions = getProjectStatusOptions(
    this.translateService,
    false,
    [ProjectStatus.Archived]
  );
  readonly statusTranslationKey = projectStatusTranslationKey;
  readonly fluentExamples = [
    { labelKey: 'operator-equals', code: "options.equals('status', ProjectStatus.Active)" },
    { labelKey: 'operator-not-equals', code: "options.notEquals('status', ProjectStatus.Archived)" },
    { labelKey: 'operator-in', code: "options.isIn('status', selectedStatuses)" },
    { labelKey: 'operator-not-in', code: "options.isNotIn('status', hiddenStatuses)" },
    { labelKey: 'operator-like', code: "options.like('name', `%${search}%`)" },
    { labelKey: 'operator-greater-than', code: "options.greaterThan('budget', 0)" },
    { labelKey: 'operator-greater-than-or-equal', code: "options.greaterThanOrEqual('deadline', from)" },
    { labelKey: 'operator-less-than', code: "options.lessThan('budget', maximum)" },
    { labelKey: 'operator-less-than-or-equal', code: "options.lessThanOrEqual('deadline', to)" },
    { labelKey: 'operator-nested-or', code: "options.and(FilterRequestOptions.equals('status', active).or(FilterRequestOptions.equals('status', completed)))" },
    { labelKey: 'operator-root-or', code: "new CollectionHttpRequestOptions().equals('status', active).or(FilterRequestOptions.equals('status', completed))" },
    { labelKey: 'operator-filter-arrays', code: "FilterRequestOptions.equals('status', active).orArray(otherStatusFilters)" },
    { labelKey: 'operator-merge', code: "options.and(FilterRequestOptions.mergeToAndFilters(dynamicFilters)!)" },
    { labelKey: 'operator-order', code: "options.orderAsc('status').orderDesc('deadline')" },
    { labelKey: 'operator-dynamic-order', code: "options.order('name', direction)" },
    { labelKey: 'operator-collection-order', code: "options.orderByFirst('tasks', 'deadline', 'ASC').orderByLast('tasks', 'deadline', 'DESC')" }
  ];
  readonly lowLevelFilterExample = `options.filter.push(new FilterRequestOptions({\n  key: 'status',\n  operator: '==',\n  value: ProjectStatus.Active\n}));\n\noptions.orderBy.push(new OrderByRequestOptions({\n  key: 'name',\n  direction: 'ASC'\n}));`;

  readonly options = computed(() => {
    const searchValue = this.search().trim();
    const options = new ProjectCollectionRequestOptions({
      search: undefined,
      page: this.page(),
      itemsPerPage: this.itemsPerPage()
    });

    if (searchValue) {
      const pattern = this.textOperator() === 'starts-with'
        ? `${searchValue}%`
        : this.textOperator() === 'ends-with'
          ? `%${searchValue}`
          : `%${searchValue}%`;
      options.like('name', pattern, `text-${this.textOperator()}`);
    }

    const statuses = [...this.statuses()];
    if (statuses.length > 0) {
      options.isIn('status', statuses, 'selected-statuses');
    }

    if (this.activeOrCompleted()) {
      options.and(
        FilterRequestOptions
          .equals('status', ProjectStatus.Active, 'active-or-completed')
          .or(FilterRequestOptions.equals('status', ProjectStatus.Completed))
      );
    }

    if (this.withoutDeadline()) {
      options.equals('deadline', null, 'without-deadline');
    } else {
      if (this.deadlineFrom()) {
        options.greaterThanOrEqual('deadline', this.deadlineFrom(), 'deadline-from');
      }
      if (this.deadlineTo()) {
        options.lessThanOrEqual('deadline', this.deadlineTo(), 'deadline-to');
      }
    }

    if (this.unsafeField()) {
      options.and(new FilterRequestOptions({
        key: 'internal-secret',
        operator: '==',
        value: 'should-be-rejected',
        tag: 'attribute-protection'
      }));
    }

    this.applyOrder(options, this.order());
    return options;
  });

  readonly matchingProjects = computed(() => {
    const matches = PROJECT_DEMO_DATA.filter(project => this.matchesWithoutPaging(project));
    const sorted = [...matches].sort((left, right) => this.compare(left, right));
    const start = (this.page() - 1) * this.itemsPerPage();
    return sorted.slice(start, start + this.itemsPerPage());
  });

  readonly totalCount = computed(() =>
    PROJECT_DEMO_DATA.filter(project => this.matchesWithoutPaging(project)).length
  );

  readonly requestPreview = computed(() => JSON.stringify(this.options(), null, 2));

  updateTextOperator(event: Event): void {
    this.textOperator.set((event.target as HTMLSelectElement).value as 'contains' | 'starts-with' | 'ends-with');
    this.page.set(1);
  }

  updateSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
    this.page.set(1);
  }


  updateSearchValue(value: string): void {
    this.search.set(value ?? '');
    this.page.set(1);
  }

  updateLibraryPageSize(value: number): void {
    this.libraryPageSize.set(Number(value));
  }

  toggleStatus(status: ProjectStatus, checked: boolean): void {
    const next = new Set(this.statuses());
    checked ? next.add(status) : next.delete(status);
    this.statuses.set(next);
    this.page.set(1);
  }

  updateDeadlineFrom(event: Event): void {
    this.deadlineFrom.set((event.target as HTMLInputElement).value);
    this.page.set(1);
  }

  updateDeadlineTo(event: Event): void {
    this.deadlineTo.set((event.target as HTMLInputElement).value);
    this.page.set(1);
  }

  updateOrder(event: Event): void {
    this.order.set((event.target as HTMLSelectElement).value);
  }

  updatePageSize(event: Event): void {
    this.itemsPerPage.set(Number((event.target as HTMLSelectElement).value));
    this.page.set(1);
  }

  validateAgainstApi(): void {
    this.apiResult.set('Request is running…');
    this.projectApi.list(this.options()).subscribe({
      next: response => this.apiResult.set(
        `The API accepted the request: ${response.resultCount} results on this page.`
      ),
      error: error => this.apiResult.set(
        `The API rejected the request: ${error?.status ?? 'unknown status'}. ` +
        'This is expected when the unannotated field is active.'
      )
    });
  }

  resolveExpression(): void {
    this.resolverExpression.set('Request is running…');
    this.projectApi.resolveCollectionExpression(this.search().trim() || 'sample').subscribe({
      next: result => {
        this.resolverPath.set(result.resolvedPath);
        this.resolverExpression.set(result.generatedExpression);
        this.resolverMatchCount.set(result.matchCount);
        this.resolverSupported.set(result.isSupported);
        this.resolverLimitation.set(result.limitation ?? '');
      },
      error: error => {
        this.resolverPath.set('');
        this.resolverExpression.set('HTTP ' + (error?.status ?? 'unknown'));
        this.resolverMatchCount.set(0);
        this.resolverSupported.set(false);
        this.resolverLimitation.set('The API could not execute the resolver sample.');
      }
    });
  }

  nextPage(): void {
    if (this.page() * this.itemsPerPage() < this.totalCount()) {
      this.page.update(value => value + 1);
    }
  }

  previousPage(): void {
    this.page.update(value => Math.max(1, value - 1));
  }

  private applyOrder(options: ProjectCollectionRequestOptions, value: string): void {
    switch (value) {
      case 'name-desc':
        options.orderDesc('name');
        return;
      case 'deadline':
        options.orderAsc('deadline');
        return;
      default:
        options.orderAsc('status').orderAsc('name');
    }
  }

  private compare(left: ProjectViewModel, right: ProjectViewModel): number {
    switch (this.order()) {
      case 'name-desc':
        return right.name.localeCompare(left.name);
      case 'deadline':
        return (left.deadline ?? '9999').localeCompare(right.deadline ?? '9999');
      default:
        return Object.values(ProjectStatus).indexOf(left.status) -
          Object.values(ProjectStatus).indexOf(right.status) ||
          left.name.localeCompare(right.name);
    }
  }

  private matchesWithoutPaging(project: ProjectViewModel): boolean {
    const term = this.search().trim().toLowerCase();
    const name = project.name.toLowerCase();
    const selectedStatuses = this.statuses();
    const deadline = project.deadline ? new Date(project.deadline).getTime() : undefined;
    const from = this.deadlineFrom() ? new Date(this.deadlineFrom()).getTime() : undefined;
    const to = this.deadlineTo() ? new Date(this.deadlineTo()).getTime() : undefined;

    return (!term || `${project.key} ${project.name} ${project.description ?? ''}`.toLowerCase().includes(term)) &&
      (selectedStatuses.size === 0 || selectedStatuses.has(project.status)) &&
      (!this.activeOrCompleted() || project.status === ProjectStatus.Active || project.status === ProjectStatus.Completed) &&
      (this.withoutDeadline()
        ? deadline === undefined
        : (from === undefined || (deadline !== undefined && deadline >= from)) &&
          (to === undefined || (deadline !== undefined && deadline <= to)));
  }
}
