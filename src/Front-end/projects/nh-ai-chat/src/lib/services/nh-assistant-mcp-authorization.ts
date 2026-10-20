import { DOCUMENT } from '@angular/common';
import { InjectionToken, inject } from '@angular/core';
import { Observable, catchError, switchMap, throwError } from 'rxjs';
import { McpAuthorizationStart, McpCompleteRequest, McpCompletion } from '../models/assistant-admin.models';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NhAssistantApiError, nhAssistantErrorMessageKey, nhAssistantIsSafeCode } from './nh-assistant-transport';

/** The `type` of the message the assistant's OAuth callback page posts to its opener. */
export const NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE = 'nh-assistant-mcp-authorization';

/**
 * What an authorization window reports: the flow and its one-time completion code, which
 * `POST mcp-connections/complete` turns into the connection with the caller's own session, or the
 * failure code of the callback (for example `assistant-mcp-authorization-failed`).
 */
export type NhAssistantMcpAuthorizationOutcome =
  | { flowId: string; completionCode: string; code?: undefined }
  | { flowId: string | null; code: string; completionCode?: undefined };

/** A window opened for one authorization. */
export interface NhAssistantMcpAuthorizationHandle {
  /** Shows the authorization and emits its outcome once, then completes. */
  authorize(start: McpAuthorizationStart): Observable<NhAssistantMcpAuthorizationOutcome>;
  /** Closes the window, for example when starting the authorization failed. */
  close(): void;
}

/**
 * Opens MCP authorizations in the person's browser. `open()` runs in the click handler, before the
 * authorization URL is known, so popup blockers allow the window.
 */
export interface NhAssistantMcpAuthorizationWindow {
  open(): NhAssistantMcpAuthorizationHandle;
}

/**
 * The authorization window of the assistant components. The default opens a popup and waits for the
 * callback page's message; the testing package replaces it for the mock back-end.
 */
export const NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW = new InjectionToken<NhAssistantMcpAuthorizationWindow>(
  'NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW',
  { providedIn: 'root', factory: () => new NhAssistantMcpPopupAuthorizationWindow(inject(DOCUMENT)) }
);

const safeToken = /^[A-Za-z0-9_-]{16,128}$/;
/** The code of a callback whose own code cannot be shown. */
const authorizationFailed = 'assistant-mcp-authorization-failed';
const flowPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Opens the authorization in a popup. Only a message from that popup, sent by the assistant's
 * callback page (`callbackOrigin`), with the started flow is accepted; the completion code is useless
 * without the starter's own session. A closed popup
 * without a message reports `assistant-mcp-authorization-cancelled`, a blocked one
 * `assistant-mcp-popup-blocked`.
 */
export class NhAssistantMcpPopupAuthorizationWindow implements NhAssistantMcpAuthorizationWindow {
  constructor(private readonly document: Document) {}

  open(): NhAssistantMcpAuthorizationHandle {
    const view = this.document.defaultView;
    // The callback page needs window.opener, so the popup cannot be opened with noopener.
    const popup = view?.open('about:blank', '_blank', 'popup=yes,width=560,height=720') ?? null;
    return new PopupHandle(view, popup);
  }
}

class PopupHandle implements NhAssistantMcpAuthorizationHandle {
  constructor(
    private readonly view: Window | null,
    private readonly popup: Window | null
  ) {}

