import {
  EnvironmentInjector,
  EnvironmentProviders,
  Injectable,
  InjectionToken,
  inject,
  makeEnvironmentProviders,
  runInInjectionContext
} from '@angular/core';
import { ClientToolReportStatus } from '../models/assistant-api.models';

/**
 * One call of a client tool that the browser runs: the server's `client-tool.requested` event of the
 * person's own turn. The arguments were bound to the tool's input on the server, which the application
 * declared there with `AddClientTool`; a handler still treats them as untrusted values from the model and
 * checks them before it uses them, for example before it builds a route.
 */
export interface NhAssistantClientToolInvocation<TArguments = Record<string, unknown>> {
  /** The call; the store reports the handler's result for it. */
  invocationId: string;
  /** The client tool the application declared on the server. */
  toolId: string;
  /** The conversation whose turn waits for the result. */
  conversationId: string;
  /** The arguments, bound to the tool's input on the server. */
  arguments: TArguments;
  /** The end of the wait on the server's clock; a later result is refused. */
  expiresAt: string;
}

/**
 * What a handler reports. `completed` when it ran, with an `outcome` such as `navigated`, `saved`,
 * `cancelled` or `closed`; `declined` when the person did not let it run; `unavailable` when it cannot run
 * these arguments, such as a route the application does not allow; `failed` when it failed. An `id` names
 * what the person created, only when the application chooses to return it; `data` only for a tool that
 * declares its result on the server.
 */
export interface NhAssistantClientToolResult {
  status: ClientToolReportStatus;
  /** A dash-case code of at most 40 characters. */
  outcome?: string | null;
  /** At most 64 letters, digits and `-_.:`. */
  id?: string | null;
  data?: Record<string, unknown> | null;
}

/**
 * Runs one client tool in the application's own interface. The application declares the tool on the
 * server, never here: a browser only says which tool ids it can run, so no description of the browser
 * reaches the model. `run` starts in the assistant's injection context, so it may call `inject(...)`
 * before its first `await`.
 */
export interface NhAssistantClientToolHandler {
  /** The id of the client tool, as the application declared it on the server. */
  readonly toolId: string;
  run(invocation: NhAssistantClientToolInvocation): NhAssistantClientToolResult | Promise<NhAssistantClientToolResult>;
}

/** The client tool handlers of the assistant scope; register them with `provideNhAssistantClientTools`. */
export const NH_ASSISTANT_CLIENT_TOOLS = new InjectionToken<NhAssistantClientToolHandler[]>('NH_ASSISTANT_CLIENT_TOOLS');

/**
 * Registers handlers of client tools for the assistant scope, next to `provideNhAssistant` in the same
 * providers: for example the built-in `nhAssistantNavigateTool` and `nhAssistantModalTool` of
 * `@newheap/platform-ai-chat/client-tools`. The assistant then tells the server with each message which
 * tools this browser can run, and runs a tool's handler when the person's own turn calls it. Calling it
 * more than once adds handlers; a tool id registered twice fails when the assistant starts.
 */
export function provideNhAssistantClientTools(...handlers: NhAssistantClientToolHandler[]): EnvironmentProviders {
  return makeEnvironmentProviders(handlers.map(handler => ({ provide: NH_ASSISTANT_CLIENT_TOOLS, useValue: handler, multi: true })));
}

const dashCase = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const idCharacters = /^[A-Za-z0-9._:-]+$/;
const reportable: readonly ClientToolReportStatus[] = ['completed', 'declined', 'unavailable', 'failed'];

/**
 * The client tool handlers of the assistant scope by tool id. It runs a handler in the assistant's
 * injection context and turns what it returned, or threw, into a result the server accepts: an unknown
 * tool is `unavailable`, a handler that throws `failed`, and an outcome or id out of bounds is left out.
 */
@Injectable()
export class NhAssistantClientTools {
  private readonly injector = inject(EnvironmentInjector);
  private readonly handlers: ReadonlyMap<string, NhAssistantClientToolHandler>;

  constructor() {
    const handlers = new Map<string, NhAssistantClientToolHandler>();
    for (const handler of inject(NH_ASSISTANT_CLIENT_TOOLS, { optional: true }) ?? []) {
      if (handlers.has(handler.toolId)) {
        throw new Error(`The client tool '${handler.toolId}' has more than one handler.`);
      }
      handlers.set(handler.toolId, handler);
    }
    this.handlers = handlers;
  }

  /** The tool ids this browser can run, which the assistant sends with each request that carries a turn. */
  ids(): string[] {
    return [...this.handlers.keys()];
  }

  /** Whether this browser has a handler for `toolId`. */
  has(toolId: string): boolean {
    return this.handlers.has(toolId);
  }

  /** Runs the handler of the invocation's tool and returns a result the server accepts. */
  async run(invocation: NhAssistantClientToolInvocation): Promise<NhAssistantClientToolResult> {
    const handler = this.handlers.get(invocation.toolId);
    if (!handler) {
      return { status: 'unavailable', outcome: 'no-handler' };
    }

    try {
      const result = await runInInjectionContext(this.injector, () => handler.run(invocation));
      return nhAssistantBoundClientToolResult(result);
    } catch {
      // The person's app failed; the model reads that, never the exception.
      return { status: 'failed' };
    }
  }
}

/**
 * A handler's result within the bounds the server accepts: a known status, a dash-case outcome of at most
 * 40 characters and an id of at most 64 letters, digits and `-_.:`; anything else is left out.
 */
export function nhAssistantBoundClientToolResult(result: NhAssistantClientToolResult | null | undefined): NhAssistantClientToolResult {
  if (!result || !reportable.includes(result.status)) {
    return { status: 'failed' };
  }

  const bounded: NhAssistantClientToolResult = { status: result.status };
  if (typeof result.outcome === 'string' && result.outcome.length <= 40 && dashCase.test(result.outcome)) {
    bounded.outcome = result.outcome;
  }
  if (typeof result.id === 'string' && result.id.length <= 64 && idCharacters.test(result.id)) {
    bounded.id = result.id;
  }
  if (result.data && typeof result.data === 'object' && !Array.isArray(result.data)) {
    bounded.data = result.data;
  }
  return bounded;
}
