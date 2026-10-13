import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { finalize } from 'rxjs/operators';
import {
  PROJECT_MEMBER_ROLES,
  ProjectApiService,
  ProjectCollectionRequestOptions,
  ProjectMemberApiService,
  ProjectMemberRole,
  ProjectMemberViewModel,
  ProjectViewModel
} from 'sample-project-management-common';

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * SPM-270: the people on a project and their role. Readers see the project's agent threads,
 * participants also type in them and cancel runs, approvers also decide the intake reviewer's
 * proposals. The server checks every role live, so a change applies at once.
 */
@Component({
  selector: 'app-project-members-page',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './project-members-page.component.html',
  styleUrl: './project-members-page.component.scss'
})
export class ProjectMembersPageComponent implements OnInit {
  private readonly projectApi = inject(ProjectApiService);
  private readonly memberApi = inject(ProjectMemberApiService);

  readonly roles = PROJECT_MEMBER_ROLES;
  readonly projects = signal<ProjectViewModel[]>([]);
  readonly projectId = signal<string | null>(null);
  readonly members = signal<ProjectMemberViewModel[]>([]);
  readonly loadingProjects = signal(true);
  readonly loadingMembers = signal(false);
  readonly saving = signal(false);
  readonly errorKey = signal<string | null>(null);
  readonly newUserId = signal('');
  readonly newRole = signal<ProjectMemberRole>(ProjectMemberRole.Reader);

  readonly selectedProject = computed(() =>
    this.projects().find(project => project.id === this.projectId()) ?? null);
  readonly canAdd = computed(() =>
    !!this.projectId() && GUID_PATTERN.test(this.newUserId().trim()) && !this.saving());

  ngOnInit(): void {
    this.projectApi.list(new ProjectCollectionRequestOptions({ itemsPerPage: 100 }))
      .pipe(finalize(() => this.loadingProjects.set(false)))
      .subscribe({
        next: response => {
          this.projects.set(response.items);
          const first = response.items[0];
          if (first) {
            this.selectProject(first.id);
          }
        },
        error: error => this.errorKey.set(this.errorKeyFor(error))
      });
  }

  selectProject(projectId: string): void {
    this.projectId.set(projectId || null);
    this.members.set([]);
    this.errorKey.set(null);
    if (projectId) {
      this.loadMembers(projectId);
    }
  }

  onProjectChange(event: Event): void {
    this.selectProject((event.target as HTMLSelectElement).value);
  }

  onUserIdInput(event: Event): void {
    this.newUserId.set((event.target as HTMLInputElement).value);
  }

  onNewRoleChange(event: Event): void {
    this.newRole.set((event.target as HTMLSelectElement).value as ProjectMemberRole);
  }

  addMember(): void {
    const projectId = this.projectId();
    if (!projectId || !this.canAdd()) {
      return;
    }

    this.saving.set(true);
    this.errorKey.set(null);
    this.memberApi.addMember({ projectId, userId: this.newUserId().trim(), role: this.newRole() })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: () => {
          this.newUserId.set('');
          this.loadMembers(projectId);
        },
        error: error => this.errorKey.set(this.errorKeyFor(error))
      });
  }

  changeRole(member: ProjectMemberViewModel, event: Event): void {
    const role = (event.target as HTMLSelectElement).value as ProjectMemberRole;
    if (role === member.role) {
      return;
    }

    this.saving.set(true);
    this.errorKey.set(null);
    this.memberApi.changeMember(member.id, { projectId: member.projectId, userId: member.userId, role })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: () => this.loadMembers(member.projectId),
        error: error => {
          this.errorKey.set(this.errorKeyFor(error));
          this.loadMembers(member.projectId);
        }
      });
  }

  removeMember(member: ProjectMemberViewModel): void {
    this.saving.set(true);
    this.errorKey.set(null);
    this.memberApi.removeMember(member.id)
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: () => this.loadMembers(member.projectId),
        error: error => this.errorKey.set(this.errorKeyFor(error))
      });
  }

  roleKey(role: ProjectMemberRole): string {
    return `project-members.role-${role.toLowerCase()}`;
  }

  private loadMembers(projectId: string): void {
    this.loadingMembers.set(true);
    this.memberApi.listForProject(projectId)
      .pipe(finalize(() => this.loadingMembers.set(false)))
      .subscribe({
        next: response => {
          // Ignore a late answer for a project that is no longer selected.
          if (this.projectId() === projectId) {
            this.members.set(response.items);
          }
        },
        error: error => this.errorKey.set(this.errorKeyFor(error))
      });
  }

  private errorKeyFor(error: { status?: number } | null | undefined): string {
    switch (error?.status) {
      case 400:
        return 'project-members.error-validation';
      case 401:
      case 403:
        return 'project-members.error-forbidden';
      default:
        return 'project-members.error-offline';
    }
  }
}