  authorize(start: McpAuthorizationStart): Observable<NhAssistantMcpAuthorizationOutcome> {
    return new Observable<NhAssistantMcpAuthorizationOutcome>(subscriber => {
      const view = this.view;
      const popup = this.popup;
      if (!view || !popup || popup.closed) {
        subscriber.next({ flowId: start.flowId, code: NhAssistantClientErrorCodes.mcpPopupBlocked });
        subscriber.complete();
        return undefined;
      }

      let finished = false;
      const finish = (outcome: NhAssistantMcpAuthorizationOutcome): void => {
        if (finished) {
          return;
        }
        finished = true;
        subscriber.next(outcome);
        subscriber.complete();
      };
      const onMessage = (event: MessageEvent): void => {
        if (event.source !== popup || event.origin !== start.callbackOrigin) {
          return;
        }
        const outcome = readMessage(event.data, start.flowId);
        if (outcome) {
          finish(outcome);
        }
      };
      view.addEventListener('message', onMessage);
      const closedCheck = view.setInterval(() => {
        if (popup.closed) {
          // The message is dispatched before the page closes itself; a later check finds nothing.
          finish({ flowId: start.flowId, code: NhAssistantClientErrorCodes.mcpAuthorizationCancelled });
        }
      }, 500);
      popup.location.replace(start.authorizationUrl);

      return () => {
        view.removeEventListener('message', onMessage);
        view.clearInterval(closedCheck);
      };
    });
  }

  close(): void {
    if (this.popup && !this.popup.closed) {
      this.popup.close();
    }
  }
}

/**
 * The outcome in a callback message for the started flow, or null for any other message. The message
 * is `{ type, flowId, completionCode, code, messageKey }`; the text of a failure comes from its code,
 * so the message key the page sends along is not needed.
 */
function readMessage(data: unknown, flowId: string): NhAssistantMcpAuthorizationOutcome | null {
  if (!data || typeof data !== 'object') {
    return null;
  }
  const message = data as Record<string, unknown>;
  if (message['type'] !== NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE) {
    return null;
  }
  const messageFlow = message['flowId'];
  if (messageFlow !== null && messageFlow !== flowId) {
    return null;
  }
  const completionCode = message['completionCode'];
  if (messageFlow === flowId && typeof completionCode === 'string' && safeToken.test(completionCode)) {
    return { flowId, completionCode };
  }
  const code = message['code'];
  return { flowId, code: nhAssistantIsSafeCode(code) ? code : authorizationFailed };
}

/**
 * Reads the outcome of a redirect-mode authorization from a URL fragment
 * (`#nh-mcp-flow=…&nh-mcp-code=…` or `#nh-mcp-error=…&nh-mcp-message-key=…`); null when the fragment
 * has none. The text of a failure comes from its code. Remove the fragment from the address bar after
 * reading it.
 */
export function nhAssistantMcpAuthorizationFromFragment(fragment: string): NhAssistantMcpAuthorizationOutcome | null {
  const values = new URLSearchParams(fragment.startsWith('#') ? fragment.slice(1) : fragment);
  const flowId = values.get('nh-mcp-flow');
  const completionCode = values.get('nh-mcp-code');
  const error = values.get('nh-mcp-error');
  if (flowId && flowPattern.test(flowId) && completionCode && safeToken.test(completionCode)) {
    return { flowId, completionCode };
  }
  if (error) {
    return { flowId: flowId && flowPattern.test(flowId) ? flowId : null, code: nhAssistantIsSafeCode(error) ? error : authorizationFailed };
  }
  return null;
}

/**
 * Runs one authorization: starts it, shows it in the window opened before, and completes it with
 * the caller's own session. Errors with `NhAssistantApiError`, also for the window's failure codes.
 */
export function nhAssistantConnectMcp(
  handle: NhAssistantMcpAuthorizationHandle,
  start: Observable<McpAuthorizationStart>,
  complete: (request: McpCompleteRequest) => Observable<McpCompletion>
): Observable<McpCompletion> {
  return start.pipe(
    catchError((error: unknown) => {
      handle.close();
      return throwError(() => error);
    }),
    switchMap(started => handle.authorize(started)),
    switchMap(outcome => outcome.completionCode !== undefined
      ? complete({ flowId: outcome.flowId, completionCode: outcome.completionCode })
      : throwError(() => new NhAssistantApiError(0, outcome.code, nhAssistantErrorMessageKey(outcome.code)))));
}
