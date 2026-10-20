import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentBoard,
  AgentBoardAgent,
  LiveAgentWorkChanged,
  NH_ASSISTANT_SYSTEM_SUBJECT_TYPE,
  NhAssistantAgentRunApiService,
  NhAssistantApiError,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantMarkdownPipe as NhAssistantMarkdownPipe,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { NhAssistantBoardHistoryComponent } from './internal/nh-assistant-board-history.component';
import {
  NhAssistantRunNowAgent,
  NhAssistantRunNowDialogComponent,
  NhAssistantRunNowSubject
} from './internal/nh-assistant-run-now-dialog.component';
import { NhAssistantAgentInboxComponent } from './nh-assistant-agent-inbox.component';
import { NhAssistantRunResultComponent } from './nh-assistant-run-result.component';

/** What a board tile shows below the agent: its latest result, its proposals or its history. */
export type NhAssistantBoardPanel = 'result' | 'proposals' | 'history';

let nextId = 0;

/**
 * The board of one group of agents, for a page of the application such as "Project management"
 * (K15): per agent a tile with what it works on, its latest result for the whole application rendered
 * with the assistant's safe Markdown rendering, "New" for what arrived since the person's last visit
 * (K16), "Run now" for people who may run it, its open proposals in the inbox, and its history with
 * every result on demand. Showing the board records the visit, so the next visit marks what is new
 * after this one. The server decides which agents the person sees: an agent that works on the whole
 * application by its view policy, by default its required policy. With `divisionId` the board shows
 * the work of agents that work per division in that division. With live updates the board reloads by
 * itself when its agents' work changes.
 */
