import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { NEVER, Observable, catchError, map, of, switchMap } from 'rxjs';
import { McpAuthorizationStart, McpAuthorizeRequest, McpCompletion } from '../models/assistant-admin.models';
import { NH_ASSISTANT_CONFIG } from '../nh-assistant.config';
import { NhAssistantApiService } from './nh-assistant-api.service';
import {
  NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW,
  nhAssistantConnectMcp,
  nhAssistantMcpAuthorizationFromFragment
} from './nh-assistant-mcp-authorization';
import { NhAssistantApiError } from './nh-assistant-transport';

/** The outcome of an authorization the page returned from in redirect mode. */
export type NhAssistantMcpReturnOutcome =
  | { completion: McpCompletion; code?: undefined }
  | { code: string; completion?: undefined };

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

  /** The outcome of the redirect-mode authorization this page opened with, until it is shown. */
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
    return this.run(request => this.api.authorizeMcpConnection(serverId, { ...request, allowBackgroundUse: options.allowBackgroundUse ?? null }));
  }

  /**
   * Runs any authorization start, for example an administrator's organization connection, in the
   * configured mode.
   */
  run(start: (request: McpAuthorizeRequest) => Observable<McpAuthorizationStart>): Observable<McpCompletion> {
    if (this.redirects) {
      const returnUrl = this.config.mcpConnectReturnUrl?.() ?? withoutFragment(this.document.location.href);
      return start({ returnUrl }).pipe(switchMap(started => {
        this.navigate(started.authorizationUrl);
        return NEVER;
      }));
    }
    return nhAssistantConnectMcp(this.authorizationWindow.open(), start({}), request => this.api.completeMcpAuthorization(request));
  }

  /** Leaves the page for an authorization in redirect mode. */
  navigate(url: string): void {
    this.document.location.assign(url);
  }

  /**
   * Completes a redirect-mode authorization when the page opened with its fragment, removes the
   * fragment from the address and records the outcome in `returned`. Emits null without one.
   */
  completeReturn(): Observable<NhAssistantMcpReturnOutcome | null> {
    const location = this.document.location;
    const outcome = nhAssistantMcpAuthorizationFromFragment(location.hash);
    if (!outcome) {
      return of(null);
    }
    this.document.defaultView?.history.replaceState(this.document.defaultView.history.state, '', withoutFragment(location.href));
    const completed: Observable<NhAssistantMcpReturnOutcome> = outcome.completionCode !== undefined
      ? this.api.completeMcpAuthorization({ flowId: outcome.flowId, completionCode: outcome.completionCode }).pipe(
        map(completion => ({ completion })),
        catchError((error: unknown) => of({ code: error instanceof NhAssistantApiError ? error.code : 'assistant-server' })))
      : of({ code: outcome.code });
    return completed.pipe(map(result => {
      this.returned.set(result);
      return result;
    }));
  }
}

function withoutFragment(url: string): string {
  const index = url.indexOf('#');
  return index < 0 ? url : url.slice(0, index);
}
