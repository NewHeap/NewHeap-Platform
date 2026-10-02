import { DestroyRef, EnvironmentInjector, Injectable, PLATFORM_ID, inject, runInInjectionContext, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import type { HubConnection } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import {
  LiveConversationChanged,
  LiveConversationEvent,
  LiveConversationRead,
  LiveConversationRemoved
} from '../models/assistant-live.models';
import { NH_ASSISTANT_CONFIG } from '../nh-assistant.config';

export type NhAssistantLiveState = 'off' | 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

const reconnectDelaysMs = [0, 2_000, 5_000, 10_000, 30_000];
const restartDelayMs = 30_000;

/**
 * Live updates of the assistant hub: conversation changes, read positions, removals and the
 * turn events of other sessions. Connects only while the store asks for it, reconnects on its
 * own and emits `resync` after every (re)connect, because updates sent while disconnected are lost.
 */
@Injectable()
export class NhAssistantLiveService {
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly injector = inject(EnvironmentInjector);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly changedSubject = new Subject<LiveConversationChanged>();
  private readonly readSubject = new Subject<LiveConversationRead>();
  private readonly removedSubject = new Subject<LiveConversationRemoved>();
  private readonly eventSubject = new Subject<LiveConversationEvent>();
  private readonly resyncSubject = new Subject<void>();
  private readonly stateSignal = signal<NhAssistantLiveState>('off');

  private connection?: HubConnection;
  private hubPath: string | null = null;
  private hubUrl: string | null = null;
  private revision = 0;
  private restartTimer?: ReturnType<typeof setTimeout>;

  readonly changed$: Observable<LiveConversationChanged> = this.changedSubject.asObservable();
  readonly read$: Observable<LiveConversationRead> = this.readSubject.asObservable();
  readonly removed$: Observable<LiveConversationRemoved> = this.removedSubject.asObservable();
  readonly events$: Observable<LiveConversationEvent> = this.eventSubject.asObservable();
  /** Reload snapshots: emitted after every (re)connect. */
  readonly resync$: Observable<void> = this.resyncSubject.asObservable();
  readonly state = this.stateSignal.asReadonly();

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      void this.stop();
      this.changedSubject.complete();
      this.readSubject.complete();
      this.removedSubject.complete();
      this.eventSubject.complete();
      this.resyncSubject.complete();
    });
  }

  /**
   * Connects to the hub at `hubPath`, or disconnects when it is `null`. Calling it again with the
   * same path keeps the connection; another path or `restart` reconnects, for example after the
   * signed-in user changed.
   */
  async connect(hubPath: string | null, restart = false): Promise<void> {
    const url = this.browser && this.config.liveUpdates !== false && hubPath ? this.resolveUrl(hubPath) : null;
    if (!restart && url === this.hubUrl && this.connection) {
      return;
    }

    await this.stop();
    this.hubPath = url ? hubPath : null;
    this.hubUrl = url;
    if (!url) {
      return;
    }

    const revision = ++this.revision;
    this.stateSignal.set('connecting');
    try {
      const connection = await this.build(url);
      if (revision !== this.revision) {
        await connection.stop().catch(() => undefined);
        return;
      }
      this.connection = connection;
      await connection.start();
      if (revision === this.revision) {
        this.stateSignal.set('connected');
        this.resyncSubject.next();
      }
    } catch {
      // Live updates are optional; snapshots keep the assistant correct. Try again later.
      if (revision === this.revision) {
        this.stateSignal.set('disconnected');
        this.scheduleRestart(revision);
      }
    }
  }

  async stop(): Promise<void> {
    this.revision++;
    this.hubUrl = null;
    clearTimeout(this.restartTimer);
    const connection = this.connection;
    this.connection = undefined;
    this.stateSignal.set('off');
    if (connection) {
      await connection.stop().catch(() => undefined);
    }
  }

  private async build(url: string): Promise<HubConnection> {
    const signalR = await import('@microsoft/signalr');
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(url, {
        accessTokenFactory: async () => (await this.token()) ?? ''
      })
      .withAutomaticReconnect(reconnectDelaysMs)
      .configureLogging(signalR.LogLevel.None)
      .build();

    connection.on('ConversationChanged', (message: LiveConversationChanged) => this.changedSubject.next(message));
    connection.on('ConversationRead', (message: LiveConversationRead) => this.readSubject.next(message));
    connection.on('ConversationRemoved', (message: LiveConversationRemoved) => this.removedSubject.next(message));
    connection.on('ConversationEvent', (message: LiveConversationEvent) => this.eventSubject.next(message));
    connection.onreconnecting(() => this.stateSignal.set('reconnecting'));
    connection.onreconnected(() => {
      this.stateSignal.set('connected');
      this.resyncSubject.next();
    });
    connection.onclose(() => {
      if (this.connection === connection) {
        this.connection = undefined;
        this.stateSignal.set('disconnected');
        this.scheduleRestart(this.revision);
      }
    });
    return connection;
  }

  /** Starts over after the automatic reconnect gave up or the first start failed. */
  private scheduleRestart(revision: number): void {
    clearTimeout(this.restartTimer);
    this.restartTimer = setTimeout(() => {
      if (revision === this.revision && this.hubPath) {
        void this.connect(this.hubPath, true);
      }
    }, restartDelayMs);
  }

  private async token(): Promise<string | null> {
    try {
      return await runInInjectionContext(this.injector, () => this.config.getAccessToken());
    } catch {
      return null;
    }
  }

  /** `hubBaseUrl` + `hubPath`, or the hub path on the origin of `apiBaseUrl`. */
  private resolveUrl(hubPath: string): string {
    const page = globalThis.location?.href ?? 'http://localhost';
    if (this.config.hubBaseUrl) {
      const base = new URL(this.config.hubBaseUrl, page).toString().replace(/\/+$/, '');
      return `${base}/${hubPath.replace(/^\/+/, '')}`;
    }
    return new URL(hubPath, new URL(this.config.apiBaseUrl, page).origin).toString();
  }
}
