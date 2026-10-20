import { CommonModule } from '@angular/common';
import { Component, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  MutationType,
  NhCommonModule,
  NhModalMutateBaseComponent,
  TaskResult
} from '@newheap/platform-common';
import { NhAssistantPanelService, NhAssistantStore } from '@newheap/platform-ai-chat';
import {
  NhAssistantObjectAgentsComponent,
  NhAssistantSubjectSuggestionsComponent
} from '@newheap/platform-ai-chat/agent-runs';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import {
  ProjectApiService,
  ProjectMutateModel,
  ProjectStatus,
  ProjectViewModel,
  getProjectStatusOptions
} from 'sample-project-management-common';

let nextProjectFormId = 0;

/**
 * Creates or edits a project. An existing project also shows its agent work (SPM-280): the open
 * proposals of every agent on the project with the viewer's own decision right, and the
 * conversation with the intake reviewer about the project. The suggestions load in an `@defer`
 * block, so the `agent-runs` entry point stays out of the portal's initial bundle.
 *
 * SPM-303: below them `nh-assistant-object-agents` lists the agents that work on projects with their
 * latest result and open proposals; the project's participants run one now, and its work on the
 * project opens on demand.
 */
@Component({
  selector: 'app-project-edit-modal',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NhCommonModule,
    TranslateModule,
    NhAssistantSubjectSuggestionsComponent,
    NhAssistantObjectAgentsComponent
  ],
  templateUrl: './project-edit-modal.component.html',
  styleUrl: './project-edit-modal.component.scss'
})
export class ProjectEditModalComponent
  extends NhModalMutateBaseComponent<ProjectMutateModel, ProjectViewModel> {
  readonly project = input<ProjectViewModel>();
  readonly demoMode = input(false);
  readonly lifecycleReporter = input<(event: string) => void>(() => undefined);
  readonly statusOptions = getProjectStatusOptions(inject(TranslateService), false);
  /** The agent whose conversation about the project the dialog offers. */
  readonly intakeReviewerId = 'sample-project-intake-reviewer';
  /** Agent work shows only while the assistant is enabled and the access policy allows the user. */
  readonly assistantEnabled = inject(NhAssistantStore).enabled;
  private readonly panel = inject(NhAssistantPanelService);
  /** The footer's save button sits outside the form and submits it by id. */
  readonly formId = `project-edit-form-${++nextProjectFormId}`;

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

  /**
   * The assistant panel opened the conversation with the reviewer about this project. The panel is
   * a drawer below this dialog's layer, so the dialog closes to make room for it.
   */
  onConversationOpened(): void {
    this.lifecycleReporter()('content event: agent conversation opened');
    this.closeDialog();
  }

  /**
   * An agent's conversation about the project opens in the assistant panel, from a run's receipt or
   * from its work; the dialog closes to make room for the drawer below its layer.
   */
  onAgentConversation(conversationId: string): void {
    this.lifecycleReporter()('content event: agent conversation opened');
    this.closeDialog();
    this.panel.open(conversationId);
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
