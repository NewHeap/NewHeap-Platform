import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
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

/**
 * SPM-277: the intake reviewer's work in the management portal. The inbox lists the proposals
 * that wait for the signed-in person across projects; the thread view shows one project's
 * proposals, runs, working memory and files, and lets participants start the reviewer.
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

    this.projectApi.list(new ProjectCollectionRequestOptions({ itemsPerPage: 100 }))
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: response => {
          this.projects.set(response.items);
          this.projectId.set(response.items[0]?.id ?? null);
        },
        error: () => this.loadFailed.set(true)
      });
  }

  onProjectChange(event: Event): void {
    this.projectId.set((event.target as HTMLSelectElement).value || null);
  }

  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
  }
}
