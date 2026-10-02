import { DestroyRef, Injectable, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { TranslateService } from '@ngx-translate/core';
import { Observable, Subject, firstValueFrom } from 'rxjs';
import { NotificationSettings } from '../models/assistant-api.models';
import { NH_ASSISTANT_CONFIG } from '../nh-assistant.config';
import { NhAssistantApiService } from './nh-assistant-api.service';

/**
 * Push state of this browser for the signed-in user:
 * - `unavailable`: the server, the browser or the host configuration does not support Web Push;
 * - `off`: the user turned notifications off;
 * - `prompt`: on, but the browser has not asked for permission yet (it asks on the next message sent);
 * - `blocked`: on, but the browser denied permission;
 * - `on`: this browser receives notifications.
 */
export type NhAssistantPushState = 'unavailable' | 'off' | 'prompt' | 'blocked' | 'on';

const openConversationMessage = 'nh-assistant.open-conversation';

/**
 * Keeps this browser's Web Push subscription in line with the user's choice. Notifications are on
 * by default; because browsers only ask for permission after a user action, the assistant asks
 * when the user sends a message or turns notifications on. The bundled service worker shows a
 * notification only when no window of the application is focused.
 */
@Injectable()
export class NhAssistantPushService {
  private readonly api = inject(NhAssistantApiService);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly translate = inject(TranslateService, { optional: true });
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly settingsState = signal<NotificationSettings | null>(null);
  private readonly permissionState = signal<NotificationPermission | 'unsupported'>('default');
  private readonly subscribedState = signal(false);
  private readonly busyState = signal(false);
  private readonly openSubject = new Subject<string>();
  private registration?: Promise<ServiceWorkerRegistration>;
  private revision = 0;
  private readonly onWorkerMessage = (event: MessageEvent) => {
    const data = event.data as { type?: unknown; conversationId?: unknown } | null;
    if (data?.type === openConversationMessage && typeof data.conversationId === 'string') {
      this.openSubject.next(data.conversationId);
    }
  };

  /** The caller's server-side choice, or `null` before it was loaded. */
  readonly settings = this.settingsState.asReadonly();
  readonly busy = this.busyState.asReadonly();
  /** Conversations a clicked notification asks to open. */
  readonly openConversation$: Observable<string> = this.openSubject.asObservable();
  readonly state = computed<NhAssistantPushState>(() => {
    const settings = this.settingsState();
    if (!settings?.pushAvailable || !this.isSupported()) {
      return 'unavailable';
    }
    if (!settings.pushEnabled) {
      return 'off';
    }
    const permission = this.permissionState();
    if (permission === 'denied' || permission === 'unsupported') {
      return 'blocked';
    }
    return permission === 'granted' && this.subscribedState() ? 'on' : 'prompt';
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      if (this.browser && this.isSupported()) {
        navigator.serviceWorker.removeEventListener('message', this.onWorkerMessage);
      }
      this.openSubject.complete();
    });
    if (this.browser && this.isSupported()) {
      navigator.serviceWorker.addEventListener('message', this.onWorkerMessage);
      navigator.serviceWorker.startMessages();
    }
  }

  /**
   * Loads the user's choice and, when permission was granted before, renews this browser's
   * subscription, which also moves it to the current user and to a rotated server key.
   */
  async activate(serverSupportsPush: boolean): Promise<void> {
    const revision = ++this.revision;
    if (!serverSupportsPush || !this.isSupported()) {
      this.settingsState.set(null);
      return;
    }

    try {
      const settings = await firstValueFrom(this.api.getNotificationSettings());
      if (revision !== this.revision) {
        return;
      }
      this.settingsState.set(settings);
      this.permissionState.set(Notification.permission);
      if (settings.pushEnabled && Notification.permission === 'granted') {
        await this.subscribe(settings);
      }
    } catch {
      // Notifications are optional; the assistant works without them.
      if (revision === this.revision) {
        this.settingsState.set(null);
      }
    }
  }

  /**
   * Forgets the user. The browser subscription is cancelled, so a previous user on a shared device
   * stops receiving notifications even when the server cannot be told.
   */
  async deactivate(): Promise<void> {
    this.revision++;
    this.settingsState.set(null);
    this.subscribedState.set(false);
    if (!this.isSupported()) {
      return;
    }
    try {
      const registration = await navigator.serviceWorker.getRegistration(this.scope());
      const subscription = await registration?.pushManager.getSubscription();
      await subscription?.unsubscribe();
    } catch {
      // Nothing to cancel.
    }
  }

  /** Asks for permission once, after a user action such as sending a message. */
  async onUserGesture(): Promise<void> {
    const settings = this.settingsState();
    if (!settings?.pushEnabled || !settings.pushAvailable || !this.isSupported() || Notification.permission !== 'default') {
      return;
    }
    const permission = await Notification.requestPermission();
    this.permissionState.set(permission);
    if (permission === 'granted') {
      await this.subscribe(settings);
    }
  }

  /** Turns notifications on or off for every browser of the user. Call it from a user action. */
  async setEnabled(enabled: boolean): Promise<void> {
    this.busyState.set(true);
    try {
      const settings = await firstValueFrom(this.api.updateNotificationSettings(enabled));
      this.settingsState.set(settings);
      if (!this.isSupported()) {
        return;
      }
      if (enabled) {
        if (Notification.permission === 'default') {
          this.permissionState.set(await Notification.requestPermission());
        }
        if (Notification.permission === 'granted') {
          await this.subscribe(settings);
        }
        return;
      }
      await this.unsubscribe();
    } finally {
      this.busyState.set(false);
    }
  }

  private async subscribe(settings: NotificationSettings): Promise<void> {
    if (!settings.publicKey) {
      return;
    }
    const revision = this.revision;
    const registration = await this.register();
    const key = decodeBase64Url(settings.publicKey);
    let subscription = await registration.pushManager.getSubscription();
    if (subscription && !sameKey(subscription.options.applicationServerKey, key)) {
      // The server rotated its key; a subscription for the old key would be refused.
      await subscription.unsubscribe();
      subscription = null;
    }
    subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
    const json = subscription.toJSON();
    if (!json.endpoint || !json.keys?.['p256dh'] || !json.keys['auth'] || revision !== this.revision) {
      return;
    }
    await firstValueFrom(this.api.subscribePush({
      endpoint: json.endpoint,
      keys: { p256dh: json.keys['p256dh'], auth: json.keys['auth'] },
      language: this.translate?.getCurrentLang() ?? navigator.language
    }), { defaultValue: undefined });
    if (revision === this.revision) {
      this.subscribedState.set(true);
    }
  }

  private async unsubscribe(): Promise<void> {
    this.subscribedState.set(false);
    const registration = await navigator.serviceWorker.getRegistration(this.scope());
    const subscription = await registration?.pushManager.getSubscription();
    if (!subscription) {
      return;
    }
    await firstValueFrom(this.api.unsubscribePush(subscription.endpoint), { defaultValue: undefined }).catch(() => undefined);
    await subscription.unsubscribe();
  }

  private register(): Promise<ServiceWorkerRegistration> {
    const push = this.config.push!;
    this.registration ??= (async () => {
      const url = new URL(push.serviceWorkerUrl, location.href);
      url.searchParams.set('app', new URL(push.openUrl ?? '/', location.href).href);
      if (push.icon) {
        url.searchParams.set('icon', new URL(push.icon, location.href).href);
      }
      return navigator.serviceWorker.register(url.href, { scope: this.scope() });
    })();
    return this.registration;
  }

  private scope(): string {
    const push = this.config.push;
    if (!push) {
      return '/';
    }
    return push.scope ?? new URL('./', new URL(push.serviceWorkerUrl, location.href)).pathname;
  }

  private isSupported(): boolean {
    return this.browser
      && !!this.config.push?.serviceWorkerUrl
      && globalThis.isSecureContext === true
      && 'serviceWorker' in navigator
      && 'PushManager' in globalThis
      && 'Notification' in globalThis;
  }
}

function decodeBase64Url(value: string): Uint8Array<ArrayBuffer> {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  const binary = atob(base64);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

function sameKey(current: ArrayBuffer | null, expected: Uint8Array): boolean {
  if (!current) {
    return false;
  }
  const bytes = new Uint8Array(current);
  return bytes.length === expected.length && bytes.every((byte, index) => byte === expected[index]);
}
