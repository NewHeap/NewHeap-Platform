import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { NhAssistantPanelService } from '@newheap/platform-ai-chat';
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
 */
@Component({
  selector: 'app-intake-reviews-page',
  standalone: true,
  imports: [CommonModule, TranslateModule, NhAssistantAgentInboxComponent, NhAssistantAgentThreadViewComponent],
  templateUrl: './intake-reviews-page.component.html',
  styleUrl: './intake-reviews-page.component.scss'
})
export class IntakeReviewsPageComponent implements OnInit {
  private readonly projectApi = inject(ProjectApiService);
  private readonly panel = inject(NhAssistantPanelService);

  readonly agentId = 'sample-project-intake-reviewer';
  readonly projects = signal<ProjectViewModel[]>([]);
  readonly projectId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly loadFailed = signal(false);

  ngOnInit(): void {
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
