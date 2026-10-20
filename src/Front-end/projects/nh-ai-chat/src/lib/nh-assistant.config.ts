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
   * Returns extra headers for every assistant request, for example the NewHeap active division:
   * `() => ({ 'X-NH-ActiveDivisionId': inject(MyAuthService).getAuthorization()?.activeDivision?.id ?? '' })`.
   * Runs in the assistant's injection context before every request, like `getAccessToken`, and
   * when the live-update connection starts. `Authorization`, `Accept`, `Accept-Language` and
   * `Content-Type` stay under the library's control, and empty values are left out. Browsers send
   * no custom headers with the WebSocket and server-sent event transports of the live connection;
   * pass what the hub needs with `getHubQueryParameters`. Default: none.
   */
  getRequestHeaders?: () => Record<string, string> | null | Promise<Record<string, string> | null>;
  /**
   * Returns query parameters for the live-update hub URL: the values the hub needs that browsers
   * cannot send as headers, because the WebSocket and server-sent event transports carry none.
   * Without them a hub that requires such a value refuses those transports and the connection
   * falls back to long polling. For example the NewHeap active division, which the platform's JWT
   * handler copies from `divisionId` to `X-NH-ActiveDivisionId` on `/hub` paths:
   * `() => ({ divisionId: inject(MyAuthService).getAuthorization()?.activeDivision?.id ?? '' })`.
   * Runs in the assistant's injection context each time the connection starts. `access_token`,
   * `id` and `negotiateVersion` stay under the control of the library and SignalR, empty values
   * are left out and a failing getter only leaves the parameters out. Never return a secret: URLs
   * appear in server and proxy logs. Default: none.
   */
  getHubQueryParameters?: () => Record<string, string> | null | Promise<Record<string, string> | null>;
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
  /**
   * Replaces the server's links to subjects, which come from the link template of the subject type
   * (`AddSubjectType(...).OpenAt("/projects/{id}")`), with the host's own routes, per subject type:
   * for example `{ project: subject => ['/projects', subject.id] }`. Return router commands or a URL
   * within the application, `null` to show no link, or `undefined` to keep the server's link.
   * Runs in the assistant's injection context. Default: the server's links.
   */
  subjectLinks?: Record<string, NhAssistantSubjectLinkBuilder>;
  /**
   * How MCP authorizations open. `popup` (default) opens a window from the click and stays on the
   * page. `redirect` sends the page to the authorization server and back to `mcpConnectReturnUrl`,
   * where the assistant completes the connection; its origin must be in
   * `NewHeap:AI:Assistant:Mcp:OAuth:ReturnOrigins`.
   */
  mcpConnectMode?: 'popup' | 'redirect';
  /** The page a redirect-mode authorization returns to. Default: the current address without its fragment. */
  mcpConnectReturnUrl?: () => string;
}

/** A subject an agent works on, by its type and id. */
export interface NhAssistantSubjectRef {
  type: string;
  id: string;
}

/**
 * Builds the link to one subject in the host application: router commands such as
 * `['/projects', subject.id]`, a URL such as `/projects/7`, `null` for no link, or `undefined` to keep
 * the server's link.
 */
export type NhAssistantSubjectLinkBuilder = (subject: NhAssistantSubjectRef) => readonly unknown[] | string | null | undefined;

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