@Component({
  selector: 'nh-assistant-agent-board',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantMarkdownPipe,
    NhAssistantRunNowDialogComponent,
    NhAssistantRunResultComponent,
    NhAssistantAgentInboxComponent,
    NhAssistantBoardHistoryComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-board.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentBoardComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly translate = inject(TranslateService);
  private subscription?: Subscription;
  private visitSubscription?: Subscription;
  /** The board and scope whose visit this view recorded; the visit is recorded once per board shown. */
  private visited: string | null = null;
  /**
   * What the first read of the shown board found: the visit before this one and the agents that were
   * new then. Later reads, after live updates or a start, compare with the visit this view recorded,
   * so the view keeps both: what was new when the board was opened stays new while it is shown.
   */
  private opened: { previousVisit: string | null; newAgents: ReadonlySet<string> } | null = null;

  /** The group whose board is shown, such as `project-management`, as the application registered it. */
  readonly group = input.required<string>();
  /** The division whose board is shown; without it the board of the whole application. */
  readonly divisionId = input<string | null>(null);
  /** Emits a conversation a result or proposal offers to open, so the host can open it in the assistant panel. */
  readonly openConversation = output<string>();

  readonly id = `nh-assistant-agent-board-${nextId++}`;
  readonly codeKeys = nhAssistantCodeKeys;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  /** The board does not exist or the person sees none of its agents. */
  readonly notFound = signal(false);
  /** The board as shown: new since the visit before this one, also after the visit was recorded. */
  readonly board = signal<AgentBoard | null>(null);
  readonly agents = computed(() => this.board()?.agents ?? []);
  /** Per agent what its tile shows below it; one panel per tile. */
  readonly panels = signal<Readonly<Record<string, NhAssistantBoardPanel>>>({});
  /** The agent the run-now dialog runs; `null` while it is closed. */
  readonly runningNow = signal<AgentBoardAgent | null>(null);
  readonly runNowAgent = computed<NhAssistantRunNowAgent | null>(() => {
    const agent = this.runningNow();
    return agent ? { id: agent.agentId, name: this.nameOf(agent) } : null;
  });
  /** The whole application in the board's scope: the run-now dialog runs on it without asking. */
  readonly runNowSubject = computed<NhAssistantRunNowSubject | null>(() => {
    const agent = this.runningNow();
    return agent?.subjectType && agent.subjectId
      ? { type: agent.subjectType, id: agent.subjectId, displayName: agent.subjectDisplayName }
      : null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.visitSubscription?.unsubscribe();
    });
    effect(() => {
      this.group();
      this.divisionId();
      untracked(() => {
        this.panels.set({});
        this.runningNow.set(null);
        this.fetch(false);
      });
    });
    nhAssistantReloadOnAgentWork(change => this.concerns(change), () => this.fetch(true));
  }

  load(): void {
    this.fetch(false);
  }

  /** The agent's name: a translation key of an agent in code, or the text of one created as data. */
  nameOf(agent: AgentBoardAgent): string {
    const translated = this.translate.instant(agent.displayNameKey) as string;
    return translated && translated !== agent.displayNameKey ? translated : agent.displayNameKey;
  }

  /** What the agent works on, in the reader's language. */
  worksOnOf(agent: AgentBoardAgent): string {
    const worksOn = agent.worksOn;
    const parts: string[] = worksOn.allObjects
      ? [this.translate.instant('nh-assistant.agent-runs.board.works-on-all') as string]
      : worksOn.subjectTypes.map(type => type.pluralName ?? type.name);
    if (worksOn.application) {
      parts.push(this.translate.instant(worksOn.perDivision
        ? 'nh-assistant.agent-runs.board.works-on-application-per-division'
        : 'nh-assistant.agent-runs.board.works-on-application') as string);
    }
    return this.translate.instant('nh-assistant.agent-runs.board.works-on', { list: parts.join(', ') }) as string;
  }

  panelOf(agent: AgentBoardAgent): NhAssistantBoardPanel | null {
    return this.panels()[agent.agentId] ?? null;
  }

  toggle(agent: AgentBoardAgent, panel: NhAssistantBoardPanel): void {
    this.panels.update(panels => {
      const next = { ...panels };
      if (next[agent.agentId] === panel) {
        delete next[agent.agentId];
      } else {
        next[agent.agentId] = panel;
      }
      return next;
    });
  }

  runNow(agent: AgentBoardAgent): void {
    this.runningNow.set(agent);
  }

  /** A run started: the board shows it as the agent's latest run. */
  onStarted(): void {
    this.fetch(true);
  }

  proposalsKey(count: number): string {
    switch (count) {
      case 0:
        return 'nh-assistant.agent-runs.object-agents.proposals-none';
      case 1:
        return 'nh-assistant.agent-runs.object-agents.proposals-one';
      default:
        return 'nh-assistant.agent-runs.object-agents.proposals';
    }
  }

  /** The beginning of a result as Markdown, marked when the result goes on. */
  previewOf(result: string, truncated: boolean): string {
    return truncated ? `${result}…` : result;
  }

  /**
   * Whether a live update concerns the board: the work in one of its agents' threads, or a run of the
   * whole application that may have started an agent's first thread.
   */
  private concerns(change: LiveAgentWorkChanged): boolean {
    return this.agents().some(agent => agent.conversationId === change.conversationId)
      || (change.subjectType === NH_ASSISTANT_SYSTEM_SUBJECT_TYPE && change.reason === 'run');
  }

  /**
   * Reads the board. A quiet reload, after a live update or a start, keeps what is shown when it
   * fails and shows no loading state. The first time a board is shown, its visit is recorded after it
   * was read, so it still marks what arrived since the visit before.
   */
  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
      this.notFound.set(false);
    }

    const group = this.group();
    const divisionId = this.divisionId();
    this.subscription?.unsubscribe();
    this.subscription = this.api.getBoard(group, divisionId).subscribe({
      next: board => {
        this.board.set(this.asOpened(board, group, divisionId));
        this.loading.set(false);
        this.loadFailed.set(false);
        this.notFound.set(false);
        this.recordVisit(group, divisionId);
      },
      error: (failure: unknown) => {
        if (quiet && this.board() !== null) {
          return;
        }
        this.board.set(null);
        this.loading.set(false);
        const missing = failure instanceof NhAssistantApiError && failure.code === 'assistant-board-not-found';
        this.notFound.set(missing);
        this.loadFailed.set(!missing);
      }
    });
  }

  /**
   * The board read compared with the visit before the board was opened: the first read of a board
   * keeps its previous visit and new agents; later reads keep showing them as new.
   */
  private asOpened(board: AgentBoard, group: string, divisionId: string | null): AgentBoard {
    if (this.visited !== `${group}|${divisionId ?? ''}` || this.opened === null) {
      this.opened = {
        previousVisit: board.lastVisitedAt,
        newAgents: new Set(board.agents.filter(agent => agent.isNew).map(agent => agent.agentId))
      };
      return board;
    }

    const opened = this.opened;
    return {
      ...board,
      lastVisitedAt: opened.previousVisit,
      agents: board.agents.map(agent => agent.isNew || !opened.newAgents.has(agent.agentId) ? agent : { ...agent, isNew: true })
    };
  }

  private recordVisit(group: string, divisionId: string | null): void {
    const key = `${group}|${divisionId ?? ''}`;
    if (this.visited === key) {
      return;
    }

    this.visited = key;
    this.visitSubscription?.unsubscribe();
    // A visit that cannot be recorded only means the next visit shows more as new.
    this.visitSubscription = this.api.visitBoard(group, divisionId).subscribe({ error: () => undefined });
  }
}
