import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AdminAlertDelivery,
  AdminAlertDeliveryMode,
  AdminAlertKind,
  AdminAlertKindChange,
  AdminAlertKinds,
  AlertUrgency,
  NH_ASSISTANT_ALERT_URGENCIES,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { startNhAssistantAdminTranslations } from '../i18n/nh-assistant-admin-translations';

/** Periods the view offers, in days before now. */
export type NhAssistantAlertKindsPeriod = 7 | 30 | 90;

/** The most digest times of one day, as the server allows. */
const maxDigestTimes = 4;

/** The longest reason of a change, as the server allows. */
const maxReason = 500;

const dayMs = 24 * 60 * 60 * 1000;
const timeOfDay = /^([01]\d|2[0-3]):[0-5]\d$/;

/** What the administrator is changing about one kind's default delivery. */
interface NhAssistantAlertDeliveryDraft {
  mode: AdminAlertDeliveryMode;
  digestTimes: string[];
  workdaysOnly: boolean;
  immediatelyFrom: AlertUrgency | null;
  quietHours: boolean;
  quietStart: string;
  quietEnd: string;
  reason: string;
}

/** A text with its parameters, for the translate pipe; `urgency` is a parameter that is translated first. */
interface NhAssistantAlertKindText {
  key: string;
  params?: Record<string, string | number>;
  urgency?: AlertUrgency;
}

let nextId = 0;

/**
 * The administration of alert kinds (FB87 phase 2), for administrators: every kind the application
 * declares in code, with its urgency, whether it is required and on, its default delivery and the
 * numbers of its alerts raised in the period (raised, delivered, read, dismissed, resolved before
 * reading, withheld and of those muted), and per agent in shadow the alerts it would have sent. An
 * administrator switches a kind off and changes its default delivery (immediately or in digests at up
 * to four times, working days only, immediately from an urgency and quiet hours) only where the kind's
 * code allows it; every control the code keeps is disabled and says so, and the server refuses the rest
 * with its own code. A person's own choices apply on top. The server requires the assistant admin policy.
 */
