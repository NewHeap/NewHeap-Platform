import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { NhAssistantPanelService, NhAssistantStore } from '@newheap/platform-ai-chat';
import { NhAssistantAgentFeedbackReportComponent } from '@newheap/platform-ai-chat/admin';
import {
  NhAssistantAgentInboxComponent,
  NhAssistantAgentThreadViewComponent
} from '@newheap/platform-ai-chat/agent-runs';
import { finalize } from 'rxjs/operators';
import {
  ProjectApiService,
  ProjectCollectionRequestOptions,
  ProjectViewModel
} from 'sample-project-management-common';

const projectIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * SPM-277: the intake reviewer's work in the management portal. The inbox lists the proposals
 * that wait for the signed-in person across projects; the thread view shows one project's
 * proposals, runs, working memory and files, and lets participants start the reviewer.
 *
 * SPM-301: this page is where a project's agent work opens. The server names the project type and
 * links every project the assistant shows here, with `OpenAt("/management/intake-reviews?project={id}")`;
 * the page chooses the project of the link, also when a link in its own inbox is followed.
 *
 * Rejecting a proposal in either view asks, optionally and after the decision, what was wrong.
 * Assistant administrators also see the reviewer's feedback report: the server reports
 * `canAdminister` from the assistant admin policy `app.project.edit-or-admin` (manage
 * permission or administrator role), and the report endpoints enforce the same policy.
 */
@Component({
  selector: 'app-intake-reviews-page',
  standalone: true,
  imports: [
    CommonModule,
    TranslateModule,
    NhAssistantAgentInboxComponent,
    NhAssistantAgentThreadViewComponent,
    NhAssistantAgentFeedbackReportComponent
  ],
  templateUrl: './intake-reviews-page.component.html',
  styleUrl: './intake-reviews-page.component.scss'
})
export class IntakeReviewsPageComponent implements OnInit {
  private readonly projectApi = inject(ProjectApiService);
  private readonly panel = inject(NhAssistantPanelService);
  private readonly assistant = inject(NhAssistantStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly agentId = 'sample-project-intake-reviewer';
  readonly projects = signal<ProjectViewModel[]>([]);
  readonly projectId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  /** The feedback report is for assistant administrators only. */
  readonly canAdminister = this.assistant.canAdminister;

  ngOnInit(): void {
    // The portal's launcher normally starts the assistant scope already; starting it twice is safe.
    void this.assistant.initialize();

    // A subject link names the project; following one on this page keeps the page and switches.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(parameters => {
        const linked = parameters.get('project');
        if (linked && projectIdPattern.test(linked)) {
          this.projectId.set(linked);
        }
      });

    this.projectApi.list(new ProjectCollectionRequestOptions({ itemsPerPage: 100 }))
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: response => {
          this.projects.set(response.items);
          if (!this.projectId()) {
            this.projectId.set(response.items[0]?.id ?? null);
          }
        },
        error: () => this.loadFailed.set(true)
      });
  }

  onProjectChange(event: Event): void {
    const projectId = (event.target as HTMLSelectElement).value || null;
    this.projectId.set(projectId);
    // The address names the chosen project, as the assistant's links to it do.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { project: projectId },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
  }
}
