import { DestroyRef, EnvironmentInjector, Injectable, PLATFORM_ID, inject, runInInjectionContext, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import type { HubConnection } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import {
  LiveAgentWorkChanged,
  LiveAgentWorkWatch,
  LiveConversationChanged,
  LiveConversationEvent,
  LiveConversationRead,
  LiveConversationRemoved
} from '../models/assistant-live.models';
import { NH_ASSISTANT_CONFIG } from '../nh-assistant.config';
import { nhAssistantRequestHeaders } from './nh-assistant-transport';

export type NhAssistantLiveState = 'off' | 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

const reconnectDelaysMs = [0, 2_000, 5_000, 10_000, 30_000];
const restartDelayMs = 30_000;
/** How long a watch of the whole application's agent work lasts when the server does not say. */
const defaultWatchRenewalSeconds = 300;
/** The shortest renewal of a watch, whatever the server says. */
const minimumWatchRenewalSeconds = 30;
/** Query parameters the library and SignalR set themselves. */
const reservedHubQueryParameters = new Set(['access_token', 'id', 'negotiateversion']);

/**
 * Live updates of the assistant hub: conversation changes, read positions, removals, the turn
 * events of other sessions and changes to the work of agents that run on their own. Connects only
 * while the store asks for it, reconnects on its own and emits `resync` after every (re)connect,
 * because updates sent while disconnected are lost. The work of agents for the whole application
 * lists no members, so it arrives only while a screen watches it (`watchAgentWork`).
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
  private readonly agentWorkSubject = new Subject<LiveAgentWorkChanged>();
  private readonly resyncSubject = new Subject<void>();
  private readonly stateSignal = signal<NhAssistantLiveState>('off');

  private connection?: HubConnection;
  private hubPath: string | null = null;
  private hubUrl: string | null = null;
  private revision = 0;
  private restartTimer?: ReturnType<typeof setTimeout>;
  /** The screens that watch the work of agents for the whole application. */
  private agentWorkWatchers = 0;
  private watchTimer?: ReturnType<typeof setTimeout>;

  readonly changed$: Observable<LiveConversationChanged> = this.changedSubject.asObservable();
  readonly read$: Observable<LiveConversationRead> = this.readSubject.asObservable();
  readonly removed$: Observable<LiveConversationRemoved> = this.removedSubject.asObservable();
  readonly events$: Observable<LiveConversationEvent> = this.eventSubject.asObservable();
  /**
   * A run, proposal or feedback of an agent thread changed. It carries no content; components
   * that show the thread reload it.
   */
  readonly agentWork$: Observable<LiveAgentWorkChanged> = this.agentWorkSubject.asObservable();
  /** Reload snapshots: emitted after every (re)connect. */
  readonly resync$: Observable<void> = this.resyncSubject.asObservable();
  readonly state = this.stateSignal.asReadonly();

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.agentWorkWatchers = 0;
      void this.stop();
      this.changedSubject.complete();
      this.readSubject.complete();
      this.removedSubject.complete();
      this.eventSubject.complete();
      this.agentWorkSubject.complete();
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
        void this.renewAgentWorkWatch();
      }
    } catch {
      // Live updates are optional; snapshots keep the assistant correct. Try again later.
      if (revision === this.revision) {
        this.stateSignal.set('disconnected');
        this.scheduleRestart(revision);
      }
    }
  }

  /**
   * Follows the work of agents for the whole application, such as their proposals and runs, while the
   * returned function has not been called; a screen that shows it, such as the approval inbox or a
   * board, watches it while it is shown. The whole application lists no members, so the hub sends its
   * agents' work only to connections that watch it: it joins the work the server decides the person
   * may read, the agent's view policy included, renews the watch when the server says and after every
   * reconnect, and so checks the person's rights again. The watch ends when the last screen releases
   * it. Without a connection nothing arrives and screens keep their own reload.
   */
  watchAgentWork(): () => void {
    this.agentWorkWatchers++;
    if (this.agentWorkWatchers === 1) {
      void this.renewAgentWorkWatch();
    }

    let released = false;
    return () => {
      if (released) {
        return;
      }
      released = true;
      this.agentWorkWatchers = Math.max(0, this.agentWorkWatchers - 1);
      if (this.agentWorkWatchers === 0) {
        clearTimeout(this.watchTimer);
        const connection = this.connection;
        if (connection && this.stateSignal() === 'connected') {
          connection.invoke('UnwatchAgentWork').catch(() => undefined);
        }
      }
    };
  }

  async stop(): Promise<void> {
    this.revision++;
    this.hubUrl = null;
    clearTimeout(this.restartTimer);
    clearTimeout(this.watchTimer);
    const connection = this.connection;
    this.connection = undefined;
    this.stateSignal.set('off');
    if (connection) {
      await connection.stop().catch(() => undefined);
    }
  }

  private async build(url: string): Promise<HubConnection> {
    const signalR = await import('@microsoft/signalr');
    const headers = await nhAssistantRequestHeaders(this.config, this.injector);
    const hubUrl = await this.withQueryParameters(url);
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: async () => (await this.token()) ?? '',
        headers
      })
      .withAutomaticReconnect(reconnectDelaysMs)
      .configureLogging(signalR.LogLevel.None)
      .build();

    connection.on('ConversationChanged', (message: LiveConversationChanged) => this.changedSubject.next(message));
    connection.on('ConversationRead', (message: LiveConversationRead) => this.readSubject.next(message));
    connection.on('ConversationRemoved', (message: LiveConversationRemoved) => this.removedSubject.next(message));
    connection.on('ConversationEvent', (message: LiveConversationEvent) => this.eventSubject.next(message));
    connection.on('AgentWorkChanged', (message: LiveAgentWorkChanged) => this.agentWorkSubject.next(message));
    connection.onreconnecting(() => this.stateSignal.set('reconnecting'));
    connection.onreconnected(() => {
      this.stateSignal.set('connected');
      this.resyncSubject.next();
      // A new connection belongs to no watch yet.
      void this.renewAgentWorkWatch();
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

  /**
   * Asks the hub to watch the work of agents for the whole application for the screens that watch it,
   * and schedules the renewal the server asks for. An older server without watching, or a failed call,
   * is tried again later; live updates stay optional.
   */
  private async renewAgentWorkWatch(): Promise<void> {
    clearTimeout(this.watchTimer);
    const connection = this.connection;
    if (this.agentWorkWatchers === 0 || !connection || this.stateSignal() !== 'connected') {
      return;
    }

    let seconds = defaultWatchRenewalSeconds;
    try {
      const watch = await connection.invoke<LiveAgentWorkWatch | null>('WatchAgentWork');
      if (typeof watch?.refreshAfterSeconds === 'number' && Number.isFinite(watch.refreshAfterSeconds)) {
        seconds = Math.max(minimumWatchRenewalSeconds, watch.refreshAfterSeconds);
      }
    } catch {
      // Tried again at the next renewal or reconnect.
    }

    if (connection === this.connection && this.agentWorkWatchers > 0) {
      clearTimeout(this.watchTimer);
      this.watchTimer = setTimeout(() => void this.renewAgentWorkWatch(), seconds * 1_000);
    }
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

  /**
   * The hub URL with the host's `getHubQueryParameters`, which reach the hub on every transport,
   * unlike headers on WebSockets and server-sent events.
   */
  private async withQueryParameters(url: string): Promise<string> {
    const getter = this.config.getHubQueryParameters;
    if (!getter) {
      return url;
    }

    let parameters: Record<string, string> | null;
    try {
      parameters = await runInInjectionContext(this.injector, () => getter());
    } catch {
      return url;
    }

    const target = new URL(url);
    for (const [name, value] of Object.entries(parameters ?? {})) {
      if (typeof value === 'string' && value.length > 0 && !reservedHubQueryParameters.has(name.toLowerCase())) {
        target.searchParams.set(name, value);
      }
    }
    return target.toString();
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
