import { DestroyRef, inject } from '@angular/core';
import { debounceTime, filter, merge } from 'rxjs';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { NhAssistantLiveService } from '../services/nh-assistant-live.service';

/** How long a view waits for more agent-work updates before it reloads once. */
export const NH_ASSISTANT_AGENT_WORK_RELOAD_DELAY_MS = 300;

/**
 * Watches the work of agents for the whole application while the calling component lives, so their
 * proposals and runs arrive as live agent-work updates although the whole application lists no
 * members (see `NhAssistantLiveService.watchAgentWork`). Call it in an injection context, for example
 * a component constructor, of a screen that shows that work, such as the approval inbox or a board.
 */
export function nhAssistantWatchAgentWork(): void {
  const live = inject(NhAssistantLiveService, { optional: true });
  if (!live || typeof live.watchAgentWork !== 'function') {
    return;
  }

  const release = live.watchAgentWork();
  inject(DestroyRef).onDestroy(() => release());
}

/**
 * Calls `reload` once a burst of live agent-work updates that `matches` has settled, and after
 * every (re)connect of the hub, because updates sent while it was disconnected are lost. Without a
 * live service or a connected hub nothing arrives and the view keeps its manual refresh. Call it in
 * an injection context, for example a component constructor; it stops when that context ends.
 */
export function nhAssistantReloadOnAgentWork(
  matches: (change: LiveAgentWorkChanged) => boolean,
  reload: () => void
): void {
  const live = inject(NhAssistantLiveService, { optional: true });
  if (!live) {
    return;
  }

  const subscription = merge(live.agentWork$.pipe(filter(matches)), live.resync$)
    .pipe(debounceTime(NH_ASSISTANT_AGENT_WORK_RELOAD_DELAY_MS))
    .subscribe(() => reload());
  inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
}
