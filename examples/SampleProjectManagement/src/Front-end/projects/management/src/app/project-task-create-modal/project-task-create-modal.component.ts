import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  MutationType,
  NhCommonModule,
  NhModalMutateBaseComponent,
  TaskResult
} from '@newheap/platform-common';
import { TranslateModule } from '@ngx-translate/core';
import {
  ProjectApiService,
  ProjectTaskApiService,
  ProjectTaskMutateModel,
  ProjectTaskViewModel,
  ProjectViewModel
} from 'sample-project-management-common';

/** What the assistant's client tool fills the form with (SPM-400). */
export interface ProjectTaskPrefill {
  projectId: string;
  title: string;
}

let nextTaskFormId = 0;

/**
 * SPM-400: creates a task of one project. The assistant's `sample-ui.create-task` client tool opens it
 * prefilled through `nhAssistantModalTool`; the person reviews the title and saves it through
 * `POST project-tasks` with their own rights, so nothing changes until they do. The form shows the
 * project it belongs to, and saves nothing while that project cannot be read.
 */
@Component({
  selector: 'app-project-task-create-modal',
  standalone: true,
  imports: [FormsModule, NhCommonModule, TranslateModule],
  templateUrl: './project-task-create-modal.component.html',
  styleUrl: './project-task-create-modal.component.scss'
})
export class ProjectTaskCreateModalComponent
  extends NhModalMutateBaseComponent<ProjectTaskMutateModel, ProjectTaskViewModel> {
  readonly prefill = input<ProjectTaskPrefill | null>(null);
  /** The project of the task, once read; `null` while it loads or when it cannot be read. */
  readonly project = signal<ProjectViewModel | null>(null);
  readonly projectState = signal<'loading' | 'ready' | 'failed'>('loading');
  /** The footer's save button sits outside the form and submits it by id. */
  readonly formId = `project-task-form-${++nextTaskFormId}`;
  private readonly projectApi = inject(ProjectApiService);
  private readonly taskApi = inject(ProjectTaskApiService);

  override async appOnInit(): Promise<void> {
    await this.newFormData(MutationType.Create);
    // The project shows next to the form; only saving waits for it.
    void this.loadProject().catch(() => this.projectState.set('failed'));
  }

  override async onNewFormData(): Promise<ProjectTaskMutateModel> {
    const prefill = this.prefill();
    return {
      projectId: prefill?.projectId ?? '',
      title: (prefill?.title ?? '').slice(0, 180),
      isCompleted: false
    };
  }

  override async onSubmitCreate(): Promise<TaskResult<ProjectTaskViewModel>> {
    return await this.taskApi.createTask(this.formData!).taskResultLastValueFrom();
  }

  override async onSubmitUpdate(): Promise<TaskResult<ProjectTaskViewModel>> {
    // The form only creates tasks.
    return this.onSubmitCreate();
  }

  private async loadProject(): Promise<void> {
    const projectId = this.prefill()?.projectId;
    if (!projectId) {
      this.projectState.set('failed');
      return;
    }

    const project = await this.projectApi.getById(projectId).lastValueFrom();
    this.project.set(project ?? null);
    this.projectState.set(project ? 'ready' : 'failed');
  }
}
