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

/** The ids of projects, runs and divisions in the address. */
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The id of an agent in the address. */
const agentIdPattern = /^[a-z0-9][a-z0-9._-]{0,99}$/i;

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
 * FB80: the inbox offers "Decide all" for its whole filter, such as one kind of action. It first shows
 * what it decides, how many times which kind of action on how many projects, and leaves the equipment
 * orders, whose tool requires that each is decided on its own; then it decides exactly that set, each
 * proposal with its own rules, and shows what succeeded, what was refused and what had changed.
 *
 * SPM-277: "Compact list" shows the inbox with `density: 'compact'`: one row per proposal that expands
 * to its card, one tab stop with the arrow keys, and the list's letters (`j`, `k`, `x`, `a`, `r`, `?`)
 * while the focus is in a row; `a` and `r` decide the focused row, never the selection.
 *
 * SPM-304: the count of an agent's open proposals on the compact Project management board opens this
 * page with `?agent=` and, on a division's board, `?division=` (`openInbox` of the board); the inbox
 * then lists that agent's proposals in that scope until the person shows all again, and records no
 * visit, because it shows only part of the inbox.
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
  /** SPM-277: whether the inbox shows one compact row per proposal, with the list's keys. */
  readonly compactList = signal(false);
  /** SPM-310: the run a clicked notification is about; the inbox then lists that run's proposals. */
  readonly runId = signal<string | null>(null);
  /** SPM-304: the agent whose open proposals the board's count asked for. */
  readonly boardAgentId = signal<string | null>(null);
  /** SPM-304: the division of the board that asked, if any. */
  readonly boardDivisionId = signal<string | null>(null);
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

    // A subject link names the project; following one on this page keeps the page and switches. The
    // agent and division a board asked for change what the inbox lists, so its counts are read again.
    let counted = false;
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(parameters => {
        const linked = parameters.get('project');
        if (linked && idPattern.test(linked)) {
          this.projectId.set(linked);
        }
        const run = parameters.get('run');
        this.runId.set(run && idPattern.test(run) ? run : null);

        const agent = parameters.get('agent');
        const division = parameters.get('division');
        const boardAgentId = agent && agentIdPattern.test(agent) ? agent : null;
        const boardDivisionId = division && idPattern.test(division) ? division : null;
        const boardChanged = boardAgentId !== this.boardAgentId() || boardDivisionId !== this.boardDivisionId();
        this.boardAgentId.set(boardAgentId);
        this.boardDivisionId.set(boardDivisionId);
        if (!counted || boardChanged) {
          counted = true;
          this.loadSummary();
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

  /**
   * FB78: counts what the inbox lists with the page's agent, type and division, or with the agent and
   * division a board asked for (SPM-304), so the kinds of action it offers are those that wait. A
   * summary that cannot be read leaves the filter out.
   */
  loadSummary(): void {
    const boardAgentId = this.boardAgentId();
    const activeDivisionId = this.activeDivisionOnly() ? this.activeDivisionId ?? undefined : undefined;
    this.agentRuns
      .getInboxSummary({
        agentId: boardAgentId ?? this.agentId,
        subjectType: boardAgentId ? undefined : 'project',
        divisionId: this.boardDivisionId() ?? activeDivisionId
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

  /**
   * SPM-310 and SPM-304: leaves the run a notification opened, or the agent a board asked for, and shows
   * the whole inbox again.
   */
  showAllProposals(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { run: null, agent: null, division: null },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }
}
