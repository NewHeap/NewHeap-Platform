import { Injectable, OnDestroy } from '@angular/core';
import type { HubConnection } from '@microsoft/signalr';
import { BehaviorSubject, Observable, Subject, Subscription } from 'rxjs';
import { NhBackgroundOperationChanged } from '../models/background-operation.models';
import { NhCommonModuleConfig } from '../models/config.models';
import { NhAppService } from './nh-app.service';
import { NhAuthService } from './nh-auth.service';

export type NhBackgroundOperationConnectionState =
  'disabled' | 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

@Injectable({ providedIn: 'root' })
export class NhBackgroundOperationLiveUpdateService implements OnDestroy {
  private readonly changedSubject = new Subject<NhBackgroundOperationChanged>();
  private readonly resyncSubject = new Subject<void>();
  private readonly connectionStateSubject = new BehaviorSubject<NhBackgroundOperationConnectionState>('disconnected');
  private readonly authSubscription: Subscription;
  private connection?: HubConnection;
  private connectPromise?: Promise<void>;
  private reconnectTimer?: ReturnType<typeof setTimeout>;
  private consumers = 0;
  private authorizationScopeKey: string;

  readonly changed$: Observable<NhBackgroundOperationChanged> = this.changedSubject.asObservable();
  readonly resync$: Observable<void> = this.resyncSubject.asObservable();
  readonly connectionState$: Observable<NhBackgroundOperationConnectionState> = this.connectionStateSubject.asObservable();

  constructor(
    private readonly config: NhCommonModuleConfig,
    private readonly auth: NhAuthService,
    private readonly app: NhAppService
  ) {
    this.authorizationScopeKey = this.getAuthorizationScopeKey();

    if (!config.backgroundOperations.liveUpdatesEnabled || app.isPlatformServer()) {
      this.connectionStateSubject.next('disabled');
    }

    this.authSubscription = auth.authSubject.subscribe(() => {
      const nextScopeKey = this.getAuthorizationScopeKey();
      if (nextScopeKey === this.authorizationScopeKey) {
        return;
      }

      this.authorizationScopeKey = nextScopeKey;
      if (this.consumers > 0 && this.isEnabled()) {
        void this.restart();
      }
    });
  }

  start(): void {
    this.consumers++;
    if (this.consumers === 1 && this.isEnabled()) {
      void this.ensureConnected();
    }
  }

  stop(): void {
    if (this.consumers === 0) {
      return;
    }

    this.consumers--;
    if (this.consumers === 0) {
      this.clearReconnectTimer();
      void this.stopConnection();
    }
  }

  ngOnDestroy(): void {
    this.consumers = 0;
    this.clearReconnectTimer();
    this.authSubscription.unsubscribe();
    void this.stopConnection();
    this.changedSubject.complete();
    this.resyncSubject.complete();
    this.connectionStateSubject.complete();
  }

  private isEnabled(): boolean {
    return this.config.backgroundOperations.liveUpdatesEnabled && !this.app.isPlatformServer();
  }

  private async restart(): Promise<void> {
    await this.stopConnection();
    if (this.consumers > 0) {
      await this.ensureConnected();
    }
  }

  private ensureConnected(): Promise<void> {
    if (this.connectPromise) {
      return this.connectPromise;
    }

    if (this.connection?.state === 'Connected') {
      return Promise.resolve();
    }

    this.connectPromise = this.connectCore().finally(() => {
      this.connectPromise = undefined;
    });
    return this.connectPromise;
  }

  private async connectCore(): Promise<void> {
    this.connectionStateSubject.next('connecting');
    try {
      this.connection ??= await this.buildConnection();
      await this.connection.start();
      this.connectionStateSubject.next('connected');
      this.resyncSubject.next();
    } catch {
      this.connectionStateSubject.next('disconnected');
      this.scheduleReconnect();
    }
  }

  private async buildConnection(): Promise<HubConnection> {
    const signalR = await import('@microsoft/signalr');
    const baseHubUrl = this.joinUrl(
      this.config.backgroundOperations.hubBaseUrl || this.config.baseUrl || this.config.apiBaseUrl,
      this.config.backgroundOperations.hubUrlSuffix
    );
    const divisionId = this.auth.getAuthorization()?.activeDivision?.id;
    const hubUrl = divisionId
      ? this.appendQueryParameter(baseHubUrl, 'divisionId', divisionId)
      : baseHubUrl;
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        withCredentials: true,
        accessTokenFactory: () =>
          this.config.authType === 'header'
            ? this.auth.getAuthorization()?.token ?? ''
            : ''
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connection.on('OperationChanged', (change: NhBackgroundOperationChanged) => {
      this.changedSubject.next(change);
    });
    connection.onreconnecting(() => this.connectionStateSubject.next('reconnecting'));
    connection.onreconnected(() => {
      this.connectionStateSubject.next('connected');
      this.resyncSubject.next();
    });
    connection.onclose(() => {
      this.connectionStateSubject.next('disconnected');
      this.scheduleReconnect();
    });
    return connection;
  }

  private async stopConnection(): Promise<void> {
    const connection = this.connection;
    this.connection = undefined;
    this.connectPromise = undefined;
    if (connection && connection.state !== 'Disconnected') {
      try {
        await connection.stop();
      } catch {
        // A failed transport is already disconnected from the application's
        // perspective. Polling remains the durable fallback.
      }
    }

    if (this.isEnabled()) {
      this.connectionStateSubject.next('disconnected');
    }
  }

  private scheduleReconnect(): void {
    if (this.consumers === 0 || this.reconnectTimer || !this.isEnabled()) {
      return;
    }

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      void this.ensureConnected();
    }, 5000);
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }
  }

  private joinUrl(base: string, suffix: string): string {
    return `${base.replace(/\/$/, '')}/${suffix.replace(/^\//, '')}`;
  }

  private appendQueryParameter(url: string, name: string, value: string): string {
    const separator = url.includes('?') ? '&' : '?';
    return `${url}${separator}${encodeURIComponent(name)}=${encodeURIComponent(value)}`;
  }

  private getAuthorizationScopeKey(): string {
    const authorization = this.auth.getAuthorization();
    return `${authorization?.user?.id ?? 'anonymous'}:${authorization?.activeDivision?.id ?? 'global'}`;
  }
}
