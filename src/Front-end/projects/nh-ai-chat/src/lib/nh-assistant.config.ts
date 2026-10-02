import { InjectionToken, Type } from '@angular/core';
import { Observable } from 'rxjs';
import { ClientContext } from './models/assistant-api.models';

/** Decides whether the current user may see and use the assistant. */
export interface NhAssistantAccessPolicy {
  canUse(): boolean | Observable<boolean>;
}

export interface NhAssistantConfig {
  /** Base URL of the assistant endpoints, for example `environment.api.baseUrl + '/assistant'`. */
  apiBaseUrl: string;
  /**
   * Returns the caller's bearer token, or null when the request should be anonymous. It runs
   * in the injection context of the assistant scope before every request, so it may call
   * `inject(...)`, for example `() => inject(MyAuthService).getAuthorization()?.token ?? null`.
   */
  getAccessToken: () => string | null | Promise<string | null>;
  /**
   * Returns what the user has open, sent with each message as untrusted page context.
   * Runs in the assistant's injection context when the panel opens and before every
   * message, like `getAccessToken`. The library validates and truncates the result; a
   * failing getter or an invalid shape only leaves the context out. Default: none.
   */
  getPageContext?: () => ClientContext | null | Promise<ClientContext | null>;
  /**
   * Stable, non-secret identity for browser UI state (for example a user id plus tenant id).
   * Runs in the assistant injection context. Return null on sign-out. Without this callback
   * the assistant keeps UI state in memory only. Never return an access token.
   */
  getStateScope?: () => string | null | Promise<string | null>;
  /** Injectable access policy. Default: always allowed. */
  accessPolicy?: Type<NhAssistantAccessPolicy>;
  /** Agent selected when the user has not chosen one. Default: the first agent the server lists. */
  defaultAgentId?: string;
  /** `'bundled'` merges the library's `en` and `nl` texts into `TranslateService`; `'host'` leaves translations to the host. Default `'bundled'`. */
  translations?: 'bundled' | 'host';
  /**
   * Router link of the host's assistant administration page. The panel shows a link to it
   * only when the server reports `canAdminister`. Default: no link.
   */
  adminRoute?: string | any[];
  /** Renders assistant text as sanitized Markdown. Default `{ enabled: true }`. */
  markdown?: { enabled: boolean };
  /**
   * Live updates of shared and parallel conversations through the server's SignalR hub, which
   * `GET status` reports. Uses `@microsoft/signalr`, loaded on first use. Default `true`; when off
   * or unavailable the assistant reloads snapshots instead.
   */
  liveUpdates?: boolean;
  /**
   * Base the server's hub path is appended to, for example `'/api'` when a proxy serves the API
   * below a prefix, as for the NewHeap background operation hub. Default: the origin of `apiBaseUrl`.
   */
  hubBaseUrl?: string;
  /**
   * Web Push notifications for people who are away. Copy the bundled worker from
   * `node_modules/@newheap/platform-ai-chat/push` into the application's assets and point
   * `serviceWorkerUrl` to it. Without this setting the assistant never subscribes the browser.
   */
  push?: NhAssistantPushConfig;
  /**
   * Builds the invitation link an owner copies. Default: the current page with
   * `#nh-assistant-join=<conversationId>.<token>`; the panel joins when the page opens with it.
   */
  buildShareLink?: (conversationId: string, token: string) => string;
}

export interface NhAssistantPushConfig {
  /** URL of the bundled worker, for example `/nh-assistant/nh-assistant-push-worker.js`. */
  serviceWorkerUrl: string;
  /**
   * Scope of the worker. Default: the worker's folder. Keep it apart from an application service
   * worker; the assistant worker only shows notifications and never controls pages.
   */
  scope?: string;
  /** Page a notification opens when no window of the application is open. Default `/`. */
  openUrl?: string;
  /** Icon URL of the notifications. */
  icon?: string;
}

export const NH_ASSISTANT_CONFIG = new InjectionToken<NhAssistantConfig>('NH_ASSISTANT_CONFIG');

export const NH_ASSISTANT_ACCESS_POLICY = new InjectionToken<NhAssistantAccessPolicy>('NH_ASSISTANT_ACCESS_POLICY');

/** The `fetch` function type the assistant client uses. */
export type NhAssistantFetch = (input: string, init: RequestInit) => Promise<Response>;

/**
 * Transport used by the assistant client. It is deliberately independent of the host's
 * `HttpClient` and interceptors, because streaming responses need `fetch` and the access
 * token comes from `NhAssistantConfig.getAccessToken`. Tests and the mock API from
 * `@newheap/platform-ai-chat/testing` replace it.
 */
export const NH_ASSISTANT_FETCH = new InjectionToken<NhAssistantFetch>('NH_ASSISTANT_FETCH', {
  providedIn: 'root',
  factory: () => (input: string, init: RequestInit) => globalThis.fetch(input, init)
});

/** Default policy: every user who can reach the host may use the assistant. */
export class NhAssistantAllowAllAccessPolicy implements NhAssistantAccessPolicy {
  canUse(): boolean {
    return true;
  }
}
