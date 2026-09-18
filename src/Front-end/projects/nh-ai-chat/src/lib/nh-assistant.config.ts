import { InjectionToken, Type } from '@angular/core';
import { Observable } from 'rxjs';

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
