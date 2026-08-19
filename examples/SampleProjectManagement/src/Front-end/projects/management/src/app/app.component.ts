import { CommonModule } from '@angular/common';
import {
  Component,
  OnInit,
  ViewContainerRef,
  computed,
  inject,
  signal
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  NhHttpUtil,
  NhCommonModule,
  NhModalConfirmComponent,
  NhModalOptions,
  NhModalService,
  NhStringUtil
} from '@newheap/platform-common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import {
  PROJECT_DEMO_DATA,
  ProjectApiService,
  ProjectBulkStatusResultViewModel,
  ProjectCollectionRequestOptions,
  ProjectStatus,
  ProjectViewModel,
  SampleApiConnectionStateService,
  SampleAuthService,
  getProjectStatusOptions,
  projectStatusKey
} from 'sample-project-management-common';
import { ProjectEditModalComponent } from './project-edit-modal/project-edit-modal.component';

type ActionKind = 'success' | 'error' | 'simulated' | 'warning';

@Component({
  selector: 'app-management-overview',
  standalone: true,
  imports: [CommonModule, NhCommonModule, TranslateModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit {
  private readonly projectApi = inject(ProjectApiService);
  private readonly modalService = inject(NhModalService);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly translateService = inject(TranslateService);
  private readonly connectionState = inject(SampleApiConnectionStateService);
  private readonly authService = inject(SampleAuthService);
  private readonly authorization = toSignal(this.authService.authSubject, {
    initialValue: this.authService.getAuthorization()
  });

  readonly projects = signal<ProjectViewModel[]>(PROJECT_DEMO_DATA);
  readonly search = signal('');
  readonly statusFilter = signal<ProjectStatus | ''>('');
  readonly selectedIds = signal<Set<string>>(new Set());
  readonly bulkStatus = signal(ProjectStatus.Active);
  readonly statusFilterOptions = getProjectStatusOptions(
    this.translateService,
    true,
    [ProjectStatus.Archived]
  );
  readonly editableStatusOptions = getProjectStatusOptions(
    this.translateService,
    false,
    [ProjectStatus.Archived]
  );
  readonly bulkStatusOptions = getProjectStatusOptions(
    this.translateService,
    false,
    [ProjectStatus.Draft]
  );
  readonly bulkResult = signal<ProjectBulkStatusResultViewModel | null>(null);
  readonly modalLifecycle = signal<string[]>([]);
  readonly loading = signal(true);
  readonly lastAction = signal('');
  readonly lastActionKind = signal<ActionKind>('success');
  readonly updatingProjectIds = signal<Set<string>>(new Set());
  readonly bulkUpdating = signal(false);
  readonly requestPreview = signal('{}');
  readonly canManage = computed(() => {
    this.authorization();
    return this.authService.isOnePermissionGranted(['app.project.manage']);
  });
  readonly filteredProjects = computed(() => {
    const term = this.search().trim().toLowerCase();
    const status = this.statusFilter();

    return this.projects().filter(project => {
      const matchesSearch = !term ||
        `${project.key} ${project.name} ${project.description ?? ''}`
          .toLowerCase()
          .includes(term);
      const matchesStatus = status === '' || project.status === status;
      return matchesSearch && matchesStatus;
    });
  });

  readonly allVisibleSelected = computed(() => {
    const visible = this.filteredProjects();
    return visible.length > 0 && visible.every(project => this.selectedIds().has(project.id));
  });

  readonly bulkSucceeded = computed(() =>
    this.bulkResult()?.results.filter(item => item.success) ?? []);

  readonly bulkFailed = computed(() =>
    this.bulkResult()?.results.filter(item => !item.success) ?? []);

  readonly utilitySamples = [
    {
      utility: 'NhStringUtil.upperFirst',
      input: 'project management',
      output: NhStringUtil.upperFirst('project management')
    },
    {
      utility: 'NhStringUtil.lowerFirst',
      input: 'ProjectStatus',
      output: NhStringUtil.lowerFirst('ProjectStatus')
    },
    {
      utility: 'NhHttpUtil.filenameFromContentDisposition',
      input: 'attachment; filename="project-export.xlsx"',
      output: NhHttpUtil.filenameFromContentDisposition(
        'attachment; filename="project-export.xlsx"'
      )
    }
  ];

  readonly filterCode = `const options = new ProjectCollectionRequestOptions({\n  search: searchTerm,\n  itemsPerPage: 20\n})\n  .equals('status', ProjectStatus.Active)\n  .orderAsc('name');\n\nreturn projectApi.list(options);`;

  readonly lowLevelFilterCode = `// Lower-level equivalent: use this only when you\n// deliberately build a dynamic or raw request tree.\noptions.filter.push(new FilterRequestOptions({\n  key: 'status',\n  operator: '==',\n  value: ProjectStatus.Active\n}));`;

  readonly partialCode = `// ProjectService: the service owns the unit of work.\nawait using var transaction =\n  await repository.StartOrGetTransactionScopeAsync(ct);\n\nvar result = await base.UpdatePartialAsync(\n  id,\n  calls => calls.SetProperty(\n    project => project.Status,\n    model.Status),\n  cancellationToken: ct);\n\nif (!result.Success) return result;\n\nawait eventPublisher.PublishAsync(\n  new ProjectStatusChangedEvent { ProjectId = id });\nawait transaction.CommitAsync(ct);\nreturn result;`;

  readonly bulkCode = `// ProjectService opens the outer transaction.\nawait using var transaction =\n  await repository.StartOrGetTransactionScopeAsync(ct);\n\nvar result = await base.BulkAsync(\n  new BulkCRUDMutateModel<...> {\n    // BulkAsync joins the already-open transaction.\n    UseTransaction = false,\n    ContinueOnError = model.ContinueOnError,\n    UpdatePartial = partialUpdates\n  },\n  new BaseDbEntityServiceOperationOptions(),\n  cancellationToken: ct);\n\nif (!result.Success) return result;\n\nawait eventPublisher.PublishAsync(\n  new ProjectBulkChangedEvent { Updated = count });\nawait transaction.CommitAsync(ct);\nreturn result;`;

  constructor() {
    this.modalService.setViewContainerRef(this.viewContainerRef);
  }

  ngOnInit(): void {
    this.loadProjects();
  }

  loadProjects(): void {
    const options = new ProjectCollectionRequestOptions({
      search: this.search(),
      itemsPerPage: 20
    });

    const status = this.statusFilter();
    if (status !== '') {
      options.equals('status', status);
    }
    options.orderAsc('name');

    this.requestPreview.set(JSON.stringify(options, null, 2));
    this.loading.set(true);

    this.projectApi.list(options).subscribe({
      next: response => {
        this.projects.set(response.items);
        this.connectionState.markConnected();
        this.loading.set(false);
      },
      error: error => {
        this.connectionState.markFailure(error);
        if (!this.connectionState.demoMode()) {
          this.reportAction(
            this.translateService.instant('project.projects-load-failed'),
            'error'
          );
        }
        this.loading.set(false);
      }
    });
  }

  updateSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }

  updateStatusFilter(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.statusFilter.set(value === '' ? '' : value as ProjectStatus);
    this.loadProjects();
  }

  openEditModal(project?: ProjectViewModel): void {
    const demoMode = this.connectionState.demoMode();
    const modal = this.modalService.open(
      ProjectEditModalComponent,
      new NhModalOptions({
        title: project ? 'Edit project' : 'Create project',
        modalClasses: 'large'
      }),
      {
        project,
        demoMode,
        lifecycleReporter: (event: string) => this.recordModalEvent(event)
      }
    );

    this.recordModalEvent('modal reference opened');
    const contentSubscriptions = [
      modal.contentComponent!.created.subscribe(created => {
        this.projects.update(projects => [created, ...projects]);
        this.reportAction(
          this.translateService.instant(
            demoMode ? 'project.create-simulated' : 'project.create-succeeded',
            { key: created.key }
          ),
          demoMode ? 'simulated' : 'success'
        );
        this.recordModalEvent(`content-event created: ${created.key}`);
        setTimeout(() => modal.close());
      }),
      modal.contentComponent!.updated.subscribe(updated => {
        this.replaceProject(updated);
        this.reportAction(
          this.translateService.instant(
            demoMode ? 'project.update-simulated' : 'project.update-succeeded',
            { key: updated.key }
          ),
          demoMode ? 'simulated' : 'success'
        );
        this.recordModalEvent(`content-event updated: ${updated.key}`);
        setTimeout(() => modal.close());
      })
    ];
    modal.onClose(() => {
      contentSubscriptions.forEach(subscription => subscription.unsubscribe());
      this.recordModalEvent('closed event received; subscriptions disposed');
    });
  }

  openDeleteModal(project: ProjectViewModel): void {
    const modal = this.modalService.open(
      NhModalConfirmComponent,
      new NhModalOptions({ title: 'Delete project' })
    );

    modal.contentComponent!.message = `Are you sure you want to delete ${project.key}? The API rejects projects with open tasks.`;
    modal.contentComponent!.modalClass = 'danger';
    modal.contentComponent!.onConfirm = async () => {
      const demoMode = this.connectionState.demoMode();
      try {
        if (!demoMode) {
          await this.projectApi.deleteProject(project.id).lastValueFrom();
          this.connectionState.markConnected();
        }

        this.projects.update(projects => projects.filter(item => item.id !== project.id));
        this.selectedIds.update(ids => {
          const next = new Set(ids);
          next.delete(project.id);
          return next;
        });
        this.reportAction(
          this.translateService.instant(
            demoMode ? 'project.delete-simulated' : 'project.delete-succeeded',
            { key: project.key }
          ),
          demoMode ? 'simulated' : 'success'
        );
        modal.close();
      } catch (error) {
        this.connectionState.markFailure(error);
        this.reportAction(
          this.translateService.instant('project.delete-failed', { key: project.key }),
          'error'
        );
      }
    };
    modal.contentComponent!.onCancel = () => modal.close();
  }

  updateSingleStatus(project: ProjectViewModel, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const status = select.value as ProjectStatus;
    const localUpdate = () => this.replaceProject({
      ...project,
      status
    });

    if (this.connectionState.demoMode()) {
      localUpdate();
      this.reportAction(
        this.translateService.instant('project.status-update-simulated', { key: project.key }),
        'simulated'
      );
      return;
    }

    this.setProjectUpdating(project.id, true);
    this.projectApi.updateStatus(project.id, status).subscribe({
      next: () => {
        this.connectionState.markConnected();
        localUpdate();
        this.reportAction(
          this.translateService.instant('project.status-update-succeeded', { key: project.key }),
          'success'
        );
        this.setProjectUpdating(project.id, false);
      },
      error: error => {
        this.connectionState.markFailure(error);
        select.value = project.status;
        this.reportAction(
          this.translateService.instant('project.status-update-failed', { key: project.key }),
          'error'
        );
        this.setProjectUpdating(project.id, false);
      }
    });
  }

  toggleProject(id: string, checked: boolean): void {
    const selected = new Set(this.selectedIds());
    checked ? selected.add(id) : selected.delete(id);
    this.selectedIds.set(selected);
  }

  toggleAll(checked: boolean): void {
    const selected = new Set(this.selectedIds());
    for (const project of this.filteredProjects()) {
      checked ? selected.add(project.id) : selected.delete(project.id);
    }
    this.selectedIds.set(selected);
  }

  updateBulkStatus(event: Event): void {
    this.bulkStatus.set((event.target as HTMLSelectElement).value as ProjectStatus);
  }

  applyBulkStatus(): void {
    const ids = [...this.selectedIds()];
    if (ids.length === 0) {
      return;
    }

    if (this.connectionState.demoMode()) {
      const now = new Date().toISOString();
      this.projects.update(projects => projects.map(project =>
        ids.includes(project.id)
          ? { ...project, status: this.bulkStatus(), lastModifiedDateTime: now }
          : project
      ));
      this.reportAction(
        this.translateService.instant('project.bulk-update-simulated', { count: ids.length }),
        'simulated'
      );
      this.bulkResult.set({
        requestedCount: ids.length,
        succeededCount: ids.length,
        failedCount: 0,
        failedIds: [],
        results: ids.map(id => ({ id, success: true, errorMessages: [] }))
      });
      this.selectedIds.set(new Set());
      return;
    }

    this.bulkUpdating.set(true);
    this.projectApi.bulkUpdateStatus({
      ids,
      status: this.bulkStatus(),
      continueOnError: true
    }).subscribe({
      next: result => {
        this.connectionState.markConnected();
        this.bulkResult.set(result);
        this.reportAction(
          this.translateService.instant('project.bulk-update-completed', {
            succeeded: result.succeededCount,
            failed: result.failedCount
          }),
          result.failedCount > 0 ? 'warning' : 'success'
        );
        this.selectedIds.set(new Set());
        this.bulkUpdating.set(false);
        this.loadProjects();
      },
      error: error => {
        this.connectionState.markFailure(error);
        this.reportAction(
          this.translateService.instant('project.bulk-update-failed'),
          'error'
        );
        this.bulkUpdating.set(false);
      }
    });
  }

  statusKey(status: ProjectStatus): string {
    return projectStatusKey(status);
  }

  private replaceProject(updated: ProjectViewModel): void {
    this.projects.update(projects => projects.map(project =>
      project.id === updated.id ? updated : project
    ));
  }

  private reportAction(message: string, kind: ActionKind): void {
    this.lastActionKind.set(kind);
    this.lastAction.set(message);
  }

  private setProjectUpdating(id: string, updating: boolean): void {
    this.updatingProjectIds.update(ids => {
      const next = new Set(ids);
      updating ? next.add(id) : next.delete(id);
      return next;
    });
  }

  private recordModalEvent(event: string): void {
    this.modalLifecycle.update(events => [event, ...events].slice(0, 6));
  }
}
