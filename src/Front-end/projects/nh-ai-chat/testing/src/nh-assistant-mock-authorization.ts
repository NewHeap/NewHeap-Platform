import {
  McpAuthorizationStart,
  NhAssistantMcpAuthorizationHandle,
  NhAssistantMcpAuthorizationOutcome,
  NhAssistantMcpAuthorizationWindow
} from '@newheap/platform-ai-chat';
import { Observable } from 'rxjs';

/**
 * The authorization window of the mock API: instead of a popup at an authorization server it plays
 * the callback of the started flow right away, as the scenario's `admin.mcpAuthorization` says. It
 * records the authorization URLs it was asked to show.
 */
export class NhAssistantMockAuthorizationWindow implements NhAssistantMcpAuthorizationWindow {
  /** The authorization URLs shown, oldest first. */
  readonly shown: string[] = [];
  /** How many windows were closed without finishing, for example because starting failed. */
  closed = 0;

  constructor(private readonly callback: (flowId: string) => NhAssistantMcpAuthorizationOutcome) {}

  open(): NhAssistantMcpAuthorizationHandle {
    return {
      authorize: (start: McpAuthorizationStart) => new Observable<NhAssistantMcpAuthorizationOutcome>(subscriber => {
        this.shown.push(start.authorizationUrl);
        const timer = setTimeout(() => {
          subscriber.next(this.callback(start.flowId));
          subscriber.complete();
        });
        return () => clearTimeout(timer);
      }),
      close: () => {
        this.closed++;
      }
    };
  }
}
