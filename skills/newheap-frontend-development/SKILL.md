---
name: newheap-frontend-development
description: Build or review NewHeap consumer Angular pages, collections, filters, lifecycle hooks, modals, forms, dropdowns, interceptors, toast notifications, error handling, root configuration and the assistant panel.
---

# NewHeap frontend development

Read [collection queries](references/frontend-collection-query.md) for filtering, ordering or paging; [lifecycle and modals](references/frontend-lifecycle-modals.md) for pages, hooks or modal content; [root configuration](references/frontend-root-configuration.md) for providers, interceptors or compatibility opt-ins; [toast notifications](references/frontend-toast-notifications.md) for root-composed toast defaults, the toast container and localized feedback; and [logging and observability](references/observability-logging.md) for `NH_ERROR_HANDLERS` and opt-in browser telemetry. Read multiple references only when the task crosses those boundaries.

Use NewHeap fluent collection helpers and the `appOn...` extension points owned by NewHeap base components. Treat awaiting lifecycle work as an ordering decision; explicitly detach only independent work with complete error, cancellation and stale-result handling.

Mutating modal content extends `NhModalMutateBaseComponent`; other dynamic modal content may extend `NhModalComponentImpl`. Use `modalClasses: 'large'` for a large modal.

Register `NhCommonModule.forRoot(...)` once at the application root. GET deduplication and deferred dropdown loading are explicit consumer opt-ins and remain disabled as library defaults.

Keep translations inside the module object with lowercase dash-case keys. Verify the changed surface in both supported color schemes at desktop and narrow mobile widths, including focus, loading, empty, error and overflow states.

## AI assistant panel

Read [assistant UI](references/ai-assistant-ui.md) when hosting the NewHeap assistant in an Angular application: registration with the host's token and access policy, the launcher and panel in the layout, conversations, sharing and notifications, the agent inbox, boards and run results, the administration page behind the admin permission, and tests against the scripted mock API. The assistant's server side belongs to the backend module.
