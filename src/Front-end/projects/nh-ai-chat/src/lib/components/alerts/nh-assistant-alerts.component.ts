import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  Alert,
  AlertGroup,
  AlertList,
  AlertListStatus,
  AlertSubjectTarget,
  AlertUrgency
} from '../../models/assistant-alert.models';
import { nhAssistantErrorKeys, toNhAssistantError } from '../../internal/nh-assistant-errors';
import { NhAssistantIconComponent, NhAssistantIconName } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantAlertBadgeService } from '../../services/nh-assistant-alert-badge.service';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantError } from '../../services/nh-assistant.store';

/** How `nh-assistant-alerts` shows its alerts: a card each, or one row each for a long list. */
export type NhAssistantAlertDensity = 'comfortable' | 'compact';

/** The statuses the list's own filter offers; the empty value shows every alert the person did not dismiss. */
const filterStatuses: readonly (AlertListStatus | '')[] = ['', 'unread', 'open', 'closed', 'dismissed'];

const urgencyIcons: Readonly<Record<AlertUrgency, NhAssistantIconName>> = {
  info: 'bell',
  warning: 'warning',
  urgent: 'warning'
};

/** One block of the shown page: a group of the grouped list with its alerts, or the whole flat page. */
interface AlertBlock {
  key: string;
  group: AlertGroup | null;
  items: Alert[];
}

let nextId = 0;

/**
 * A person's own alerts (FB87): warnings about objects that an agent's rules or the application raised
 * for them, with nothing to approve. The most recently raised come first, or with `grouped` (on by
 * default) per parent, such as a project with the alerts about its tasks. Each alert shows its urgency,
 * its title and text in the person's language, its kind and its subject; a resolved or expired alert
 * shows as such. The person marks an alert read or dismisses it, also a closed one, and each action
 * updates the badge of `NhAssistantAlertBadgeService`.
 *
 * Opening an alert's subject marks the alert read and emits `openSubject` with its type, id and link;
 * the component never navigates, so the host routes to the object as it routes the subject of a
 * proposal. `status`, `kind`, `subjectType` with `subjectId` and `urgency` filter the list; with
 * `showStatusFilter` (on by default) the person chooses the status too. `density: 'compact'` shows one
 * row per alert without its text, as the inbox's compact list does.
 */
@Component({
  selector: 'nh-assistant-alerts',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantIconComponent, NhAssistantTranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-alerts.component.html',
  styleUrl: './nh-assistant-alerts.component.scss'
})
export class NhAssistantAlertsComponent {
  private readonly api = inject(NhAssistantApiService);
  private readonly badge = inject(NhAssistantAlertBadgeService);
  private subscription?: Subscription;
  private actionSubscription?: Subscription;

  /** Shows the alerts of one status; without it every alert the person did not dismiss. */
  readonly status = input<AlertListStatus | null>(null);
  /** Limits the list to one or more kinds. */
  readonly kind = input<string | readonly string[] | null>(null);
  /** Limits the list to one kind of subject, or with `subjectId` to one subject. */
  readonly subjectType = input<string | null>(null);
  readonly subjectId = input<string | null>(null);
  /** Limits the list to one or more urgencies. */
  readonly urgency = input<AlertUrgency | readonly AlertUrgency[] | null>(null);
  /** Groups the alerts per parent and subject; on by default. */
  readonly grouped = input(true);
  /** Lets the person choose the status the list shows. */
  readonly showStatusFilter = input(true);
  readonly itemsPerPage = input(20);
  /** `compact` shows one row per alert without its text; `comfortable`, the default, a card each. */
  readonly density = input<NhAssistantAlertDensity>('comfortable');
  /** Emits the subject of an alert the person opens, for the host to route to; the alert is marked read. */
  readonly openSubject = output<AlertSubjectTarget>();

