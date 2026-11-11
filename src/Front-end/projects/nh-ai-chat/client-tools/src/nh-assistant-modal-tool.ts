import { EventEmitter, Type, inject } from '@angular/core';
import { NhModalComponentRef, NhModalOptions, NhModalService } from '@newheap/platform-common';
import {
  NhAssistantClientToolHandler,
  NhAssistantClientToolInvocation,
  NhAssistantClientToolResult
} from '@newheap/platform-ai-chat';

/**
 * The part of a mutate modal's content the modal tool uses: the `created` and `updated` outputs and
 * `closeDialog` of `NhModalMutateBaseComponent`.
 */
export interface NhAssistantModalToolContent<TResult> {
  created: EventEmitter<TResult>;
  updated: EventEmitter<TResult>;
  closeDialog(event?: unknown): void;
}

/** Options of `nhAssistantModalTool`. */
export interface NhAssistantModalToolOptions<TArguments, TResult> {
  /** The id of the client tool, as the application declared it on the server. */
  toolId: string;
  /**
   * The modal's content: a component that extends `NhModalMutateBaseComponent`, which the person reviews
   * and saves through the application's normal API and authorization.
   */
  component: Type<NhAssistantModalToolContent<TResult>>;
  /** The modal's options, such as its title and `modalClasses: 'large'`. */
  options?: (args: TArguments) => Partial<NhModalOptions>;
  /**
   * The inputs that prefill the form, built from values the application checked; the content reads them
   * in `onNewFormData`. Runs in the assistant's injection context.
   */
  inputs?: (args: TArguments) => Record<string, unknown>;
  /**
   * The id of the saved record that the model may read, when the application chooses to return one;
   * without it the model only learns whether the person saved, cancelled or closed the form.
   */
  resultId?: (saved: TResult) => string | null | undefined;
}

/**
 * A client tool that opens the application's own mutate modal, prefilled, so the person reviews and saves
 * the change themselves: nothing changes until they do, and the save goes through the application's API
 * with the person's rights. It reports `completed` with outcome `saved` once the content emitted
 * `created` or `updated` (with the id `resultId` returns, if any), `cancelled` when the person left the
 * form with its own close, such as Cancel, Escape or the browser's back button (`closeDialog`), and
 * `closed` when the modal closed any other way, such as its close button or the page that held it.
 */
export function nhAssistantModalTool<TArguments = Record<string, unknown>, TResult = unknown>(
  options: NhAssistantModalToolOptions<TArguments, TResult>
): NhAssistantClientToolHandler {
  return {
    toolId: options.toolId,
    run: (invocation: NhAssistantClientToolInvocation) => {
      // Everything that injects runs in the assistant's injection context, before the modal waits.
      const modalService = inject(NhModalService);
      const args = invocation.arguments as TArguments;
      const modal = modalService.open(
        options.component as Type<never>,
        new NhModalOptions(options.options?.(args) ?? {}),
        options.inputs?.(args) ?? {}) as NhModalComponentRef<NhAssistantModalToolContent<TResult>>;
      return waitForModal(modal, options);
    }
  };
}

function waitForModal<TArguments, TResult>(
  modal: NhModalComponentRef<NhAssistantModalToolContent<TResult>>,
  options: NhAssistantModalToolOptions<TArguments, TResult>
): Promise<NhAssistantClientToolResult> {
  return new Promise<NhAssistantClientToolResult>(resolve => {
    const content = modal.contentComponent;
    if (!content) {
      modal.close();
      resolve({ status: 'failed' });
      return;
    }

    let result: NhAssistantClientToolResult | null = null;
    const saved = (record: TResult) => {
      result ??= { status: 'completed', outcome: 'saved', id: options.resultId?.(record) ?? null };
      // The content finishes its submit before the modal closes, as a host that opens it would.
      setTimeout(() => modal.close());
    };
    const subscriptions = [content.created.subscribe(saved), content.updated.subscribe(saved)];

    // The content's own close (Cancel, Escape, back navigation) leaves the form unsaved.
    const closeDialog = content.closeDialog.bind(content);
    content.closeDialog = (event?: unknown) => {
      result ??= { status: 'completed', outcome: 'cancelled' };
      closeDialog(event);
    };

    // Destroying the modal ends every way it closes, also when the page that held it went away.
    modal.componentRef.onDestroy(() => {
      subscriptions.forEach(subscription => subscription.unsubscribe());
      resolve(result ?? { status: 'completed', outcome: 'closed' });
    });
  });
}
