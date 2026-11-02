import { DestroyRef, Injectable, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { TranslateService } from '@ngx-translate/core';
import { Observable, Subject, finalize, firstValueFrom, shareReplay, tap } from 'rxjs';
import { NotificationSettings, UpdateNotificationSettingsRequest } from '../models/assistant-api.models';
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
const openAlertsMessage = 'nh-assistant.open-alerts';

/**
 * A clicked notification of alerts: one alert with its `alertId`, or a digest with its `digestId`.
 * Either opens the host's alert list.
 */
export interface NhAssistantAlertNotificationClick {
  alertId?: string;
  digestId?: string;
}

/**
 * Keeps this browser's Web Push subscription in line with the user's choice. Notifications are on
 * by default; because browsers only ask for permission after a user action, the assistant asks
 * when the user sends a message or turns notifications on. The bundled service worker shows a
 * notification only when no window of the application is focused.
 *
 * It also holds the person's notification settings (`GET notifications`) for every part of the page
 * that shows them (FB87-13): the assistant's start, `nh-assistant-notification-preferences` and the push
 * switch share one request while it is in flight (`loadSettings`), activating push for the signed-in user
 * reuses settings that were loaded for them already, and saving choices (`updatePreferences`) keeps
 * `settings` current for all of them. Forgetting the user (`deactivate`) drops all of it.
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
  private readonly openRunSubject = new Subject<string>();
  private readonly openAlertsSubject = new Subject<NhAssistantAlertNotificationClick>();
  private registration?: Promise<ServiceWorkerRegistration>;
  /** One more for every user forgotten, so nothing loaded for an earlier user is stored. */
  private revision = 0;
  /** The `GET notifications` in flight, which every caller shares until it ends. */
  private loading?: Observable<NotificationSettings>;
  /** The activation of push for the signed-in user, which repeated calls share. */
  private activation?: Promise<void>;
  /** One more for every activation started or stopped, so a stopped one changes nothing when it ends. */
  private activationRevision = 0;
  private readonly onWorkerMessage = (event: MessageEvent) => {
    const data = event.data as { type?: unknown; conversationId?: unknown; runId?: unknown; alertId?: unknown; digestId?: unknown } | null;
    if (data?.type === openConversationMessage && typeof data.conversationId === 'string') {
      this.openNotification(data.conversationId, typeof data.runId === 'string' ? data.runId : undefined);
    }
    if (data?.type === openAlertsMessage) {
      this.openAlerts({
        alertId: typeof data.alertId === 'string' ? data.alertId : undefined,
        digestId: typeof data.digestId === 'string' ? data.digestId : undefined
      });
    }
  };

  /** The caller's server-side choice, or `null` before it was loaded. */
  readonly settings = this.settingsState.asReadonly();
  readonly busy = this.busyState.asReadonly();
  /**
   * Conversations a clicked notification asks to open; the assistant panel opens them. A
   * notification about the proposals of a run goes to `openRun$` instead while the application
   * subscribes to it.
   */
  readonly openConversation$: Observable<string> = this.openSubject.asObservable();
  /**
   * Runs whose proposals a clicked notification is about, for the application to open its inbox of
   * the run, for example `nh-assistant-agent-inbox` with `[runId]`, which lists every proposal of the
   * run the person may decide. While the application subscribes, such a click emits the run here
   * instead of opening the conversation in the panel; without a subscriber it opens the conversation
   * as before. Subscribe in the application shell, so a notification that opens a new window finds
   * the subscription.
   */
  readonly openRun$: Observable<string> = this.openRunSubject.asObservable();
  /**
   * Clicked notifications of alerts, one alert or a digest, for the application to open its alert list,
   * for example with `nh-assistant-alerts`. Subscribe in the application shell. Without a subscriber a
   * click in an open window goes to `push.alertsUrl` when it is set; a click without an open window
   * opens that page in a new one.
   */
  readonly openAlerts$: Observable<NhAssistantAlertNotificationClick> = this.openAlertsSubject.asObservable();
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
      this.openRunSubject.complete();
      this.openAlertsSubject.complete();
    });
    if (this.browser && this.isSupported()) {
      navigator.serviceWorker.addEventListener('message', this.onWorkerMessage);
      navigator.serviceWorker.startMessages();
    }
  }

  /**
   * Opens what a clicked notification is about: the run through `openRun$` when the notification
   * names one and the application subscribes, otherwise the conversation through
   * `openConversation$`. The bundled worker and the panel's notification fragments call it.
   */
  openNotification(conversationId: string, runId?: string): void {
    if (runId && this.openRunSubject.observed) {
      this.openRunSubject.next(runId);
      return;
    }

    this.openSubject.next(conversationId);
  }

  /**
   * Opens the alert list for a clicked notification of alerts: through `openAlerts$` while the
   * application subscribes, otherwise by going to `push.alertsUrl` when it is set. The bundled worker
   * calls it.
   */
  openAlerts(click: NhAssistantAlertNotificationClick): void {
    if (this.openAlertsSubject.observed) {
      this.openAlertsSubject.next(click);
      return;
    }

    const alertsUrl = this.config.push?.alertsUrl;
    if (this.browser && alertsUrl) {
      location.assign(new URL(alertsUrl, location.href).href);
    }
  }

  /**
   * The person's notification settings as the server has them now (`GET notifications`), also stored in
   * `settings`. Callers while a request is in flight share it, so a page whose parts all show the
   * settings asks once. What arrives after the user was forgotten is never stored nor shared.
   */
  loadSettings(): Observable<NotificationSettings> {
    if (this.loading) {
      return this.loading;
    }

    const revision = this.revision;
    const loading = this.api.getNotificationSettings().pipe(
      tap(settings => {
        if (revision === this.revision) {
          this.settingsState.set(settings);
        }
      }),
      finalize(() => {
        if (this.loading === loading) {
          this.loading = undefined;
        }
      }),
      shareReplay({ bufferSize: 1, refCount: false })
    );
    this.loading = loading;
    return loading;
  }

  /**
   * Changes the person's notification choices (`PUT notifications`) and stores the settings the server
   * answers with in `settings`, so every part of the page shows them.
   */
  updatePreferences(request: UpdateNotificationSettingsRequest): Observable<NotificationSettings> {
    const revision = this.revision;
    return this.api.updateNotificationPreferences(request).pipe(
      tap(settings => {
        if (revision === this.revision) {
          this.settingsState.set(settings);
        }
      })
    );
  }

  /**
   * Loads the user's choice and, when permission was granted before, renews this browser's
   * subscription, which also moves it to the current user and to a rotated server key. Settings that
   * were loaded for the user already are reused, and calls while an activation runs share it, so the
   * assistant's start and a page that shows the notification choices ask the server once.
   */
  async activate(serverSupportsPush: boolean): Promise<void> {
    if (!serverSupportsPush || !this.isSupported()) {
      this.activationRevision++;
      this.activation = undefined;
      this.settingsState.set(null);
      return;
    }

    this.activation ??= this.runActivation(this.revision, ++this.activationRevision);
    return this.activation;
  }

  /**
   * Forgets the user. The browser subscription is cancelled, so a previous user on a shared device
   * stops receiving notifications even when the server cannot be told.
   */
  async deactivate(): Promise<void> {
    this.revision++;
    this.loading = undefined;
    this.activation = undefined;
    this.settingsState.set(null);
    await this.cancelBrowserSubscription();
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

  private async runActivation(revision: number, activationRevision: number): Promise<void> {
    const current = () => revision === this.revision && activationRevision === this.activationRevision;
    try {
      const settings = this.settingsState() ?? await firstValueFrom(this.loadSettings());
      if (!current()) {
        return;
      }
      this.settingsState.set(settings);
      this.permissionState.set(Notification.permission);
      if (settings.pushEnabled && Notification.permission === 'granted') {
        await this.subscribe(settings);
      } else if (!settings.pushEnabled) {
        // A person who turned notifications off gets none in this browser, also none of an earlier person.
        await this.cancelBrowserSubscription();
      }
    } catch {
      // Notifications are optional; the assistant works without them, and the next activation tries again.
      if (current()) {
        this.settingsState.set(null);
        this.activation = undefined;
      }
    }
  }

  /** Cancels this browser's subscription without telling the server, which drops it once it is gone. */
  private async cancelBrowserSubscription(): Promise<void> {
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
      url.searchParams.set('alerts', new URL(push.alertsUrl ?? push.openUrl ?? '/', location.href).href);
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
