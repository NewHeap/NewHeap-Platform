import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NotificationAlertChannel,
  NotificationAlertChannels,
  NotificationAlertKind,
  NotificationAlertKindChange,
  NotificationQuietHours,
  NotificationSubjectMute
} from '../../models/assistant-alert.models';
import { NotificationSettings, UpdateNotificationSettingsRequest } from '../../models/assistant-api.models';
import { nhAssistantErrorKeys, toNhAssistantError } from '../../internal/nh-assistant-errors';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantPushSwitchComponent } from '../../internal/nh-assistant-push-switch.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantPushService } from '../../services/nh-assistant-push.service';
import { NhAssistantError } from '../../services/nh-assistant.store';

/** A time of day as `PUT notifications` takes it: `HH:mm`. */
const timeOfDay = /^([01]\d|2[0-3]):[0-5]\d$/;

/** The most digest times of a day. */
const maxDigestTimes = 4;

/** Quiet hours while the person edits them: either time may still be empty or no time. */
interface QuietHoursDraft {
  start: string;
  end: string;
}

/** A person's choices of one kind while they edit them. */
interface KindDraft {
  muted: boolean;
  /** The person's digest times, `null` for the kind's default; a time may be what they typed that is no time. */
  digestTimes: string[] | null;
  /** The person's quiet hours, `null` for the kind's default. */
  quietHours: QuietHoursDraft | null;
  channels: NotificationAlertChannels;
}

let nextId = 0;

/**
 * One person's notification choices (FB87): the switch of desktop notifications that the assistant's
 * preferences show as well, owned by `NhAssistantPushService`, and per kind of alert what the kind lets
 * them change: receiving it at all (never for a required kind), the times of its digests (up to four a
 * day), their own quiet hours and its channels. It shows the person's time zone, in which times apply,
 * and the objects they asked not to be told about for a while, which they lift here. It saves the
 * changed kinds and the mutes to lift together with `PUT notifications`. It reads and saves through
 * `NhAssistantPushService`, which shares one `GET notifications` with the rest of the page. A server
 * without alerts lists no kinds; the push switch still works.
 */
@Component({
  selector: 'nh-assistant-notification-preferences',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPushSwitchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-notification-preferences.component.html',
  styleUrl: './nh-assistant-notification-preferences.component.scss'
})
export class NhAssistantNotificationPreferencesComponent {
  private readonly push = inject(NhAssistantPushService);
  private subscription?: Subscription;

  /** Shows a close button that emits `closed`, for a panel or dialog. */
  readonly closable = input(false);
  /** Emitted when the person leaves the choices. */
  readonly closed = output<void>();
  /** Emitted with every choice as stored after a successful save. */
  readonly saved = output<NotificationSettings>();

  readonly id = `nh-assistant-notification-preferences-${nextId++}`;
  readonly maxDigestTimes = maxDigestTimes;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly saving = signal(false);
  readonly savedMessage = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  private readonly stored = signal<NotificationSettings | null>(null);
  private readonly drafts = signal<Readonly<Record<string, KindDraft>>>({});
  /** The mutes the person chose to lift with the next save. */
  private readonly lifting = signal<ReadonlySet<string>>(new Set());

