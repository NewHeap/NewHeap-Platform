import { isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, PLATFORM_ID, effect, inject, untracked } from '@angular/core';
import { BehaviorSubject, Observable, Subscription } from 'rxjs';
import { AgentSubjectSummary, NH_ASSISTANT_MAX_SUBJECT_SUMMARY } from '../models/assistant-agent-run.models';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { NH_ASSISTANT_AGENT_WORK_RELOAD_DELAY_MS } from '../internal/nh-assistant-agent-work';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { NhAssistantLiveService } from './nh-assistant-live.service';
import { NhAssistantStore } from './nh-assistant.store';

/** Summaries kept for subjects that nobody shows any more, so a list that re-renders needs no request. */
const maxUnwatchedSummaries = 500;

interface SummaryEntry {
  readonly subjectType: string;
  readonly subjectId: string;
  readonly value: BehaviorSubject<AgentSubjectSummary | null>;
  watchers: number;
  queued: boolean;
  loaded: boolean;
  failed: boolean;
  /** The request whose answer the entry takes; older answers are ignored. */
  request: number;
}

/**
 * Counts of agent work per subject for badges in lists and on pages. Every summary asked for in the
 * same tick becomes one `POST subjects/{type}/summary` per subject type, in chunks of
 * `NH_ASSISTANT_MAX_SUBJECT_SUMMARY`. Results are cached per subject and shared by everyone who
 * shows it. Live agent-work updates refetch only the subjects they name, batched; a reconnect of
 * the hub refetches every subject shown. Nothing is requested on the server or while the assistant
 * is disabled or denied, and the cache clears when it becomes unavailable, for example on sign-out.
 */
@Injectable()
export class NhAssistantSubjectSummaryService {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly store = inject(NhAssistantStore);
  private readonly live = inject(NhAssistantLiveService, { optional: true });
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly entries = new Map<string, SummaryEntry>();
  /** Subject ids per type that wait for the next request. */
  private readonly queue = new Map<string, Set<string>>();
  /** Subjects named by live updates that wait for the end of the burst. */
  private readonly changed = new Set<string>();
  private requests = new Subscription();
  private readonly liveSubscription = new Subscription();
  private flushScheduled = false;
  private changedTimer?: ReturnType<typeof setTimeout>;
  private requestRevision = 0;
  private destroyed = false;

