import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NotificationAlertChannel,
  NotificationAlertChannels,
  NotificationAlertKind,
  NotificationAlertKindChange
} from '../../models/assistant-alert.models';
import { NotificationSettings } from '../../models/assistant-api.models';
import { nhAssistantErrorKeys, toNhAssistantError } from '../../internal/nh-assistant-errors';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantPushSwitchComponent } from '../../internal/nh-assistant-push-switch.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantApiService } from '../../services/nh-assistant-api.service';
import { NhAssistantPushService } from '../../services/nh-assistant-push.service';
import { NhAssistantError } from '../../services/nh-assistant.store';

/** A time of day as `PUT notifications` takes it: `HH:mm`. */
const timeOfDay = /^([01]\d|2[0-3]):[0-5]\d$/;

/** A person's choices of one kind while they edit them. */
interface KindDraft {
  muted: boolean;
  /** `HH:mm`, `null` for the kind's default, or what the person typed that is no time. */
  digestTime: string | null;
  channels: NotificationAlertChannels;
}

let nextId = 0;

/**
 * One person's notification choices (FB87): the switch of desktop notifications that the assistant's
 * preferences show as well, owned by `NhAssistantPushService`, and per kind of alert what the kind lets
 * them change: receiving it at all (never for a required kind), the time of its daily digest, and its
 * channels. It shows the person's time zone, in which digest times apply, and saves the changed kinds
 * together with `PUT notifications`. A server without alerts lists no kinds; the push switch still works.
 */
@Component({
  selector: 'nh-assistant-notification-preferences',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPushSwitchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-notification-preferences.component.html',
  styleUrl: './nh-assistant-notification-preferences.component.scss'
})
export class NhAssistantNotificationPreferencesComponent {
  private readonly api = inject(NhAssistantApiService);
  private readonly push = inject(NhAssistantPushService);
  private subscription?: Subscription;

  /** Shows a close button that emits `closed`, for a panel or dialog. */
  readonly closable = input(false);
  /** Emitted when the person leaves the choices. */
  readonly closed = output<void>();
  /** Emitted with every choice as stored after a successful save. */
  readonly saved = output<NotificationSettings>();

  readonly id = `nh-assistant-notification-preferences-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly saving = signal(false);
  readonly savedMessage = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  private readonly stored = signal<NotificationSettings | null>(null);
  private readonly drafts = signal<Readonly<Record<string, KindDraft>>>({});

  /** The kinds of alert the person receives; an older server sends none. */
  readonly kinds = computed<readonly NotificationAlertKind[]>(() => this.stored()?.alertKinds ?? []);
  /** The person's time zone, in which digest times apply; `null` when the server names none. */
  readonly timeZone = computed(() => this.stored()?.timeZone ?? null);
  /** What saving would send: the kinds the person changed, each with what changed only. */
  readonly changes = computed<NotificationAlertKindChange[]>(() => {
    const drafts = this.drafts();
    return this.kinds()
      .map(kind => changeOf(kind, drafts[kind.id]))
      .filter((change): change is NotificationAlertKindChange => change !== null);
  });
  readonly invalid = computed(() => Object.values(this.drafts()).some(draft => !isDigestTime(draft.digestTime)));
  readonly canSave = computed(() => !this.loading() && !this.saving() && !this.invalid() && this.changes().length > 0);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.getNotificationSettings().subscribe({
      next: settings => {
        this.apply(settings);
        this.loading.set(false);
        // A page that shows the choices before the assistant started still shows the push switch.
        if (this.push.settings() === null && settings.pushAvailable) {
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

  /** The person's digest time; an empty field returns to the kind's default. */
  setDigestTime(kind: NotificationAlertKind, event: Event): void {
    const value = (event.target as HTMLInputElement).value.trim();
    this.update(kind, draft => ({ ...draft, digestTime: value === '' ? null : value.slice(0, 5) }));
  }

  resetDigestTime(kind: NotificationAlertKind): void {
    this.update(kind, draft => ({ ...draft, digestTime: null }));
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

  /** Whether the time the person typed for a kind is no time of day. */
  isInvalid(kind: NotificationAlertKind): boolean {
    return !isDigestTime(this.draftOf(kind).digestTime);
  }

  /** The translation key of a kind's delivery as it applies now: immediately, or in its digest. */
  deliveryKey(kind: NotificationAlertKind): string {
    const time = this.effectiveDigestTime(kind);
    if (time === null) {
      return 'nh-assistant.notification-preferences.delivery-immediately';
    }

    return kind.workdaysOnly
      ? 'nh-assistant.notification-preferences.delivery-digest-workdays'
      : 'nh-assistant.notification-preferences.delivery-digest';
  }

  effectiveDigestTime(kind: NotificationAlertKind): string | null {
    const digestTime = this.draftOf(kind).digestTime;
    return isDigestTime(digestTime) && digestTime !== null ? digestTime : kind.defaultDigestTime;
  }

  onPushFailed(error: unknown): void {
    this.error.set(toNhAssistantError(error));
  }

  save(): void {
    if (!this.canSave()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.savedMessage.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.updateNotificationPreferences({ alertKinds: this.changes() }).subscribe({
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
  }
}

function draftOf(kind: NotificationAlertKind): KindDraft {
  return { muted: kind.muted, digestTime: kind.digestTime, channels: { ...kind.channels } };
}

function channelKey(channel: NotificationAlertChannel): keyof NotificationAlertChannels {
  return channel === 'push' ? 'push' : 'inApp';
}

function isDigestTime(value: string | null): boolean {
  return value === null || timeOfDay.test(value);
}

/**
 * What changed of one kind, only where the kind lets the person change it; `null` when nothing did.
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
  if (kind.mayChangeDigestTime && draft.digestTime !== kind.digestTime) {
    change.digestTime = draft.digestTime;
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