  /** The kinds of alert the person receives; an older server sends none. */
  readonly kinds = computed<readonly NotificationAlertKind[]>(() => this.stored()?.alertKinds ?? []);
  /** The person's mutes of a kind about one object that have not ended; an older server sends none. */
  readonly mutes = computed<readonly NotificationSubjectMute[]>(() => this.stored()?.subjectMutes ?? []);
  /** The person's time zone, in which digest times apply; `null` when the server names none. */
  readonly timeZone = computed(() => this.stored()?.timeZone ?? null);
  /** What saving would send: the kinds the person changed, each with what changed only. */
  readonly changes = computed<NotificationAlertKindChange[]>(() => {
    const drafts = this.drafts();
    return this.kinds()
      .map(kind => changeOf(kind, drafts[kind.id]))
      .filter((change): change is NotificationAlertKindChange => change !== null);
  });
  /** The ids of the mutes saving would lift. */
  readonly lifts = computed(() => this.mutes().filter(mute => this.lifting().has(mute.id)).map(mute => mute.id));
  readonly invalid = computed(() => Object.values(this.drafts()).some(draft => !isValidDraft(draft)));
  readonly canSave = computed(() => !this.loading()
    && !this.saving()
    && !this.invalid()
    && (this.changes().length > 0 || this.lifts().length > 0));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());

    // Settings that another part of the page loaded or saved, such as a mute set by dismissing an alert,
    // show here too; what the person changed and has not saved stays.
    effect(() => {
      const shared = this.push.settings();
      untracked(() => {
        if (shared && !this.loading() && !this.saving() && shared !== this.stored()) {
          this.merge(shared);
        }
      });
    });
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.push.loadSettings().subscribe({
      next: settings => {
        this.apply(settings);
        this.loading.set(false);
        // A page that shows the choices before the assistant started still shows the push switch; the
        // activation reuses these settings.
        if (settings.pushAvailable) {
          void this.push.activate(true);
        }
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  draftOf(kind: NotificationAlertKind): KindDraft {
    return this.drafts()[kind.id] ?? draftOf(kind);
  }

  /** Whether the person receives a kind they may mute; the switch shows receiving, not muting. */
  setReceived(kind: NotificationAlertKind, event: Event): void {
    const received = (event.target as HTMLInputElement).checked;
    this.update(kind, draft => ({ ...draft, muted: !received }));
  }

  /** The digest times a kind shows for the person: their own while they edit, else the kind's. */
  digestTimesOf(kind: NotificationAlertKind): string[] {
    return this.draftOf(kind).digestTimes ?? defaultTimesOf(kind);
  }

  /** Whether the server takes several digest times for a kind; an older server takes one. */
  takesSeveralTimes(kind: NotificationAlertKind): boolean {
    return kind.defaultDigestTimes !== undefined;
  }

  /**
   * One of the person's digest times; an empty field drops that time, and the kind's own times return to
   * the kind's default.
   */
  setDigestTime(kind: NotificationAlertKind, event: Event, index = 0): void {
    const value = (event.target as HTMLInputElement).value.trim().slice(0, 5);
    const times = [...this.digestTimesOf(kind)];
    if (value === '') {
      times.splice(index, 1);
    } else {
      times[index] = value;
    }
    this.update(kind, draft => ({ ...draft, digestTimes: ownTimes(kind, times) }));
  }

  /** Adds a digest time an hour after the last one, up to four. */
  addDigestTime(kind: NotificationAlertKind): void {
    const times = this.digestTimesOf(kind);
    if (times.length >= maxDigestTimes) {
      return;
    }

    this.update(kind, draft => ({ ...draft, digestTimes: ownTimes(kind, [...times, nextTime(times)]) }));
  }

  removeDigestTime(kind: NotificationAlertKind, index: number): void {
    const times = this.digestTimesOf(kind).filter((_, position) => position !== index);
    this.update(kind, draft => ({ ...draft, digestTimes: ownTimes(kind, times) }));
  }

  resetDigestTime(kind: NotificationAlertKind): void {
    this.update(kind, draft => ({ ...draft, digestTimes: null }));
  }

  /** The quiet hours a kind shows for the person: their own while they edit, else the kind's. */
  quietHoursOf(kind: NotificationAlertKind): QuietHoursDraft | null {
    return this.draftOf(kind).quietHours ?? kind.defaultQuietHours ?? null;
  }

  /** The start or end of the person's own quiet hours; the kind's own quiet hours return to the default. */
  setQuietHours(kind: NotificationAlertKind, part: 'start' | 'end', event: Event): void {
    const value = (event.target as HTMLInputElement).value.trim().slice(0, 5);
    const current = this.quietHoursOf(kind) ?? { start: '', end: '' };
    const next = { ...current, [part]: value };
    const own = sameHours(next, kind.defaultQuietHours ?? null) || (next.start === '' && next.end === '') ? null : next;
    this.update(kind, draft => ({ ...draft, quietHours: own }));
  }

  resetQuietHours(kind: NotificationAlertKind): void {
    this.update(kind, draft => ({ ...draft, quietHours: null }));
  }

  /** Whether the quiet hours the person typed for a kind are no quiet hours. */
  isQuietHoursInvalid(kind: NotificationAlertKind): boolean {
    const quietHours = this.draftOf(kind).quietHours;
    return quietHours !== null && !isQuietHours(quietHours);
  }

  setChannel(kind: NotificationAlertKind, channel: NotificationAlertChannel, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    this.update(kind, draft => ({ ...draft, channels: { ...draft.channels, [channelKey(channel)]: on } }));
  }

  channelOn(draft: KindDraft, channel: NotificationAlertChannel): boolean {
    return draft.channels[channelKey(channel)];
  }

  /** The channels that deliver a kind the person may not choose, for its hint. */
  channelsOf(kind: NotificationAlertKind): NotificationAlertChannel[] {
    return (['push', 'in-app'] as const).filter(channel => kind.channels[channelKey(channel)]);
  }

  /** Whether a time the person typed for a kind is no time of day, or a time they gave twice. */
  isInvalid(kind: NotificationAlertKind, index = 0): boolean {
    const times = this.draftOf(kind).digestTimes;
    const time = times?.[index];
    if (times === null || time === undefined) {
      return false;
    }

    return !timeOfDay.test(time) || times.indexOf(time) !== index;
  }

  /** Whether any time the person typed for a kind is no time of day or given twice. */
  hasInvalidTime(kind: NotificationAlertKind): boolean {
    const times = this.draftOf(kind).digestTimes;
    return times !== null && times.some((_, index) => this.isInvalid(kind, index));
  }

  /** The kind's own digest times for a hint, such as `07:30, 16:00`. */
  defaultTimesText(kind: NotificationAlertKind): string {
    return defaultTimesOf(kind).join(', ');
  }

  /** The translation key of a kind's delivery as it applies now: immediately, or in its digests. */
  deliveryKey(kind: NotificationAlertKind): string {
    const times = this.effectiveDigestTimes(kind);
    if (times.length === 0) {
      return 'nh-assistant.notification-preferences.delivery-immediately';
    }

    const digests = times.length > 1 ? 'delivery-digests' : 'delivery-digest';
    return `nh-assistant.notification-preferences.${digests}${kind.workdaysOnly ? '-workdays' : ''}`;
  }

  /** The digest times of a kind as they would apply: the valid times the person chose, else the kind's. */
  effectiveDigestTimes(kind: NotificationAlertKind): string[] {
    const times = this.draftOf(kind).digestTimes;
    return times !== null && times.length > 0 && times.every(time => timeOfDay.test(time))
      ? sortedTimes(times)
      : defaultTimesOf(kind);
  }

  /** The first digest time as it would apply, for a single time. */
  effectiveDigestTime(kind: NotificationAlertKind): string | null {
    return this.effectiveDigestTimes(kind)[0] ?? null;
  }

  /** Whether saving lifts a mute. */
  isLifting(mute: NotificationSubjectMute): boolean {
    return this.lifting().has(mute.id);
  }

  /** Lifts a mute with the next save, or keeps it again. */
  toggleLift(mute: NotificationSubjectMute): void {
    this.lifting.update(lifting => {
      const next = new Set(lifting);
      if (!next.delete(mute.id)) {
        next.add(mute.id);
      }
      return next;
    });
    this.savedMessage.set(false);
  }

  onPushFailed(error: unknown): void {
    this.error.set(toNhAssistantError(error));
  }

  save(): void {
    if (!this.canSave()) {
      return;
    }

    const request: UpdateNotificationSettingsRequest = {};
    if (this.changes().length > 0) {
      request.alertKinds = this.changes();
    }
    if (this.lifts().length > 0) {
      request.liftSubjectMutes = this.lifts();
    }

    this.saving.set(true);
    this.error.set(null);
    this.savedMessage.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.push.updatePreferences(request).subscribe({
      next: settings => {
        this.apply(settings);
        this.saving.set(false);
        this.savedMessage.set(true);
        this.saved.emit(settings);
      },
      error: (failure: unknown) => {
        this.saving.set(false);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }

  private update(kind: NotificationAlertKind, change: (draft: KindDraft) => KindDraft): void {
    this.drafts.update(drafts => ({ ...drafts, [kind.id]: change(drafts[kind.id] ?? draftOf(kind)) }));
    this.savedMessage.set(false);
  }

  private apply(settings: NotificationSettings): void {
    this.stored.set(settings);
    const drafts: Record<string, KindDraft> = {};
    for (const kind of settings.alertKinds ?? []) {
      drafts[kind.id] = draftOf(kind);
    }
    this.drafts.set(drafts);
    this.lifting.set(new Set());
  }

  /** Shows settings read elsewhere and keeps the person's unsaved changes of the kinds and mutes. */
  private merge(settings: NotificationSettings): void {
    const before = new Map((this.stored()?.alertKinds ?? []).map(kind => [kind.id, kind]));
    const drafts = this.drafts();
    const merged: Record<string, KindDraft> = {};
    for (const kind of settings.alertKinds ?? []) {
      const previous = before.get(kind.id);
      const draft = drafts[kind.id];
      merged[kind.id] = previous && draft && changeOf(previous, draft) !== null ? draft : draftOf(kind);
    }

    const listed = new Set((settings.subjectMutes ?? []).map(mute => mute.id));
    this.stored.set(settings);
    this.drafts.set(merged);
    this.lifting.update(lifting => new Set([...lifting].filter(id => listed.has(id))));
  }
}

function draftOf(kind: NotificationAlertKind): KindDraft {
  return {
    muted: kind.muted,
    digestTimes: storedTimesOf(kind),
    quietHours: kind.quietHours ? { ...kind.quietHours } : null,
    channels: { ...kind.channels }
  };
}

/** The kind's own digest times; an older server names one. */
function defaultTimesOf(kind: NotificationAlertKind): string[] {
  return kind.defaultDigestTimes ?? (kind.defaultDigestTime ? [kind.defaultDigestTime] : []);
}

/** The person's stored digest times; `null` for the kind's. */
function storedTimesOf(kind: NotificationAlertKind): string[] | null {
  if (kind.digestTimes !== undefined) {
    return kind.digestTimes ? [...kind.digestTimes] : null;
  }

  return kind.digestTime ? [kind.digestTime] : null;
}

/** The person's own digest times, or `null` for none or for the kind's own. */
function ownTimes(kind: NotificationAlertKind, times: string[]): string[] | null {
  if (times.length === 0) {
    return null;
  }

  return sameTimes(times, defaultTimesOf(kind)) ? null : times;
}

function sortedTimes(times: readonly string[]): string[] {
  return [...new Set(times)].sort();
}

function sameTimes(left: readonly string[] | null, right: readonly string[] | null): boolean {
  if (left === null || right === null) {
    return left === right;
  }

  const sortedLeft = sortedTimes(left);
  const sortedRight = sortedTimes(right);
  return sortedLeft.length === sortedRight.length && sortedLeft.every((time, index) => time === sortedRight[index]);
}

function sameHours(left: QuietHoursDraft | null, right: NotificationQuietHours | null): boolean {
  if (left === null || right === null) {
    return left === right;
  }

  return left.start === right.start && left.end === right.end;
}

/** The next whole hour after the last time that the list does not have yet. */
function nextTime(times: readonly string[]): string {
  const last = times.filter(time => timeOfDay.test(time)).sort().pop() ?? '07:00';
  let hour = Number(last.slice(0, 2));
  for (let step = 0; step < 24; step++) {
    hour = (hour + 1) % 24;
    const candidate = `${String(hour).padStart(2, '0')}:00`;
    if (!times.includes(candidate)) {
      return candidate;
    }
  }

  return '12:00';
}

function channelKey(channel: NotificationAlertChannel): keyof NotificationAlertChannels {
  return channel === 'push' ? 'push' : 'inApp';
}

function isQuietHours(quietHours: QuietHoursDraft): boolean {
  return timeOfDay.test(quietHours.start) && timeOfDay.test(quietHours.end) && quietHours.start !== quietHours.end;
}

function isValidDraft(draft: KindDraft): boolean {
  const times = draft.digestTimes;
  const timesValid = times === null
    || (times.length <= maxDigestTimes && times.every((time, index) => timeOfDay.test(time) && times.indexOf(time) === index));
  return timesValid && (draft.quietHours === null || isQuietHours(draft.quietHours));
}

/**
 * What changed of one kind, only where the kind lets the person change it; `null` when nothing did. A
 * server that takes several digest times gets `digestTimes`, an older one the single `digestTime`.
 */
function changeOf(kind: NotificationAlertKind, draft: KindDraft | undefined): NotificationAlertKindChange | null {
  if (!draft) {
    return null;
  }

  const change: NotificationAlertKindChange = { id: kind.id };
  let changed = false;
  if (kind.mayMute && !kind.required && draft.muted !== kind.muted) {
    change.muted = draft.muted;
    changed = true;
  }
  if (kind.mayChangeDigestTime && !sameTimes(draft.digestTimes, storedTimesOf(kind))) {
    if (kind.defaultDigestTimes !== undefined) {
      change.digestTimes = draft.digestTimes === null ? null : sortedTimes(draft.digestTimes);
    } else {
      change.digestTime = draft.digestTimes?.[0] ?? null;
    }
    changed = true;
  }
  if (kind.mayChangeQuietHours && !sameHours(draft.quietHours, kind.quietHours ?? null)) {
    change.quietHours = draft.quietHours === null ? null : { start: draft.quietHours.start, end: draft.quietHours.end };
    changed = true;
  }
  if (kind.mayChooseChannels) {
    const channels: Partial<NotificationAlertChannels> = {};
    if (draft.channels.push !== kind.channels.push && kind.availableChannels.includes('push')) {
      channels.push = draft.channels.push;
    }
    if (draft.channels.inApp !== kind.channels.inApp && kind.availableChannels.includes('in-app')) {
      channels.inApp = draft.channels.inApp;
    }
    if (Object.keys(channels).length > 0) {
      change.channels = channels;
      changed = true;
    }
  }

  return changed ? change : null;
}