@Component({
  selector: 'nh-assistant-admin-alert-kinds',
  standalone: true,
  imports: [NgTemplateOutlet, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-alert-kinds.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-admin-alert-kinds.component.scss']
})
export class NhAssistantAdminAlertKindsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;
  private change?: Subscription;

  readonly id = `nh-assistant-alert-kinds-${nextId++}`;
  readonly periods: readonly NhAssistantAlertKindsPeriod[] = [7, 30, 90];
  readonly urgencies = NH_ASSISTANT_ALERT_URGENCIES;
  readonly maxDigestTimes = maxDigestTimes;
  readonly maxReason = maxReason;
  readonly period = signal<NhAssistantAlertKindsPeriod>(30);

  readonly loading = signal(true);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly view = signal<AdminAlertKinds | null>(null);
  readonly kinds = computed(() => this.view()?.kinds ?? []);

  /** The kind whose delivery is being changed, and the change. */
  readonly editing = signal<string | null>(null);
  readonly draft = signal<NhAssistantAlertDeliveryDraft | null>(null);
  /** The kind whose change is being saved. */
  readonly saving = signal<string | null>(null);
  /** The last change that failed, with the kind it was about. */
  readonly failure = signal<{ kindId: string; error: NhAssistantError } | null>(null);
  readonly failureKeys = computed(() => nhAssistantErrorKeys(this.failure()?.error ?? null));
  /** The kind whose change was saved last. */
  readonly saved = signal<string | null>(null);

  /** What is wrong with the draft before it is sent; `null` when it may be saved. */
  readonly draftProblem = computed<string | null>(() => {
    const draft = this.draft();
    if (!draft) {
      return null;
    }
    if (draft.mode === 'daily-digest') {
      const times = draft.digestTimes;
      if (times.length < 1 || times.length > maxDigestTimes || times.some(time => !timeOfDay.test(time)) || new Set(times).size !== times.length) {
        return 'nh-assistant.admin.alert-kinds.editor.times-invalid';
      }
    }
    if (draft.quietHours && (!timeOfDay.test(draft.quietStart) || !timeOfDay.test(draft.quietEnd) || draft.quietStart === draft.quietEnd)) {
      return 'nh-assistant.admin.alert-kinds.editor.quiet-hours-invalid';
    }
    return null;
  });

  constructor() {
    startNhAssistantAdminTranslations();

    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.change?.unsubscribe();
    });
    this.load();
  }

  /** Loads the kinds with the numbers of the chosen period. */
  load(): void {
    const to = Date.now();
    this.loading.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin
      .getAlertKinds({
        from: new Date(to - this.period() * dayMs).toISOString(),
        to: new Date(to).toISOString()
      })
      .subscribe({
        next: view => {
          this.view.set(view);
          this.loading.set(false);
        },
        error: (failure: unknown) => {
          this.view.set(null);
          this.loading.set(false);
          this.error.set(toNhAssistantError(failure));
        }
      });
  }

  onPeriod(event: Event): void {
    this.period.set(Number((event.target as HTMLSelectElement).value) as NhAssistantAlertKindsPeriod);
    this.load();
  }

  /** Whether the kind's code lets an administrator change anything about its delivery. */
  mayChangeDelivery(kind: AdminAlertKind): boolean {
    const allowed = kind.administratorMayChange;
    return allowed.digestTimes || allowed.workdaysOnly || allowed.immediatelyFrom || allowed.quietHours;
  }

  /** Why the kind cannot be switched off; `null` when it can. */
  switchOffBlocked(kind: AdminAlertKind): string | null {
    if (kind.administratorMayChange.switchOff) {
      return null;
    }
    return kind.required
      ? 'nh-assistant.admin.alert-kinds.switch-off-required'
      : 'nh-assistant.admin.alert-kinds.switch-off-not-allowed';
  }

  /** The texts that describe a delivery, such as "In a digest at 07:30 on working days". */
  summary(delivery: AdminAlertDelivery): NhAssistantAlertKindText[] {
    const texts: NhAssistantAlertKindText[] = [];
    if (delivery.mode === 'daily-digest' && delivery.digestTimes.length > 0) {
      texts.push({
        key: delivery.workdaysOnly ? 'nh-assistant.admin.alert-kinds.summary.digest-workdays' : 'nh-assistant.admin.alert-kinds.summary.digest',
        params: { times: delivery.digestTimes.join(', ') }
      });
      if (delivery.immediatelyFrom) {
        texts.push({ key: 'nh-assistant.admin.alert-kinds.summary.immediately-from', urgency: delivery.immediatelyFrom });
      }
    } else {
      texts.push({ key: 'nh-assistant.admin.alert-kinds.summary.immediately' });
    }
    if (delivery.quietHours) {
      texts.push({ key: 'nh-assistant.admin.alert-kinds.summary.quiet-hours', params: { start: delivery.quietHours.start, end: delivery.quietHours.end } });
    }
    return texts;
  }

  /** The translation key of an urgency's name. */
  urgencyKey(urgency: AlertUrgency): string {
    return `nh-assistant.alerts.urgency.${urgency}`;
  }

  /** Switches the kind on or off and keeps the administrator's delivery as it is. */
  toggleEnabled(kind: AdminAlertKind): void {
    this.save(kind, {
      enabled: !kind.enabled,
      delivery: kind.settings.delivery,
      expectedVersion: kind.settings.version,
      reason: null
    });
  }

  edit(kind: AdminAlertKind): void {
    const delivery = kind.delivery;
    this.failure.set(null);
    this.saved.set(null);
    this.editing.set(kind.id);
    this.draft.set({
      mode: delivery.mode,
      digestTimes: delivery.mode === 'daily-digest' ? [...delivery.digestTimes] : [...kind.codeDelivery.digestTimes],
      workdaysOnly: delivery.workdaysOnly,
      immediatelyFrom: delivery.immediatelyFrom,
      quietHours: delivery.quietHours !== null,
      quietStart: delivery.quietHours?.start ?? '22:00',
      quietEnd: delivery.quietHours?.end ?? '07:00',
      reason: ''
    });
  }

  cancel(): void {
    this.editing.set(null);
    this.draft.set(null);
    this.failure.set(null);
  }

  update(change: Partial<NhAssistantAlertDeliveryDraft>): void {
    const draft = this.draft();
    if (draft) {
      this.draft.set({ ...draft, ...change });
    }
  }

  onMode(event: Event): void {
    const mode = (event.target as HTMLInputElement).value as AdminAlertDeliveryMode;
    const draft = this.draft();
    this.update({ mode, digestTimes: mode === 'daily-digest' && draft?.digestTimes.length === 0 ? ['07:30'] : draft?.digestTimes ?? [] });
  }

  onTime(index: number, event: Event): void {
    const times = [...(this.draft()?.digestTimes ?? [])];
    times[index] = (event.target as HTMLInputElement).value;
    this.update({ digestTimes: times });
  }

  addTime(): void {
    const times = this.draft()?.digestTimes ?? [];
    if (times.length < maxDigestTimes) {
      this.update({ digestTimes: [...times, nextFreeTime(times)] });
    }
  }

  removeTime(index: number): void {
    this.update({ digestTimes: (this.draft()?.digestTimes ?? []).filter((_, position) => position !== index) });
  }

  onImmediatelyFrom(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.update({ immediatelyFrom: value === '' ? null : value as AlertUrgency });
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /** Saves the draft as the kind's default delivery. */
  saveDelivery(kind: AdminAlertKind): void {
    const draft = this.draft();
    if (!draft || this.draftProblem() !== null) {
      return;
    }

    const digest = draft.mode === 'daily-digest';
    this.save(kind, {
      enabled: kind.settings.enabled,
      delivery: {
        mode: draft.mode,
        digestTimes: digest ? [...draft.digestTimes].sort() : [],
        workdaysOnly: digest && draft.workdaysOnly,
        immediatelyFrom: digest ? draft.immediatelyFrom : null,
        quietHours: draft.quietHours ? { start: draft.quietStart, end: draft.quietEnd } : null
      },
      expectedVersion: kind.settings.version,
      reason: draft.reason.trim() || null
    });
  }

  /** Lets the kind follow the delivery of its code again. */
  reset(kind: AdminAlertKind): void {
    this.save(kind, {
      enabled: kind.settings.enabled,
      delivery: null,
      expectedVersion: kind.settings.version,
      reason: this.draft()?.reason.trim() || null
    });
  }

  private save(kind: AdminAlertKind, change: AdminAlertKindChange): void {
    this.saving.set(kind.id);
    this.failure.set(null);
    this.saved.set(null);
    this.change?.unsubscribe();
    this.change = this.admin.updateAlertKind(kind.id, change).subscribe({
      next: updated => {
        const view = this.view();
        if (view) {
          // The answer has no numbers; the period's numbers stay those of the last load.
          this.view.set({
            ...view,
            kinds: view.kinds.map(item => item.id === updated.id ? { ...updated, numbers: item.numbers, shadow: item.shadow } : item)
          });
        }
        this.saving.set(null);
        this.saved.set(kind.id);
        this.editing.set(null);
        this.draft.set(null);
      },
      error: (failure: unknown) => {
        this.saving.set(null);
        this.failure.set({ kindId: kind.id, error: toNhAssistantError(failure) });
      }
    });
  }
}

/** A time of day that is not among `times` yet, from 12:00 on by the hour. */
function nextFreeTime(times: readonly string[]): string {
  for (let hour = 12; hour < 36; hour++) {
    const time = `${String(hour % 24).padStart(2, '0')}:00`;
    if (!times.includes(time)) {
      return time;
    }
  }
  return '12:00';
}
