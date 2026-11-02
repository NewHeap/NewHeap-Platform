import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import {
  AgentInboxSort,
  AgentInboxSummary,
  NhAssistantAgentRunApiService,
  NhAssistantPanelService,
  NhAssistantStore
} from '@newheap/platform-ai-chat';
import { NhAssistantAgentFeedbackReportComponent } from '@newheap/platform-ai-chat/admin';
import {
  NhAssistantAgentInboxComponent,
  NhAssistantAgentThreadViewComponent
} from '@newheap/platform-ai-chat/agent-runs';
import { finalize } from 'rxjs/operators';
import {
  ProjectApiService,
  ProjectCollectionRequestOptions,
  ProjectViewModel,
  SampleAuthService
} from 'sample-project-management-common';

/** The ids of projects and runs in the address. */
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * SPM-277: the intake reviewer's work in the management portal. The inbox lists the proposals
 * that wait for the signed-in person across projects; the thread view shows one project's
 * proposals, runs, working memory and files, and lets participants start the reviewer.
 *
 * SPM-301: this page is where a project's agent work opens. The server names the project type and
 * links every project the assistant shows here, with `OpenAt("/management/intake-reviews?project={id}")`;
 * the page chooses the project of the link, also when a link in its own inbox is followed.
 *
 * SPM-310: the inbox is grouped. The project resolver names each project's division as its parent,
 * which every proposal records, so the inbox shows a division with its projects' proposals, per
 * project and run, says how many of a group it shows and selects only those with "Select group". It
 * marks what came since the person's last visit, and can show only the active division's proposals.
 * A clicked notification about the proposals of a run (`NhAssistantPushService.openRun$`, which the
 * portal's shell subscribes to) opens this page with `?run=`; the inbox then lists every proposal of
 * that run the person may decide, of any agent and object, until the person shows all again.
 *
 * FB78: a filter per kind of action over the inbox. `getInboxSummary` counts what the inbox lists
 * with the page's own filter, per kind of action named as its cards name it, and how many expire within
 * 24 hours; the page offers those kinds and the expiry as filters and labels the counts "listed",
 * because deciding still checks each proposal on its own. The inbox takes the chosen kind (`toolId`),
 * the expiry (`expiresBefore`) and an order (`sort`).
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
  private readonly agentRuns = inject(NhAssistantAgentRunApiService);

  readonly agentId = 'sample-project-intake-reviewer';
  /** SPM-310: the division the person works in now, for the inbox's division filter. */
  readonly activeDivisionId = inject(SampleAuthService).getAuthorization()?.activeDivision?.id ?? null;
  /** SPM-310: whether the inbox shows the active division's proposals only. */
  readonly activeDivisionOnly = signal(false);
  /** SPM-310: the run a clicked notification is about; the inbox then lists that run's proposals. */
  readonly runId = signal<string | null>(null);
  /** FB78: what the inbox lists with the page's filter, counted per kind of action; `null` while unknown. */
  readonly summary = signal<AgentInboxSummary | null>(null);
  /** FB78: the kind of action the inbox shows, or `null` for every kind. */
  readonly toolId = signal<string | null>(null);
  /** FB78: whether the inbox shows only proposals that expire within 24 hours. */
  readonly expiringSoonOnly = signal(false);
  /** FB78: the order of the inbox, or `null` for its own: the soonest expiry first. */
  readonly sort = signal<AgentInboxSort | null>(null);
  /** FB78: until when a proposal counts as expiring soon, from the summary. */
  readonly expiresBefore = computed(() => (this.expiringSoonOnly() ? this.summary()?.expiringBefore ?? null : null));
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
        if (linked && idPattern.test(linked)) {
          this.projectId.set(linked);
        }
        const run = parameters.get('run');
        this.runId.set(run && idPattern.test(run) ? run : null);
      });

    this.loadSummary();

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

  /**
   * FB78: counts what the inbox lists with the page's agent, type and division, so the kinds of action
   * it offers are those that wait. A summary that cannot be read leaves the filter out.
   */
  loadSummary(): void {
    this.agentRuns
      .getInboxSummary({
        agentId: this.agentId,
        subjectType: 'project',
        divisionId: this.activeDivisionOnly() ? this.activeDivisionId ?? undefined : undefined
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: summary => {
          this.summary.set(summary);
          // A kind that no longer waits is no longer offered, so the inbox shows every kind again.
          if (this.toolId() && !summary.tools.some(tool => tool.toolId === this.toolId())) {
            this.toolId.set(null);
          }
        },
        error: () => this.summary.set(null)
      });
  }

  /** SPM-310: shows the active division's proposals only, or all again, with their counts. */
  toggleActiveDivisionOnly(): void {
    this.activeDivisionOnly.set(!this.activeDivisionOnly());
    this.loadSummary();
  }

  onToolChange(event: Event): void {
    this.toolId.set((event.target as HTMLSelectElement).value || null);
  }

  onSortChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.sort.set(value === 'expiresAt' || value === 'createdAt' ? value : null);
  }

  openConversation(conversationId: string): void {
    this.panel.open(conversationId);
  }

  /** SPM-310: leaves the run a notification opened and shows the whole inbox again. */
  showAllProposals(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { run: null },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }
}
