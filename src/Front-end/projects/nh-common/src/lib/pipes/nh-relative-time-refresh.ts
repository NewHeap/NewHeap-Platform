import {inject, Injectable, NgZone} from '@angular/core';

interface NhRelativeTimeRefreshEntry {
  readonly dueAt: number;
  readonly refresh: () => void;
}

/**
 * Internal clock and timer of the `nhRelativeTime` pipe. Every pipe without an explicit `now`
 * asks to be refreshed when its text would next change; one timer, running outside Angular,
 * serves all of them and marks the due views for check together in one change detection.
 *
 * `now()` returns one time per synchronous turn, so the check and the development-mode
 * no-changes check of one change detection see the same text.
 */
@Injectable({providedIn: 'root'})
export class NhRelativeTimeRefresh {
  private readonly zone = inject(NgZone);
  private readonly entries = new Map<object, NhRelativeTimeRefreshEntry>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private timerDueAt = Number.POSITIVE_INFINITY;
  private turnNow: number | null = null;

  now(): number {
    if (this.turnNow === null) {
      this.turnNow = Date.now();
      // Outside Angular, so forgetting the time does not start another change detection.
      this.zone.runOutsideAngular(() => queueMicrotask(() => {
        this.turnNow = null;
      }));
    }

    return this.turnNow;
  }

  schedule(owner: object, dueAt: number, refresh: () => void): void {
    this.entries.set(owner, {dueAt, refresh});

    if (this.timer === null || dueAt < this.timerDueAt) {
      this.arm(dueAt);
    }
  }

  cancel(owner: object): void {
    if (!this.entries.delete(owner)) {
      return;
    }

    if (this.entries.size === 0) {
      this.disarm();
    }
  }

  private arm(dueAt: number): void {
    this.disarm();

    this.timerDueAt = dueAt;
    const delay = Math.max(0, dueAt - Date.now());
    this.zone.runOutsideAngular(() => {
      this.timer = setTimeout(() => this.fire(), delay);
    });
  }

  private disarm(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
    }

    this.timer = null;
    this.timerDueAt = Number.POSITIVE_INFINITY;
  }

  private fire(): void {
    this.timer = null;
    this.timerDueAt = Number.POSITIVE_INFINITY;

    const now = Date.now();
    const due: Array<() => void> = [];
    let next = Number.POSITIVE_INFINITY;
    for (const [owner, entry] of this.entries) {
      if (entry.dueAt <= now) {
        due.push(entry.refresh);
        this.entries.delete(owner);
      } else {
        next = Math.min(next, entry.dueAt);
      }
    }

    if (next !== Number.POSITIVE_INFINITY) {
      this.arm(next);
    }

    // A refreshed pipe schedules itself again when change detection runs it.
    if (due.length > 0) {
      this.zone.run(() => {
        for (const refresh of due) {
          refresh();
        }
      });
    }
  }
}
