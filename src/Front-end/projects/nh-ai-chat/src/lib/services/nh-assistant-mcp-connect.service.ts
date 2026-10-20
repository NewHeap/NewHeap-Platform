import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { NEVER, Observable, catchError, map, of, switchMap } from 'rxjs';
import { toNhAssistantError } from '../internal/nh-assistant-errors';
import { McpAuthorizationStart, McpAuthorizeRequest, McpCompletion } from '../models/assistant-admin.models';
import { NH_ASSISTANT_CONFIG } from '../nh-assistant.config';
import { NhAssistantApiService } from './nh-assistant-api.service';
import {
  NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW,
  nhAssistantConnectMcp,
  nhAssistantMcpAuthorizationFromFragment
} from './nh-assistant-mcp-authorization';

/**
 * The outcome of an authorization the page returned from in redirect mode: the activated connection,
 * or the failure code to show. `serverId` names the server whose connection the page left for, when
 * this browser tab remembered it, so a screen shows the outcome at that server only.
 */
export type NhAssistantMcpReturnOutcome =
  | { completion: McpCompletion; code?: undefined; serverId?: string | null }
  | { code: string; completion?: undefined; serverId?: string | null };

/** Where this tab remembers the server whose connection it left for, until it returns. */
const leftForKey = 'nh-assistant.mcp-connect.server';

/**
 * Connects MCP accounts the way the host configured (`mcpConnectMode`): in a popup that the click
 * opens, or by sending the page to the authorization server and back. Every authorization completes
 * with the caller's own session.
 */
@Injectable()
export class NhAssistantMcpConnectService {
  private readonly api = inject(NhAssistantApiService);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly authorizationWindow = inject(NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW);
  private readonly document = inject(DOCUMENT);

  /**
   * The outcome of the redirect-mode authorization this page opened with, until a screen showed it.
   * `nh-assistant-connection-required` shows the outcome of its server without taking it; the
   * connection lists of the preferences and the administration show it and take it.
   */
  readonly returned = signal<NhAssistantMcpReturnOutcome | null>(null);

  /** Whether authorizations leave the page instead of opening a popup. */
  get redirects(): boolean {
    return this.config.mcpConnectMode === 'redirect';
  }

  /**
   * Connects the caller's own account to a server each person connects to. Call it in the click
   * handler, so the popup is allowed. In redirect mode the page leaves and the observable never
   * emits; the page completes the authorization when it returns.
   */
  connect(serverId: string, options: { allowBackgroundUse?: boolean } = {}): Observable<McpCompletion> {
    return this.run(
      request => this.api.authorizeMcpConnection(serverId, { ...request, allowBackgroundUse: options.allowBackgroundUse ?? null }),
      serverId);
  }

  /**
   * Runs any authorization start, for example an administrator's organization connection, in the
   * configured mode. `serverId` lets the page show the outcome at that server after a redirect.
   */
  run(start: (request: McpAuthorizeRequest) => Observable<McpAuthorizationStart>, serverId?: string): Observable<McpCompletion> {
    if (!this.redirects) {
      return nhAssistantConnectMcp(this.authorizationWindow.open(), start({}), request => this.api.completeMcpAuthorization(request));
    }

    const returnUrl = this.config.mcpConnectReturnUrl?.() ?? withoutFragment(this.document.location.href);
    return start({ returnUrl }).pipe(switchMap(started => {
      this.rememberLeftFor(serverId ?? null);
      this.navigate(started.authorizationUrl);
      return NEVER;
    }));
  }

  /** Leaves the page for an authorization in redirect mode. */
  navigate(url: string): void {
    this.document.location.assign(url);
  }

  /**
   * Completes a redirect-mode authorization when the page opened with its fragment, removes the
   * fragment from the address and records the outcome in `returned`: the connection, or the code of
   * whatever failed, also when completing it failed. Emits null without a fragment and never errors.
   */
  completeReturn(): Observable<NhAssistantMcpReturnOutcome | null> {
    const outcome = nhAssistantMcpAuthorizationFromFragment(this.document.location.hash);
    if (!outcome) {
      return of(null);
    }

    // The fragment carries a one-time code; it leaves the address before anything else happens.
    this.removeFragment();
    const serverId = this.takeLeftFor();

    if (outcome.completionCode === undefined) {
      return of(this.record({ code: outcome.code, serverId }));
    }

    const request = { flowId: outcome.flowId, completionCode: outcome.completionCode };
    return this.api.completeMcpAuthorization(request).pipe(
      map(completion => this.record({ completion, serverId: completion.serverId })),
      catchError((error: unknown) => of(this.record({ code: toNhAssistantError(error).code, serverId }))));
  }

  private record(outcome: NhAssistantMcpReturnOutcome): NhAssistantMcpReturnOutcome {
    this.returned.set(outcome);
    return outcome;
  }

  private removeFragment(): void {
    const view = this.document.defaultView;
    try {
      view?.history.replaceState(view.history.state, '', withoutFragment(this.document.location.href));
    } catch {
      // A page that may not change its history keeps the fragment; the code inside works only once.
    }
  }

  /** Remembers in this tab which server's connection the page leaves for. */
  private rememberLeftFor(serverId: string | null): void {
    try {
      const storage = this.document.defaultView?.sessionStorage;
      if (serverId) {
        storage?.setItem(leftForKey, serverId);
      } else {
        storage?.removeItem(leftForKey);
      }
    } catch {
      // Without session storage the outcome shows in the connection lists only.
    }
  }

  /** The server this tab left for, forgotten once read. */
  private takeLeftFor(): string | null {
    try {
      const storage = this.document.defaultView?.sessionStorage;
      const serverId = storage?.getItem(leftForKey) ?? null;
      storage?.removeItem(leftForKey);
      return serverId;
    } catch {
      return null;
    }
  }
}

function withoutFragment(url: string): string {
  const index = url.indexOf('#');
  return index < 0 ? url : url.slice(0, index);
}
