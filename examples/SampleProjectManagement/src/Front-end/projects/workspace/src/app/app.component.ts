import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import {
  PROJECT_DEMO_DATA,
  ProjectApiService,
  ProjectStatus,
  ProjectViewModel,
  SampleApiConnectionStateService,
  SampleAuthService,
  getProjectStatusOptions
} from 'sample-project-management-common';

@Component({
  selector: 'app-workspace-overview',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit {
  private readonly projectApi = inject(ProjectApiService);
  private readonly translateService = inject(TranslateService);
  private readonly connectionState = inject(SampleApiConnectionStateService);
  private readonly authService = inject(SampleAuthService);
  private readonly authorization = toSignal(this.authService.authSubject, {
    initialValue: this.authService.getAuthorization()
  });
  readonly projects = signal(PROJECT_DEMO_DATA);
  readonly statusMessage = signal('');
  readonly statusMessageKind = signal<'success' | 'error' | 'simulated'>('success');
  readonly loading = signal(true);
  readonly updatingProjectIds = signal<Set<string>>(new Set());
  readonly canManage = computed(() => {
    this.authorization();
    return this.authService.isOnePermissionGranted(['app.project.manage']);
  });
  readonly completedCount = computed(() =>
    this.projects().filter(project => project.status === ProjectStatus.Completed).length);
  readonly statusOptions = getProjectStatusOptions(
    this.translateService,
    false,
    [ProjectStatus.Archived]
  );

  readonly columns = [
    { status: ProjectStatus.Draft, key: 'draft' },
    { status: ProjectStatus.Active, key: 'active' },
    { status: ProjectStatus.OnHold, key: 'on-hold' },
    { status: ProjectStatus.Completed, key: 'completed' }
  ];

  ngOnInit(): void {
    this.projectApi.list().subscribe({
      next: response => {
        this.projects.set(response.items);
        this.connectionState.markConnected();
        this.loading.set(false);
      },
      error: error => {
        this.connectionState.markFailure(error);
        if (!this.connectionState.demoMode()) {
          this.reportStatus(this.translateService.instant('project.projects-load-failed'), 'error');
        }
        this.loading.set(false);
      }
    });
  }

  projectsFor(status: ProjectStatus): ProjectViewModel[] {
    return this.projects().filter(project => project.status === status);
  }

  move(project: ProjectViewModel, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const status = select.value as ProjectStatus;
    const localUpdate = () => this.projects.update(projects => projects.map(item =>
      item.id === project.id
        ? { ...item, status }
        : item
    ));

    if (this.connectionState.demoMode()) {
      localUpdate();
      this.reportStatus(
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
        this.reportStatus(
          this.translateService.instant('project.status-update-succeeded', { key: project.key }),
          'success'
        );
        this.setProjectUpdating(project.id, false);
      },
      error: error => {
        this.connectionState.markFailure(error);
        select.value = project.status;
        this.reportStatus(
          this.translateService.instant('project.status-update-failed', { key: project.key }),
          'error'
        );
        this.setProjectUpdating(project.id, false);
      }
    });
  }

  private reportStatus(message: string, kind: 'success' | 'error' | 'simulated'): void {
    this.statusMessageKind.set(kind);
    this.statusMessage.set(message);
  }

  private setProjectUpdating(id: string, updating: boolean): void {
    this.updatingProjectIds.update(ids => {
      const next = new Set(ids);
      updating ? next.add(id) : next.delete(id);
      return next;
    });
  }
}
