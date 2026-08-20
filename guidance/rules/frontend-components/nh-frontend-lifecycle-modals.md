---
id: nh-frontend-lifecycle-modals
title: "NewHeap lifecycle, pages, and modals"
area: frontend
reference: frontend-lifecycle-modals
summary: "Use the appOn lifecycle of NewHeap base components so routing, metadata, request state, modal behavior, and cleanup remain intact."
sample-cases: ["SPM-114", "SPM-115", "SPM-116", "SPM-117", "SPM-118", "SPM-119", "SPM-120", "SPM-129"]
public-symbols: ["NhPageTypeBaseComponent", "NhCollectionTypeBaseComponent", "NhModalMutateBaseComponent", "NhModalComponentImpl", "NhModalService"]
skills: ["newheap-frontend-development"]
providers: ["frontend"]
risk: critical
---
## Preferred approach

When extending NewHeap base components, override the corresponding `appOnChanges`, `appOnInit`, `appAfterContentInit`, `appAfterViewInit`, and `appOnDestroy` extension points. Use `appOnInitAndLoad` for work that must run again when active route parameters change, and use `appOnInitAndLoadWithSkipBrowserInitial` only when the hydration pass must deliberately be skipped. Await `super.appOn...()` when an intermediate base has meaningful behavior.

Treat every `await` in an `appOn...` hook as an ordering decision, not as a formatting preference:

- Await a task immediately when later initialization or lifecycle behavior depends on its result.
- When independent synchronous setup should overlap the request but the hook must still wait for completion, start the task, perform that setup, and await the captured promise at the end of the hook.
- Deliberately detach a task only when later lifecycle work does not depend on it, repeated hook invocations are safe, and cancellation or stale results are handled. Make the intent explicit with `void task().catch(handleError)` or let the invoked method contain its own complete error handling. A bare `.then()` is legacy shorthand for the same non-blocking intent; preserve its scheduling semantics when modernizing it.

```ts
override appOnInitAndLoad(): Promise<void> {
  // The request may continue while metadata and the remaining lifecycle advance.
  void this.loadProjectSummary().catch(error => this.handleProjectSummaryLoadError(error));
  this.configurePageMetadata();
  return Promise.resolve();
}
```

Do not mechanically replace a deliberately unawaited call with `await`. Awaiting the promise returned by the hook delays the NewHeap metadata flush and the next lifecycle step until that task settles.

Mutating modal content extends `NhModalMutateBaseComponent`; other dynamic content may extend `NhModalComponentImpl`. `NhModalComponent` is the service-owned shell. Pass `modalClasses: 'large'` to the modal service for a larger modal.

## Avoid

- Overriding Angular `ngOn...` or `ngAfter...` methods when the NewHeap base owns that hook.
- Leaving cleanup or subscriptions outside `appOnDestroy`.
- Adding `await` to a deliberately detached task without establishing a real ordering dependency.
- Detaching a task that can reject without observing the error, or that can write stale state after a later route load.
- Using the modal shell as the consumer content base.
- Solving route changes with full page reloads that rebuild the app shell and SignalR connection.

## Verification

Test initial navigation, parameter changes inside the same shell, modal open, save, and cancel behavior, cleanup, and browser back and forward. Check console errors, subscription leaks, and that the initial collection load runs exactly once. For detached work, prove that the hook can complete before the task while failures are still observed; for awaited work, prove the dependent step cannot run early.
