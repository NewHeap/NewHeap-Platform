import { DatePipe, DOCUMENT } from '@angular/common';
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
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, forkJoin } from 'rxjs';
import {
  AgentArtifact,
  AgentProposalDecisionRight,
  AgentRunResult,
  NH_ASSISTANT_MAX_PROPOSAL_LOOKUP,
  NhAssistantAgentRunApiService,
  NhAssistantApiError,
  NhAssistantError,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantMarkdownPipe as NhAssistantMarkdownPipe,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantSubjectLinkComponent as NhAssistantSubjectLinkComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAgentProposalsComponent } from './nh-assistant-agent-proposals.component';

let nextId = 0;

/** How long a run took, for people to read: a text key and its number. */
interface RunDuration {
  key: string;
  value: number;
}

/**
 * One run of an agent with its result: what it worked on, how it was started and ended, its usage
 * and duration, its whole answer, the proposals it made (also those it placed at other objects the
 * reader may read) and the files it saved. The answer is model output: it is rendered with the
 * assistant's safe Markdown rendering, which shows inline HTML as text and keeps only a strict set
 * of tags and links, never as HTML. The server decides who sees a run (`GET runs/{id}`): readers of
 * its object, for the whole application the agent's view policy, administrators every run. The
 * thread view's run timeline and the administration's runs view open it; an application may show
 * it anywhere with a run id. With live updates it reloads by itself while the run works.
 *
 * The run's pending proposals that the reader may decide, by the same live check as the inbox and the
 * subject's work (`POST approvals/lookup`), can be decided in place, one by one or selected together
 * with "Select all"; a pending proposal the reader may not decide says who does, and a reader who may
 * decide none sees no selection. After a rejection the list asks, optionally, what was wrong, and a
 * proposal refused because it no longer fits stays as a closed card with the reason until dismissed.
 */
