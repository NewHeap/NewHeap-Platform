---
id: nh-ai-assistant-ui
title: "Host the NewHeap assistant panel in an Angular application"
area: frontend
reference: ai-assistant-ui
summary: "Register the assistant once with the host's token and access policy, place the launcher and one panel in the layout, and test against the scripted mock API instead of a live model."
sample-cases: ["SPM-248"]
public-symbols: ["provideNhAssistant", "NhAssistantConfig", "NhAssistantAccessPolicy", "NhAssistantApiService", "NhAssistantStore", "NhAssistantPanelService", "NhAssistantLauncherComponent", "NhAssistantPanelComponent", "NH_ASSISTANT_ICONS", "NH_ASSISTANT_TRANSLATIONS", "provideNhAssistantMockApi", "NhAssistantMockBackend"]
skills: ["newheap-frontend-development"]
providers: ["frontend"]
risk: high
---
## Preferred approach

Install `@newheap/platform-ai-chat` with its peers (`@angular/cdk`,
`@ngx-translate/core`, `marked`, `dompurify`) and load
`@angular/cdk/overlay-prebuilt.css` once in the application styles. Call
`provideNhAssistant(...)` once in the root providers with the assistant base URL,
`getAccessToken` and an `accessPolicy` class. `getAccessToken` runs in the
assistant's injection context before every request, so it can read the token from
the host's existing auth service through `inject(...)`. Base the access policy on
the same permission the API enforces (for example `app.assistant.access`) and
return an observable when the signed-in user can change, so the launcher follows
sign-in and sign-out.

Place `<nh-assistant-launcher />` in the header and exactly one
`<nh-assistant-panel />` in the application layout. Other components open the
panel through `NhAssistantPanelService.open(conversationId?)`. The launcher stays
hidden while the server reports the assistant disabled or the policy denies the
user; do not add a second feature flag in the host.

Keep the bundled `en` and `nl` texts (`translations: 'bundled'`) and add only
host-specific keys under `nh-assistant.`: agent names and descriptions from the
server's `displayNameKey`/`descriptionKey`, and message keys of host-specific
error codes. Theme the panel through the `--nh-assistant-*` custom properties
mapped onto the host's tokens, and pass the host icon library through
`NH_ASSISTANT_ICONS`.

Test hosts and demos with `provideNhAssistantMockApi(script)` from
`@newheap/platform-ai-chat/testing`, registered after `provideNhAssistant` in the
same injector. The mock plays scripted turns as contract events over a real
event stream, including approvals, cancellation and the disabled flag, so the
panel is exercised without a model or assistant back-end. A route may host its
own assistant scope with route-level providers.

## Avoid

- Sending the assistant requests through host `HttpClient` interceptors or
  `EventSource`; the library streams through `fetch` with the token from
  `getAccessToken`.
- Registering `provideNhAssistant` in feature components or placing more than one
  panel in the same injector scope.
- Showing the launcher from a host-side permission check alone; the server status
  and the access policy together decide visibility.
- Rendering model text or previews with `bypassSecurityTrustHtml` or custom
  `innerHTML`; use the thread component, which renders Markdown through a strict
  DOMPurify allow-list and shows inline HTML as text.
- Displaying raw server messages. Show the translated `messageKey` of an `error`
  event and fall back to the code.
- Deciding approvals outside the approval card or without the proposal hash of
  the pending approval.
- Replacing library translations wholesale in the host files; override single
  keys only.

## Verification

Run the library tests headless (`npm run nh-ai-chat:test -- --browsers=ChromeHeadless`):
the SSE parser handles fragmented chunks, keep-alive comments and terminal events;
the store turns a contract event sequence into the expected messages, tool calls
and approvals and sends the proposal hash exactly once; the launcher disappears for
`enabled: false` and a denying policy; model text with a script tag renders no
script; `en` and `nl` have identical keys; and the mock emits exactly the contract
event fields. In the host, open the assistant playground and walk through a new
conversation, a streamed answer, a tool call, approve, reject, stop, an error and
an agent switch at desktop and mobile width in light and dark mode. SPM-248 is the
executable reference.
