import { inject } from '@angular/core';
import { Router, UrlTree } from '@angular/router';
import {
  NhAssistantClientToolHandler,
  NhAssistantClientToolInvocation,
  NhAssistantClientToolResult
} from '@newheap/platform-ai-chat';

/** Options of `nhAssistantNavigateTool`. */
export interface NhAssistantNavigateToolOptions<TArguments> {
  /** The id of the client tool, as the application declared it on the server. */
  toolId: string;
  /**
   * The application's allow-list: the route of these arguments as router commands or a `UrlTree`, built
   * from values the application checked, or `null` when it does not allow them. The model never names a
   * URL. Runs in the assistant's injection context, so it may call `inject(Router).createUrlTree(...)`.
   */
  route: (args: TArguments) => readonly unknown[] | UrlTree | null | undefined;
}

/**
 * A client tool that navigates to a route of the application that `route` allows. It reports
 * `completed` with outcome `navigated`, `declined` with outcome `navigation-cancelled` when a guard
 * kept the page, for example one that protects unsaved changes, and `unavailable` with outcome
 * `route-not-allowed` when `route` refused the arguments.
 */
export function nhAssistantNavigateTool<TArguments = Record<string, unknown>>(
  options: NhAssistantNavigateToolOptions<TArguments>
): NhAssistantClientToolHandler {
  return {
    toolId: options.toolId,
    run: (invocation: NhAssistantClientToolInvocation) => {
      // Both run in the assistant's injection context, before the first await.
      const router = inject(Router);
      const target = options.route(invocation.arguments as TArguments);
      if (!target) {
        return { status: 'unavailable', outcome: 'route-not-allowed' };
      }

      const tree = target instanceof UrlTree ? target : router.createUrlTree([...target]);
      return navigate(router, tree);
    }
  };
}

async function navigate(router: Router, tree: UrlTree): Promise<NhAssistantClientToolResult> {
  // Only a cancelled navigation resolves false; the page that is already open counts as navigated.
  const navigated = await router.navigateByUrl(tree);
  return navigated === false
    ? { status: 'declined', outcome: 'navigation-cancelled' }
    : { status: 'completed', outcome: 'navigated' };
}
