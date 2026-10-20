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
  AgentObjectAgent,
  AgentObjectAgents,
  LiveAgentWorkChanged,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import {
  NhAssistantRunNowAgent,
  NhAssistantRunNowDialogComponent,
  NhAssistantRunNowSubject
} from './internal/nh-assistant-run-now-dialog.component';
import { NhAssistantAgentThreadViewComponent } from './nh-assistant-agent-thread-view.component';
import { NhAssistantRunResultComponent } from './nh-assistant-run-result.component';

let nextId = 0;

/**
 * Failures of the object itself: asking again does not help, the page names an object that does not
 * exist, that the person may not see, or that is no valid reference.
 */
const objectFailures = new Set(['assistant-subject-not-found', 'assistant-subject-invalid', 'assistant-subject-type-unknown']);

/**
 * The agents that work on one object, for the object's page, for example on a project: per agent its
 * latest run with the beginning of its result, the whole result on demand in `nh-assistant-run-result`,
 * its open proposals, "Run now" for people who may run it (the participants of the object; for the
 * whole application, `nh-system`, the agent's run policy), and on demand its work on the object in
 * `nh-assistant-agent-thread-view`. The server lists only the agents that work on the object and that
 * the person may use, and checks every start again. A failed load reads as its translated code, such as
 * an object that does not exist or that the person may not see, with "Try again" where asking again
 * can help. With live updates the list reloads by itself when an agent's work on the object changes.
 */
@Component({
  selector: 'nh-assistant-object-agents',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantRunNowDialogComponent,
    NhAssistantAgentThreadViewComponent,
    NhAssistantRunResultComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-object-agents.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantObjectAgentsComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly translate = inject(TranslateService);
  private subscription?: Subscription;

  /** The object's type, such as `project`, or `nh-system` for the whole application. */
  readonly subjectType = input.required<string>();
  /** The object's id, or `global` (or `division-…`) for the whole application. */
  readonly subjectId = input.required<string>();
  /** Emits the conversation id of an agent's thread, so the host can open it in the assistant panel. */
  readonly openConversation = output<string>();

  readonly id = `nh-assistant-object-agents-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  /** Why the agents could not be read; `null` after a load that worked. */
  readonly loadError = signal<NhAssistantError | null>(null);
  readonly loadErrorKeys = computed(() => nhAssistantErrorKeys(this.loadError()));
  /** Loading again can help after a failure of the connection or the server, not of the object. */
  readonly loadRetryable = computed(() => {
    const error = this.loadError();
    return error === null || !objectFailures.has(error.code);
  });
  readonly codeKeys = nhAssistantCodeKeys;
  readonly listing = signal<AgentObjectAgents | null>(null);
  readonly agents = computed(() => this.listing()?.agents ?? []);
  /** The agent whose work on the object is shown; one at a time. */
  readonly expanded = signal<string | null>(null);
  /** The agent whose latest result is shown in full; one at a time. */
  readonly resultShown = signal<string | null>(null);
  /** The agent the run-now dialog runs; `null` while it is closed. */
  readonly runningNow = signal<AgentObjectAgent | null>(null);
  readonly runNowAgent = computed<NhAssistantRunNowAgent | null>(() => {
    const agent = this.runningNow();
    return agent ? { id: agent.agentId, name: this.nameOf(agent) } : null;
  });
  /** The object of the page: the run-now dialog runs on it without asking. */
  readonly runNowSubject = computed<NhAssistantRunNowSubject | null>(() => {
    const listing = this.listing();
    return listing
      ? {
        type: listing.subjectType,
        id: listing.subjectId,
        displayName: listing.subjectDisplayName,
        typeName: listing.subjectTypeName ?? null,
        link: listing.subjectLink ?? null
      }
      : null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.subjectType();
      this.subjectId();
      untracked(() => {
        this.expanded.set(null);
        this.resultShown.set(null);
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
  nameOf(agent: AgentObjectAgent): string {
    const translated = this.translate.instant(agent.displayNameKey) as string;
    return translated && translated !== agent.displayNameKey ? translated : agent.displayNameKey;
  }

  toggleResult(agent: AgentObjectAgent): void {
    this.resultShown.update(current => current === agent.agentId ? null : agent.agentId);
  }

  toggleWork(agent: AgentObjectAgent): void {
    this.expanded.update(current => current === agent.agentId ? null : agent.agentId);
  }

  runNow(agent: AgentObjectAgent): void {
    this.runningNow.set(agent);
  }

  /** A run started: the list shows it as the agent's latest run. */
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

  /** Whether a live update concerns the object: its runs, proposals or feedback changed. */
  private concerns(change: LiveAgentWorkChanged): boolean {
    return change.subjectType === this.subjectType() && change.subjectId === this.subjectId();
  }

  /**
   * Reads the agents. A quiet reload, after a live update or a start, keeps what is shown when it
   * fails and shows no loading state.
   */
  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
      this.loadError.set(null);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api.getObjectAgents(this.subjectType(), this.subjectId()).subscribe({
      next: listing => {
        this.listing.set(listing);
        this.loading.set(false);
        this.loadFailed.set(false);
        this.loadError.set(null);
      },
      error: (error: unknown) => {
        if (quiet && this.listing() !== null) {
          return;
        }
        this.listing.set(null);
        this.loading.set(false);
        this.loadFailed.set(true);
        this.loadError.set(toNhAssistantError(error));
      }
    });
  }
}