  constructor() {
    effect(() => {
      const enabled = this.store.enabled();
      untracked(() => this.onAvailabilityChanged(enabled));
    });

    if (this.live) {
      this.liveSubscription.add(this.live.agentWork$.subscribe(change => this.onAgentWork(change)));
      this.liveSubscription.add(this.live.resync$.subscribe(() => this.refetchShown()));
    }

    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.liveSubscription.unsubscribe();
      this.requests.unsubscribe();
      clearTimeout(this.changedTimer);
      for (const entry of this.entries.values()) {
        entry.value.complete();
      }
      this.entries.clear();
      this.queue.clear();
    });
  }

  /**
   * The agent work on one subject: `null` until it is known, then every update. Subscribing shows
   * the subject; summaries of subjects that are shown stay current with live updates. The request
   * starts in the next microtask, together with every other summary asked for until then.
   */
  summary(subjectType: string, subjectId: string): Observable<AgentSubjectSummary | null> {
    return new Observable<AgentSubjectSummary | null>(subscriber => {
      const entry = this.watch(subjectType, subjectId);
      const subscription = entry.value.subscribe(subscriber);

      return () => {
        subscription.unsubscribe();
        this.unwatch(entry);
      };
    });
  }

  /**
   * Fetches the summaries of these subjects again, or of every subject of the type that is shown,
   * for example after the application changed them without an agent.
   */
  refresh(subjectType: string, subjectIds?: readonly string[]): void {
    const ids = subjectIds ? new Set(subjectIds) : null;
    for (const entry of [...this.entries.values()]) {
      if (entry.subjectType !== subjectType || (ids && !ids.has(entry.subjectId))) {
        continue;
      }

      if (entry.watchers > 0) {
        this.enqueue(entry);
      } else {
        this.forget(entry);
      }
    }
  }

  private watch(subjectType: string, subjectId: string): SummaryEntry {
    const key = summaryKey(subjectType, subjectId);
    let entry = this.entries.get(key);
    if (entry) {
      // The most recently shown summaries stay longest.
      this.entries.delete(key);
    } else {
      entry = {
        subjectType,
        subjectId,
        value: new BehaviorSubject<AgentSubjectSummary | null>(null),
        watchers: 0,
        queued: false,
        loaded: false,
        failed: false,
        request: 0
      };
    }

    this.entries.set(key, entry);
    entry.watchers++;
    if (!entry.loaded || entry.failed) {
      this.enqueue(entry);
    }
    if (this.browser) {
      void this.store.initialize();
    }
    return entry;
  }

  private unwatch(entry: SummaryEntry): void {
    entry.watchers = Math.max(0, entry.watchers - 1);
    if (entry.watchers > 0) {
      return;
    }

    let unwatched = 0;
    for (const item of this.entries.values()) {
      if (item.watchers === 0) {
        unwatched++;
      }
    }
    for (const item of [...this.entries.values()]) {
      if (unwatched <= maxUnwatchedSummaries) {
        break;
      }
      if (item.watchers === 0) {
        this.forget(item);
        unwatched--;
      }
    }
  }

  private isCurrent(entry: SummaryEntry): boolean {
    return this.entries.get(summaryKey(entry.subjectType, entry.subjectId)) === entry;
  }

  /** Drops a summary nobody shows; the next subscriber fetches it again. */
  private forget(entry: SummaryEntry): void {
    if (this.isCurrent(entry)) {
      this.entries.delete(summaryKey(entry.subjectType, entry.subjectId));
    }
    this.queue.get(entry.subjectType)?.delete(entry.subjectId);
    entry.value.complete();
  }

  private enqueue(entry: SummaryEntry): void {
    if (entry.queued) {
      return;
    }

    entry.queued = true;
    let ids = this.queue.get(entry.subjectType);
    if (!ids) {
      ids = new Set<string>();
      this.queue.set(entry.subjectType, ids);
    }
    ids.add(entry.subjectId);
    this.scheduleFlush();
  }

  private scheduleFlush(): void {
    if (this.flushScheduled) {
      return;
    }

    this.flushScheduled = true;
    queueMicrotask(() => {
      this.flushScheduled = false;
      this.flush();
    });
  }

  /** Sends the queued subjects: one request per type and chunk. Waits while the assistant is unavailable. */
  private flush(): void {
    if (this.destroyed || !this.browser || !this.store.enabled()) {
      return;
    }

    for (const [subjectType, ids] of [...this.queue]) {
      this.queue.delete(subjectType);
      const subjectIds = [...ids];
      for (let start = 0; start < subjectIds.length; start += NH_ASSISTANT_MAX_SUBJECT_SUMMARY) {
        this.send(subjectType, subjectIds.slice(start, start + NH_ASSISTANT_MAX_SUBJECT_SUMMARY));
      }
    }
  }

  private send(subjectType: string, subjectIds: string[]): void {
    const revision = ++this.requestRevision;
    const entries: SummaryEntry[] = [];
    for (const subjectId of subjectIds) {
      const entry = this.entries.get(summaryKey(subjectType, subjectId));
      if (entry) {
        entry.queued = false;
        entry.request = revision;
        entries.push(entry);
      }
    }
    if (entries.length === 0) {
      return;
    }

    const subscription = this.api.summarizeSubjects(subjectType, entries.map(entry => entry.subjectId)).subscribe({
      next: list => {
        const items = new Map(list.items.map(item => [item.subjectId, item]));
        for (const entry of entries) {
          if (entry.request !== revision || !this.isCurrent(entry)) {
            continue;
          }

          entry.loaded = true;
          entry.failed = false;
          entry.value.next(items.get(entry.subjectId) ?? emptySummary(entry.subjectId));
        }
      },
      error: () => {
        // A badge without a count is better than an error in a list; the next live update,
        // reconnect or subscriber tries again.
        for (const entry of entries) {
          if (entry.request === revision) {
            entry.failed = true;
          }
        }
      }
    });
    this.requests.add(subscription);
  }

  private onAgentWork(change: LiveAgentWorkChanged): void {
    if (!change.subjectType || !change.subjectId) {
      return;
    }

    const key = summaryKey(change.subjectType, change.subjectId);
    const entry = this.entries.get(key);
    if (!entry) {
      return;
    }
    if (entry.watchers === 0) {
      this.forget(entry);
      return;
    }

    this.changed.add(key);
    if (this.changedTimer === undefined) {
      this.changedTimer = setTimeout(() => this.refetchChanged(), NH_ASSISTANT_AGENT_WORK_RELOAD_DELAY_MS);
    }
  }

  /** Refetches the shown subjects of a burst of live updates, together. */
  private refetchChanged(): void {
    this.changedTimer = undefined;
    const keys = [...this.changed];
    this.changed.clear();
    for (const key of keys) {
      const entry = this.entries.get(key);
      if (entry && entry.watchers > 0) {
        this.enqueue(entry);
      }
    }
  }

  /** After a reconnect updates may have been missed: refetch what is shown, forget the rest. */
  private refetchShown(): void {
    for (const entry of [...this.entries.values()]) {
      if (entry.watchers > 0) {
        this.enqueue(entry);
      } else {
        this.forget(entry);
      }
    }
  }

  private onAvailabilityChanged(enabled: boolean): void {
    if (enabled) {
      for (const entry of this.entries.values()) {
        if (entry.watchers > 0 && (!entry.loaded || entry.failed)) {
          this.enqueue(entry);
        }
      }
      this.scheduleFlush();
      return;
    }

    // Signed out, denied or disabled: no counts of a previous account may remain.
    this.requests.unsubscribe();
    this.requests = new Subscription();
    clearTimeout(this.changedTimer);
    this.changedTimer = undefined;
    this.changed.clear();
    this.queue.clear();
    for (const entry of [...this.entries.values()]) {
      if (entry.watchers === 0) {
        this.forget(entry);
        continue;
      }

      entry.queued = false;
      entry.loaded = false;
      entry.failed = false;
      entry.request = 0;
      if (entry.value.value !== null) {
        entry.value.next(null);
      }
    }
  }
}

function summaryKey(subjectType: string, subjectId: string): string {
  return `${subjectType}\u0000${subjectId}`;
}

function emptySummary(subjectId: string): AgentSubjectSummary {
  return { subjectId, openProposals: 0, proposalsToDecide: 0, runActive: false };
}
