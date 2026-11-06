import {isPlatformBrowser} from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  PLATFORM_ID,
  booleanAttribute,
  computed,
  inject,
  input,
  numberAttribute,
  output,
  signal
} from '@angular/core';
import {TranslateService} from '@ngx-translate/core';
import {Subscription} from 'rxjs';
import {NhBackgroundOperation, nhBackgroundOperationIsActive} from '../../models/background-operation.models';
import {NhBackgroundOperationStore} from '../../services/nh-background-operation.store';
import {nhCommonText} from '../../i18n/nh-common-translations';
import {NhCommonTextPipe} from '../../i18n/nh-common-text.pipe';
import {NhCommonIconComponent} from '../nh-common-icon/component';

/**
 * A header button with the number of the user's active background operations. It watches
 * `NhBackgroundOperationStore.watchAll()` while it is shown, so it adds no polling of its own and
 * stops the store's polling with the last watcher, and only in the browser. The count covers the
 * active root operations of the store's snapshot (`nhBackgroundOperationIsActive`). The accessible
 * name carries the count, a changed count is announced politely, and a failed snapshot shows as
 * "status unavailable". A click emits `openRequested`, so the application opens its operations page.
 */
@Component({
  selector: 'nh-background-operations-badge',
  standalone: true,
  imports: [NhCommonTextPipe, NhCommonIconComponent],
  templateUrl: './component.html',
  styleUrl: './component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NhBackgroundOperationsBadgeComponent implements OnInit, OnDestroy {
  private readonly store = inject(NhBackgroundOperationStore);
  private readonly translate = inject(TranslateService, {optional: true});
  private readonly platformId = inject(PLATFORM_ID);
  private subscription: Subscription | undefined;

  /** The name of the button before its state. Defaults to `nh-common.background-operations-badge.label`. */
  readonly label = input<string | undefined>(undefined);
  /** Disables the button, for example while the user may not open the operations page. */
  readonly disabled = input(false, {transform: booleanAttribute});
  /** Renders nothing while no operation is active and the store has no error. */
  readonly hideWhenIdle = input(false, {transform: booleanAttribute});
  /** The highest count shown; a higher count shows as `99+`. */
  readonly maxCount = input(99, {transform: numberAttribute});

  /** The user asked to see the operations. */
  readonly openRequested = output<void>();

  /** The number of active root operations in the store's snapshot. */
  readonly activeCount = signal(0);
  /** Whether the store's last request failed. */
  readonly unavailable = signal(false);
  /** The text of the polite live region; empty until the count changes after the first snapshot. */
  readonly announcement = signal('');

  readonly visible = computed(() => !this.hideWhenIdle() || this.activeCount() > 0 || this.unavailable());
  /** The text of the accessible name and tooltip: the count, none, or the unavailable state. */
  readonly nameKey = computed(() => {
    if (this.unavailable()) {
      return 'nh-common.background-operations-badge.unavailable';
    }

    return this.activeCount() === 0
      ? 'nh-common.background-operations-badge.idle'
      : 'nh-common.background-operations-badge.running';
  });
  readonly displayCount = computed(() => {
    const max = Math.max(1, Math.floor(this.maxCount()));
    return this.activeCount() > max ? `${max}+` : String(this.activeCount());
  });

  ngOnInit(): void {
    if (!isPlatformBrowser(this.platformId)) {
      return;
    }

    // The store replays its snapshot while we subscribe. An empty replay means its first list request
    // is still running; that first result sets the starting count without an announcement.
    let replaying = true;
    let awaitingFirstSnapshot = false;

    const subscription = this.store.watchAll().subscribe(operations => {
      const count = this.countActive(operations);
      const previous = this.activeCount();
      this.activeCount.set(count);

      if (replaying) {
        awaitingFirstSnapshot = operations.length === 0;
        return;
      }

      if (awaitingFirstSnapshot) {
        awaitingFirstSnapshot = false;
        return;
      }

      if (count !== previous) {
        this.announcement.set(this.announcementText(count));
      }
    });
    replaying = false;

    subscription.add(this.store.error$.subscribe(error => this.unavailable.set(error !== undefined)));
    this.subscription = subscription;
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
  }

  open(): void {
    if (this.disabled()) {
      return;
    }

    this.openRequested.emit();
  }

  private countActive(operations: readonly NhBackgroundOperation[]): number {
    return operations.filter(operation => !operation.parentOperationId && nhBackgroundOperationIsActive(operation.status)).length;
  }

  private announcementText(count: number): string {
    if (count === 0) {
      return nhCommonText(this.translate, 'nh-common.background-operations-badge.announce-none');
    }

    if (count === 1) {
      return nhCommonText(this.translate, 'nh-common.background-operations-badge.announce-one');
    }

    return nhCommonText(this.translate, 'nh-common.background-operations-badge.announce-other', {count});
  }
}