@Component({
  selector: 'nh-assistant-run-result',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantMarkdownPipe,
    NhAssistantSubjectLinkComponent,
    NhAssistantAgentProposalsComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-run-result.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantRunResultComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly document = inject(DOCUMENT);
  private subscription?: Subscription;
  private rightsSubscription?: Subscription;

  /** The run to show. */
  readonly runId = input.required<string>();
  /**
   * Whether to offer the run's conversation; leave it off where that conversation is already shown,
   * as in its thread's run timeline.
   */
  readonly conversationLink = input(true);
  /** Emits the run's thread conversation, or the one of a run that placed a proposal, so the host can open it. */
  readonly openConversation = output<string>();

  readonly id = `nh-assistant-run-result-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  /** The run does not exist or the reader may not see it. */
  readonly notFound = signal(false);
  readonly detail = signal<AgentRunResult | null>(null);
  readonly downloadingId = signal<string | null>(null);
  /** Why the latest download failed, as a code and translation key; `null` after one that worked. */
  readonly downloadError = signal<NhAssistantError | null>(null);
  /** Whether the latest download failed. */
  readonly downloadFailed = computed(() => this.downloadError() !== null);
  /** The text of the download's failure, then the text that a file could not be downloaded. */
  readonly downloadErrorKeys = computed(() => {
    const keys = nhAssistantErrorKeys(this.downloadError());
    return keys.length > 0 ? [...keys.slice(0, -1), 'nh-assistant.agent-runs.artifacts.download-failed'] : [];
  });
  readonly codeKeys = nhAssistantCodeKeys;
  /**
   * The reader's live right to decide each pending proposal of the run, by approval id; `null` until
   * it is known, when nobody may decide.
   */
  readonly rights = signal<ReadonlyMap<string, AgentProposalDecisionRight> | null>(null);
  /** Whether the reader may decide at least one of the run's proposals: only then can they select. */
  readonly decidable = computed(() => [...(this.rights()?.values() ?? [])].some(right => right.canDecide));
  readonly duration = computed(() => {
    const run = this.detail()?.run;
    return run ? durationOf(run.startedAt, run.endedAt) : null;
  });
  readonly tokens = computed(() => {
    const run = this.detail()?.run;
    return run ? run.inputTokens + run.outputTokens : 0;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.rightsSubscription?.unsubscribe();
    });
    effect(() => {
      this.runId();
      untracked(() => this.fetch(false));
    });
    nhAssistantReloadOnAgentWork(
      change => change.runId === this.runId() || change.conversationId === this.detail()?.run.conversationId,
      () => this.fetch(true)
    );
  }

  load(): void {
    this.fetch(false);
  }

  /** A proposal was decided or its feedback given: the run's proposals and the rights are read again. */
  onProposalsChanged(): void {
    this.fetch(true);
  }

  async download(artifact: AgentArtifact): Promise<void> {
    this.downloadingId.set(artifact.id);
    this.downloadError.set(null);
    try {
      const blob = await this.api.downloadRunArtifact(this.runId(), artifact.id);
      const url = URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = `${artifact.name}-v${artifact.version}`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (error) {
      this.downloadError.set(toNhAssistantError(error));
    } finally {
      this.downloadingId.set(null);
    }
  }

  /**
   * Reads the reader's live right to decide each pending proposal of the run, as the server checks it
   * for every decision. Without an answer nobody may decide here; the server checks again anyway.
   */
  private readRights(detail: AgentRunResult): void {
    this.rightsSubscription?.unsubscribe();
    const batches = pendingBatches(detail);
    if (batches.length === 0) {
      this.rights.set(new Map());
      return;
    }

    this.rightsSubscription = forkJoin(batches.map(batch => this.api.lookupProposals(batch))).subscribe({
      next: lookups => this.rights.set(new Map(lookups
        .flatMap(lookup => lookup.items)
        .map(item => [item.proposal.approvalId, { canDecide: item.canDecide, reasonCode: item.reasonCode }] as const))),
      error: () => this.rights.set(new Map())
    });
  }

  /**
   * Reads the run. A quiet reload, after a live update, keeps what is shown when it fails and shows
   * no loading state.
   */
  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
      this.notFound.set(false);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api.getRunResult(this.runId()).subscribe({
      next: detail => {
        this.detail.set(detail);
        this.loading.set(false);
        this.loadFailed.set(false);
        this.notFound.set(false);
        this.readRights(detail);
      },
      error: (failure: unknown) => {
        if (quiet && this.detail() !== null) {
          return;
        }
        this.detail.set(null);
        this.loading.set(false);
        const missing = failure instanceof NhAssistantApiError && failure.code === 'assistant-run-not-found';
        this.notFound.set(missing);
        this.loadFailed.set(!missing);
      }
    });
  }
}

/** The ids of the run's pending proposals in lookups of at most the lookup's limit each. */
function pendingBatches(detail: AgentRunResult): string[][] {
  const ids = detail.proposals.filter(proposal => proposal.status === 'pending').map(proposal => proposal.approvalId);
  const batches: string[][] = [];
  for (let start = 0; start < ids.length; start += NH_ASSISTANT_MAX_PROPOSAL_LOOKUP) {
    batches.push(ids.slice(start, start + NH_ASSISTANT_MAX_PROPOSAL_LOOKUP));
  }
  return batches;
}

/** The time between a run's start and end, in seconds below two minutes and in minutes above. */
function durationOf(startedAt: string | null, endedAt: string | null): RunDuration | null {
  if (!startedAt || !endedAt) {
    return null;
  }

  const seconds = Math.max(0, Math.round((Date.parse(endedAt) - Date.parse(startedAt)) / 1000));
  if (Number.isNaN(seconds)) {
    return null;
  }

  return seconds < 120
    ? { key: 'nh-assistant.agent-runs.result.duration-seconds', value: seconds }
    : { key: 'nh-assistant.agent-runs.result.duration-minutes', value: Math.round(seconds / 60) };
}