  readonly id = `nh-assistant-alerts-${nextId++}`;
  readonly filterStatuses = filterStatuses;
  /** The status shown: the host's, until the person chooses another in the list's filter. */
  readonly shownStatus = linkedSignal<AlertListStatus | null>(() => this.status());
  readonly page = signal(1);
  readonly loading = signal(true);
  /** True after the first successful load; later loads keep the list in place. */
  readonly loaded = signal(false);
  readonly loadFailed = signal(false);
  readonly items = signal<Alert[]>([]);
  readonly groups = signal<AlertGroup[] | null>(null);
  readonly total = signal(0);
  /** The alert whose read or dismissal is in flight. */
  readonly busyId = signal<string | null>(null);
  readonly actionError = signal<NhAssistantError | null>(null);
  readonly actionErrorKeys = computed(() => nhAssistantErrorKeys(this.actionError()));
  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.itemsPerPage())));
  readonly compact = computed(() => this.density() === 'compact');
  /** The page in blocks: per group of the grouped list, or one block of the flat list. */
  readonly blocks = computed<AlertBlock[]>(() => {
    const items = this.items();
    const groups = this.groups();
    if (!groups) {
      return [{ key: 'all', group: null, items }];
    }

    const blocks: AlertBlock[] = [];
    let start = 0;
    for (const group of groups) {
      blocks.push({ key: `${group.subjectType}|${group.subjectId}`, group, items: items.slice(start, start + group.count) });
      start += group.count;
    }
    if (start < items.length) {
      // A server that sent fewer group counts than items: the rest shows on its own.
      blocks.push({ key: 'rest', group: null, items: items.slice(start) });
    }
    return blocks;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
    });
    effect(() => {
      this.shownStatus();
      this.kind();
      this.subjectType();
      this.subjectId();
      this.urgency();
      this.grouped();
      this.itemsPerPage();
      untracked(() => {
        this.page.set(1);
        this.load();
      });
    });
  }

  load(): void {
    this.fetch(false);
  }

  goTo(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pages()));
    this.load();
  }

  onStatusChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value as AlertListStatus | '';
    this.shownStatus.set(value === '' ? null : value);
  }

  /** Marks the alert read; the list and the badge follow. */
  markRead(alert: Alert): void {
    this.act(alert, this.api.markAlertRead(alert.id));
  }

  /** Dismisses the alert, also a closed one; it leaves the list unless the list shows dismissed alerts. */
  dismiss(alert: Alert): void {
    this.act(alert, this.api.dismissAlert(alert.id));
  }

  /**
   * Opens the alert's subject: emits it for the host to route to and marks an unread alert read. The
   * component itself never navigates.
   */
  open(alert: Alert): void {
    this.openSubject.emit({ subjectType: alert.subjectType, subjectId: alert.subjectId, link: alert.subjectLink ?? null, alert });
    if (alert.status === 'unread') {
      this.markRead(alert);
    }
  }

  urgencyIcon(urgency: AlertUrgency): NhAssistantIconName {
    return urgencyIcons[urgency] ?? 'bell';
  }

  /** The translation key of the status filter's option. */
  filterKey(status: AlertListStatus | ''): string {
    return `nh-assistant.alerts.filter.${status === '' ? 'active' : status}`;
  }

  /** The text of an empty list, which says what the filter shows. */
  emptyKey(): string {
    return `nh-assistant.alerts.empty.${this.shownStatus() ?? 'active'}`;
  }

  /** A group's subject name as people see it: its name, else its type's name with its id. */
  groupName(group: AlertGroup): string {
    return group.displayName ?? group.subjectId;
  }

  /** Whether an alert's subject is the group's own subject, whose name the group already shows. */
  isGroupSubject(alert: Alert, group: AlertGroup | null): boolean {
    return !!group && group.subjectType === alert.subjectType && group.subjectId === alert.subjectId;
  }

  private act(alert: Alert, request: Observable<Alert>): void {
    if (this.busyId()) {
      return;
    }

    this.actionSubscription?.unsubscribe();
    this.busyId.set(alert.id);
    this.actionError.set(null);
    this.actionSubscription = request.subscribe({
      next: changed => {
        this.busyId.set(null);
        this.items.update(items => items.map(item => (item.id === changed.id ? changed : item)));
        this.badge.refresh();
        // The list reads its page again quietly, so an alert that no longer matches the filter leaves.
        this.fetch(true);
      },
      error: failure => {
        this.busyId.set(null);
        this.actionError.set(toNhAssistantError(failure));
        // An alert the person may no longer read leaves the list.
        this.fetch(true);
      }
    });
  }

  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api
      .getAlerts({
        status: this.shownStatus() ?? undefined,
        kind: this.kind() ?? undefined,
        subjectType: this.subjectType() ?? undefined,
        subjectId: this.subjectType() ? this.subjectId() ?? undefined : undefined,
        urgency: this.urgency() ?? undefined,
        // Only a grouped list asks for groups.
        grouped: this.grouped() || undefined,
        page: this.page(),
        itemsPerPage: this.itemsPerPage()
      })
      .subscribe({
        next: list => this.show(list, quiet),
        error: () => {
          if (quiet) {
            return;
          }

          this.loading.set(false);
          this.loadFailed.set(true);
        }
      });
  }

  private show(list: AlertList, quiet: boolean): void {
    const lastPage = Math.max(1, Math.ceil(list.total / this.itemsPerPage()));
    if (list.items.length === 0 && this.page() > lastPage) {
      // Alerts were dismissed or resolved meanwhile and this page no longer exists.
      this.page.set(lastPage);
      this.fetch(quiet);
      return;
    }

    this.items.set(list.items);
    this.groups.set(this.grouped() ? list.groups ?? null : null);
    this.total.set(list.total);
    this.loading.set(false);
    this.loadFailed.set(false);
    this.loaded.set(true);
  }
}
