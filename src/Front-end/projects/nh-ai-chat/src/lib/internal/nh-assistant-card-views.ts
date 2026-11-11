type NhAssistantIdleWindow = {
  requestIdleCallback?: (callback: () => void, options?: { timeout: number }) => number;
};

let prefetched = false;

/**
 * Loads the views of the cards that load on first use (internal): the plan, the request for input of an
 * MCP server and the default result card with its fields, which approval cards share. The thread starts
 * it once the browser is idle after it first rendered, so a card that arrives later shows at once
 * instead of its placeholder, while the application's initial bundle stays without them. A failed load
 * is left to the card, which loads its view again when it shows.
 */
export function nhAssistantPrefetchCardViews(view: NhAssistantIdleWindow | null): void {
  if (prefetched || !view) {
    return;
  }

  prefetched = true;
  const load = () => {
    void Promise.all([
      import('../components/plan/nh-assistant-plan-view.component'),
      import('../components/elicitation-card/nh-assistant-elicitation-card-view.component'),
      import('../components/tool-result/nh-assistant-tool-result-card-view.component')
    ]).catch(() => {
      prefetched = false;
    });
  };

  if (view.requestIdleCallback) {
    view.requestIdleCallback(load, { timeout: 5000 });
  } else {
    setTimeout(load, 500);
  }
}
