import { firstValueFrom, of, throwError } from 'rxjs';
import { McpAuthorizationStart } from '../models/assistant-admin.models';
import {
  NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE,
  NhAssistantMcpAuthorizationHandle,
  NhAssistantMcpPopupAuthorizationWindow,
  nhAssistantConnectMcp,
  nhAssistantMcpAuthorizationFromFragment
} from './nh-assistant-mcp-authorization';
import { NhAssistantApiError } from './nh-assistant-transport';

const start: McpAuthorizationStart = {
  flowId: '0f8fad5b-d9cb-469f-a165-70867728950e',
  authorizationUrl: 'https://auth.example.com/authorize?state=x',
  expiresAt: '2026-10-20T10:10:00Z',
  callbackOrigin: 'https://api.example.com'
};
const completionCode = 'abcdefghijklmnopqrstuvwxyz012345';

/** A popup and its opener, enough for the window's message and closed checks. */
class FakeWindows {
  readonly popup = { closed: false, location: { replace: jasmine.createSpy('replace') }, close: jasmine.createSpy('close') };
  private readonly listeners: ((event: MessageEvent) => void)[] = [];
  private interval: (() => void) | null = null;
  readonly view = {
    open: jasmine.createSpy('open').and.callFake(() => this.blocked ? null : this.popup),
    addEventListener: (_: string, listener: (event: MessageEvent) => void) => this.listeners.push(listener),
    removeEventListener: (_: string, listener: (event: MessageEvent) => void) => this.listeners.splice(this.listeners.indexOf(listener), 1),
    setInterval: (callback: () => void) => {
      this.interval = callback;
      return 1;
    },
    clearInterval: () => {
      this.interval = null;
    }
  };
  blocked = false;

  get listening(): number {
    return this.listeners.length;
  }

  post(data: unknown, source: unknown = this.popup, origin = 'https://api.example.com'): void {
    for (const listener of [...this.listeners]) {
      listener({ data, source, origin } as unknown as MessageEvent);
    }
  }

  tick(): void {
    this.interval?.();
  }

  document(): Document {
    return { defaultView: this.view } as unknown as Document;
  }
}

describe('NhAssistantMcpPopupAuthorizationWindow', () => {
  it('opens the popup before the URL is known and accepts only the started flow\'s message from it', async () => {
    const windows = new FakeWindows();
    const handle = new NhAssistantMcpPopupAuthorizationWindow(windows.document()).open();
    expect(windows.view.open).toHaveBeenCalledWith('about:blank', '_blank', jasmine.stringContaining('popup'));

    const outcome = firstValueFrom(handle.authorize(start));
    expect(windows.popup.location.replace).toHaveBeenCalledWith(start.authorizationUrl);
    windows.post({ type: NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE, flowId: start.flowId, completionCode }, {});
    // The authorization server's page in the same popup cannot answer for the callback.
    windows.post({ type: NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE, flowId: start.flowId, completionCode }, windows.popup, 'https://auth.example.com');
    windows.post({ type: 'other', flowId: start.flowId, completionCode });
    windows.post({ type: NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE, flowId: 'another-flow', completionCode });
    windows.post({ type: NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE, flowId: start.flowId, completionCode });

    expect(await outcome).toEqual({ flowId: start.flowId, completionCode });
    expect(windows.listening).toBe(0);
  });

  it('reports the callback\'s failure code, a closed popup and a blocked one', async () => {
    const failed = new FakeWindows();
    const failedOutcome = firstValueFrom(new NhAssistantMcpPopupAuthorizationWindow(failed.document()).open().authorize(start));
    failed.post({ type: NH_ASSISTANT_MCP_AUTHORIZATION_MESSAGE, flowId: null, completionCode: null, code: 'assistant-mcp-authorization-expired' });

    const closed = new FakeWindows();
    const closedOutcome = firstValueFrom(new NhAssistantMcpPopupAuthorizationWindow(closed.document()).open().authorize(start));
    closed.popup.closed = true;
    closed.tick();

    const blocked = new FakeWindows();
    blocked.blocked = true;

    expect(await failedOutcome).toEqual({ flowId: start.flowId, code: 'assistant-mcp-authorization-expired' });
    expect(await closedOutcome).toEqual({ flowId: start.flowId, code: 'assistant-mcp-authorization-cancelled' });
    expect(await firstValueFrom(new NhAssistantMcpPopupAuthorizationWindow(blocked.document()).open().authorize(start)))
      .toEqual({ flowId: start.flowId, code: 'assistant-mcp-popup-blocked' });
  });
});

describe('nhAssistantMcpAuthorizationFromFragment', () => {
  it('reads the flow and completion code or the error of a redirect-mode return', () => {
    expect(nhAssistantMcpAuthorizationFromFragment(`#nh-mcp-flow=${start.flowId}&nh-mcp-code=${completionCode}`))
      .toEqual({ flowId: start.flowId, completionCode });
    expect(nhAssistantMcpAuthorizationFromFragment('#nh-mcp-error=assistant-mcp-authorization-failed'))
      .toEqual({ flowId: null, code: 'assistant-mcp-authorization-failed' });
    expect(nhAssistantMcpAuthorizationFromFragment('#nh-mcp-error=<script>')).toEqual({ flowId: null, code: 'assistant-mcp-authorization-failed' });
    expect(nhAssistantMcpAuthorizationFromFragment('#nh-mcp-flow=not-a-flow&nh-mcp-code=short')).toBeNull();
    expect(nhAssistantMcpAuthorizationFromFragment('#section-2')).toBeNull();
  });
});

describe('nhAssistantConnectMcp', () => {
  function handle(outcome: { flowId: string; completionCode: string } | { flowId: string; code: string }): NhAssistantMcpAuthorizationHandle & { close: jasmine.Spy } {
    return { authorize: () => of(outcome), close: jasmine.createSpy('close') };
  }

  it('completes with the code the window returned and turns window failures into API errors', async () => {
    const complete = jasmine.createSpy('complete').and.returnValue(of({ serverId: 'crm', ownerKind: 'organization', status: 'active', grantedScopes: null }));

    const completed = await firstValueFrom(nhAssistantConnectMcp(handle({ flowId: start.flowId, completionCode }), of(start), complete));
    const denied = await firstValueFrom(nhAssistantConnectMcp(handle({ flowId: start.flowId, code: 'assistant-mcp-authorization-failed' }), of(start), complete))
      .then(() => null, (error: unknown) => error);

    expect(completed.status).toBe('active');
    expect(complete).toHaveBeenCalledOnceWith({ flowId: start.flowId, completionCode });
    expect(denied).toEqual(jasmine.any(NhAssistantApiError));
    expect((denied as NhAssistantApiError).code).toBe('assistant-mcp-authorization-failed');
  });

  it('closes the window when the authorization could not start', async () => {
    const window = handle({ flowId: start.flowId, completionCode });
    const failure = new NhAssistantApiError(409, 'assistant-mcp-authorization-not-confirmed', 'nh-assistant.errors.assistant-mcp-authorization-not-confirmed');

    const error = await firstValueFrom(nhAssistantConnectMcp(window, throwError(() => failure), () => of())).then(() => null, (caught: unknown) => caught);

    expect(error).toBe(failure);
    expect(window.close).toHaveBeenCalled();
  });
});
