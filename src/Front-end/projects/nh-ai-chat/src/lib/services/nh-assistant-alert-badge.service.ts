import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { AlertSummary, AlertUrgency, AlertUrgencyCounts } from '../models/assistant-alert.models';
import { NhAssistantApiService } from './nh-assistant-api.service';

/** How often a watched badge reads its counts again while the page is visible: every five minutes. */
export const NH_ASSISTANT_ALERT_BADGE_REFRESH_MS = 5 * 60_000;

/** How long after a read the page's focus or visibility reads the counts again at the earliest. */
export const NH_ASSISTANT_ALERT_BADGE_FOCUS_DELAY_MS = 10_000;

const noUrgencies: AlertUrgencyCounts = { info: 0, warning: 0, urgent: 0 };

/**
 * The badge of a person's alerts (FB87): the counts of `GET alerts/summary` as signals for the host's
 * shell, such as a bell with the number of unread alerts and the colour of the highest urgency.
 *
 * Call `watch()` where the badge shows, for example in the shell's constructor, and call the function it
 * returns when it no longer shows. While watched the badge reads its counts at once, when the window gets
 * the focus or the page becomes visible again (at most every ten seconds), every five minutes while the
 * page is visible, and after the person's own actions in `nh-assistant-alerts`. Alerts are raised by
 * rules and by the application's code, which send no live update to the person, so the badge does not
 * wait for one. When the last watcher stops, the counts are forgotten, so a next person never sees them.
 */
@Injectable()
export class NhAssistantAlertBadgeService {
  private readonly api = inject(NhAssistantApiService);
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly summaryState = signal<AlertSummary | null>(null);
  private watchers = 0;
  private subscription?: Subscription;
  /** A refresh was asked while one was in flight; it runs when that one ends. */
  private again = false;
  private lastRead = 0;
  private timer?: ReturnType<typeof setInterval>;
  private readonly onFocus = () => this.refreshIfStale();
  private readonly onVisibility = () => {
    if (this.document.visibilityState === 'visible') {
      this.refreshIfStale();
    }
  };

  /** The counts as last read; `null` before the first read, while nobody watches and when the server has no alerts. */
  readonly summary = this.summaryState.asReadonly();
  /** How many unread open alerts the person has. */
  readonly unread = computed(() => this.summaryState()?.unread ?? 0);
  /** The unread open alerts per urgency. */
  readonly unreadByUrgency = computed(() => this.summaryState()?.unreadByUrgency ?? noUrgencies);
  /** The highest urgency among the unread alerts; `null` without unread alerts. */
  readonly highestUrgency = computed<AlertUrgency | null>(() => {
    const counts = this.unreadByUrgency();
    if (counts.urgent > 0) {
      return 'urgent';
    }
    if (counts.warning > 0) {
      return 'warning';
    }
    return counts.info > 0 ? 'info' : null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  /**
   * Starts keeping the counts current and returns the function that stops it for this caller. Several
   * callers may watch; the badge keeps its counts current until the last one stops.
   */
  watch(): () => void {
    let released = false;
    this.watchers++;
    if (this.watchers === 1) {
      this.start();
    }

    return () => {
      if (released) {
        return;
      }

      released = true;
      this.watchers--;
      if (this.watchers === 0) {
        this.stop();
      }
    };
  }

  /**
   * Reads the counts again now, for example after the person's own action. Does nothing while nobody
   * watches the badge.
   */
  refresh(): void {
    if (this.watchers === 0) {
      return;
    }
    if (this.subscription && !this.subscription.closed) {
      this.again = true;
      return;
    }

    this.again = false;
    this.subscription = this.api.getAlertSummary().subscribe({
      next: summary => {
        this.lastRead = Date.now();
        this.summaryState.set(summary);
        this.next();
      },
      error: () => {
        // An assistant that is off or unreachable shows no badge; the next read tries again.
        this.lastRead = Date.now();
        this.summaryState.set(null);
        this.next();
      }
    });
  }

  private next(): void {
    if (this.again) {
      // The subscription closes after this callback; read again on the next turn.
      queueMicrotask(() => this.refresh());
    }
  }

  private refreshIfStale(): void {
    if (Date.now() - this.lastRead >= NH_ASSISTANT_ALERT_BADGE_FOCUS_DELAY_MS) {
      this.refresh();
    }
  }

  private start(): void {
    this.refresh();
    if (!this.browser) {
      return;
    }

    this.document.defaultView?.addEventListener('focus', this.onFocus);
    this.document.addEventListener('visibilitychange', this.onVisibility);
    this.timer = setInterval(() => {
      // A hidden page reads nothing; it reads again when it becomes visible.
      if (this.document.visibilityState === 'visible') {
        this.refresh();
      }
    }, NH_ASSISTANT_ALERT_BADGE_REFRESH_MS);
  }

  private stop(): void {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.again = false;
    this.lastRead = 0;
    this.summaryState.set(null);
    if (this.timer !== undefined) {
      clearInterval(this.timer);
      this.timer = undefined;
    }
    if (this.browser) {
      this.document.defaultView?.removeEventListener('focus', this.onFocus);
      this.document.removeEventListener('visibilitychange', this.onVisibility);
    }
  }
}
