import { CommonModule } from '@angular/common';
import { Component, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  MutationType,
  NhCommonModule,
  NhModalMutateBaseComponent,
  TaskResult
} from '@newheap/platform-common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import {
  ProjectApiService,
  ProjectMutateModel,
  ProjectStatus,
  ProjectViewModel,
  getProjectStatusOptions
} from 'sample-project-management-common';

@Component({
  selector: 'app-project-edit-modal',
  standalone: true,
  imports: [CommonModule, FormsModule, NhCommonModule, TranslateModule],
  templateUrl: './project-edit-modal.component.html',
  styleUrl: './project-edit-modal.component.scss'
})
export class ProjectEditModalComponent
  extends NhModalMutateBaseComponent<ProjectMutateModel, ProjectViewModel> {
  readonly project = input<ProjectViewModel>();
  readonly demoMode = input(false);
  readonly lifecycleReporter = input<(event: string) => void>(() => undefined);
  readonly statusOptions = getProjectStatusOptions(inject(TranslateService), false);

  constructor(private readonly projectApi: ProjectApiService) {
    super();
  }

  override async appOnInit(): Promise<void> {
    this.lifecycleReporter()('content appOnInit: formdata initialiseren');
    await this.newFormData(
      this.project() ? MutationType.Update : MutationType.Create
    );
  }

  override async appAfterViewInit(): Promise<void> {
    this.lifecycleReporter()('content appAfterViewInit: modalview gereed');
  }

  override async appOnDestroy(): Promise<void> {
    this.lifecycleReporter()('content appOnDestroy: cleanup uitgevoerd');
  }

  override async onNewFormData(
    mutationType: MutationType
  ): Promise<ProjectMutateModel> {
    const project = this.project();

    if (mutationType === MutationType.Update && project) {
      return {
        divisionId: project.divisionId,
        key: project.key,
        name: project.name,
        description: project.description,
        status: project.status,
        deadline: project.deadline
      };
    }

    return {
      divisionId: 'b14a1178-8bd7-4e87-845f-e0d89b63f099',
      key: '',
      name: '',
      description: '',
      status: ProjectStatus.Draft,
      deadline: null
    };
  }

  override async onSubmitCreate(): Promise<TaskResult<ProjectViewModel>> {
    if (this.demoMode()) {
      return this.completeDemoMutation();
    }

    const result = await this.projectApi
      .createProject(this.formData!)
      .taskResultLastValueFrom();
    return result;
  }

  override async onSubmitUpdate(): Promise<TaskResult<ProjectViewModel>> {
    if (this.demoMode()) {
      return this.completeDemoMutation();
    }

    const result = await this.projectApi
      .updateProject(this.project()!.id, this.formData!)
      .taskResultLastValueFrom();

    if (result.isSuccess) {
      result.data = await this.projectApi.getById(this.project()!.id).lastValueFrom();
    }

    return result;
  }

  private completeDemoMutation(): TaskResult<ProjectViewModel> {
    const now = new Date().toISOString();
    const current = this.project();
    const result = new TaskResult<ProjectViewModel>({
      data: {
        ...this.formData!,
        id: current?.id ?? crypto.randomUUID(),
        creationDateTime: current?.creationDateTime ?? now,
        lastModifiedDateTime: now
      }
    });

    return result;
  }
}
