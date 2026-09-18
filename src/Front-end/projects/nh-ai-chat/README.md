# @newheap/platform-ai-chat

Angular assistant panel for NewHeap AI chat endpoints with streaming, tool-call and approval cards.

The package talks to the assistant HTTP API of `NewHeap.Platform.AI.Chat.AspNet`
(`MapNewHeapAssistant("/api/assistant")`): it lists agents and conversations,
streams turns as server-sent events, shows tool calls, and pauses mutations on an
approval card until the user approves or rejects them. It has no dependency on
ng-bootstrap or on a specific authentication library; the host supplies the token
and the access decision.

## Installation

The package is public on npmjs.org and installs without a registry token. An optional project `.npmrc` can make the public source explicit:

```text
registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
```

Install the package with its peer dependencies:

```bash
npm install @newheap/platform-ai-chat @angular/cdk @angular/router marked dompurify
```

View available versions on [npmjs.org](https://www.npmjs.com/package/@newheap/platform-ai-chat). For NuGet, npm and AI-plugin installation, see [Consume public packages](../../../../docs/how-to/consume-public-packages.md).

Peer dependencies: Angular `^20.3.28` (`@angular/core`, `@angular/common`),
`@angular/cdk` `^20.2.14`, `@angular/router` `^20.3.28`, `@ngx-translate/core` `^17.0.0`, `marked` `^18.0.0`,
`dompurify` `^3.4.0` and `rxjs` `~7.8.0`.

The panel is a CDK overlay. Load the CDK overlay styles once, for example in
`angular.json`:

```json
"styles": [
  "node_modules/@angular/cdk/overlay-prebuilt.css",
  "src/styles.scss"
]
```

## Providers

Register the assistant once in the root providers:

```typescript
import { ApplicationConfig, Injectable, inject } from '@angular/core';
import { NhAssistantAccessPolicy, provideNhAssistant } from '@newheap/platform-ai-chat';
import { map } from 'rxjs';

@Injectable()
export class AssistantAccessPolicy implements NhAssistantAccessPolicy {
  private readonly auth = inject(AppAuthService);

  canUse() {
    return this.auth.authSubject.pipe(map(() => this.auth.isOnePermissionGranted(['app.assistant.access'])));
  }
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideNhAssistant({
      apiBaseUrl: environment.api.baseUrl + '/assistant',
      getAccessToken: () => inject(AppAuthService).getAuthorization()?.token ?? null,
      accessPolicy: AssistantAccessPolicy,
      defaultAgentId: 'order-assistant',
      adminRoute: '/admin/assistant'
    })
  ]
};
```

| Option | Default | Meaning |
| --- | --- | --- |
| `apiBaseUrl` | required | Base URL of the assistant endpoints. |
| `getAccessToken` | required | Returns the bearer token or `null`. Runs in the assistant's injection context before every request, so it may use `inject(...)`. |
| `accessPolicy` | always allowed | Injectable class whose `canUse()` returns a boolean or an observable. |
| `defaultAgentId` | first agent | Agent selected for new conversations. |
| `translations` | `'bundled'` | `'bundled'` merges the library's `en` and `nl` texts into `TranslateService`; `'host'` leaves all texts to the host. |
| `getPageContext` | none | Returns what the user has open (`{ route, title?, entities? }`). Runs in the assistant's injection context when the panel opens and before every message; see [Page context](#page-context). |
| `adminRoute` | none | Router link of the host's administration page. The panel links to it only when the server reports `canAdminister`. |
| `markdown` | `{ enabled: true }` | Renders assistant text as sanitized Markdown; `false` shows plain text. |

Requests use `fetch` instead of the host's `HttpClient`, because `EventSource`
cannot send an `Authorization` header and streamed responses must bypass response
interceptors. Every request carries `Authorization: Bearer <token>` when a token
exists and `Accept-Language` from `TranslateService`.

## Host integration

Place the launcher in the header and one panel in the layout:

```html
<header>
  <nh-assistant-launcher />
</header>
<router-outlet />
<nh-assistant-panel />
```

The launcher renders nothing while `GET status` reports `enabled: false`, the
endpoint is unavailable, or the access policy denies the user. It shows a badge
when a conversation waits for approval. Projected content replaces its default
icon. Other components open the panel through `NhAssistantPanelService`:

```typescript
inject(NhAssistantPanelService).open(conversationId);
```

The drawer is 420 px wide on the right and fills the screen below 600 px. Escape
closes it and focus returns to the element that opened it.

The composer stays editable while a turn runs or waits for approval. Sending is
temporarily blocked, so Enter preserves the draft; explicit send returns focus to the
editor. Automatic focus restoration happens only when a disappearing composer control
owned focus, never after the user chose another control, and is skipped for coarse
pointer devices to avoid opening a mobile keyboard unexpectedly.

Approval cards use optional trusted server `presentation` for the friendly tool name,
summary and named fields. Without it—including stored events from an older server—they
show a localized generic explanation. Tool ids, scope targets and bounded, redacted
argument previews stay inside collapsed technical details and never become the
authorization basis.

`NhAssistantStore` exposes the state as signals (`enabled`, `agents`,
`conversations`, `activeConversation`, `streaming`, `pendingApproval`, `error` and
more) and the actions `send`, `decide`, `cancel`, `openConversation`,
`startNewConversation` and `selectAgent`. The building blocks
`nh-assistant-thread`, `nh-assistant-composer`, `nh-assistant-conversation-list`,
`nh-assistant-tool-call-card`, `nh-assistant-approval-card` and
`nh-assistant-agent-picker` are exported for custom layouts. All components are
standalone and use `OnPush`.

Assistant text is rendered as Markdown with `marked` and sanitized with DOMPurify
against a small allow-list. Inline HTML in model text is shown as text, images
render as their alt text, `javascript:` and `data:` links lose their target, and
links open in a new tab with `rel="noopener noreferrer"`.

## Page context

With `getPageContext` every message carries what the user has open, so the model can
answer "this project" without asking:

```typescript
getPageContext: () => inject(CurrentPage).value() ?? { route: inject(Router).url, title: inject(Title).getTitle() }
```

```typescript
// On a project page
this.currentPage.set({
  route: `/projects/${project.key}`,
  title: project.name,
  entities: [{ type: 'project', id: project.key, label: `Project ${project.key}` }]
});
```

The library validates and truncates the result: route at most 200 characters, title
and labels at most 120, at most five entities with a dash-case `type` and an `id` of
at most 64 characters. Entities with an invalid type or id are dropped rather than
truncated. An invalid shape, an exception or a rejected promise leaves the context out
of that message; sending continues. A chip above the message box shows what is sent;
its close button leaves the context out of the next message, which then sends
`clientContext: null`. Page context is untrusted data: the server treats entity ids as
search hints and still authorizes every tool call.

## Preferences

The panel header opens `nh-assistant-preferences`. Users choose a style (standard,
straight to the point, personal or detailed), the form of address, the answer length
and optional own instructions of at most 1,000 characters. The component loads and
saves through `NhAssistantApiService.getPreferences()` and `updatePreferences()`.
Preferences steer the style only; the server keeps them below the application
context and the agent instructions and never lets them change approvals or tool
access.

## Administration

The administration page is a separate entry point, so it stays out of the initial
bundle. Route to it lazily behind the host's admin permission and pass the route as
`adminRoute`:

```typescript
{
  path: 'admin/assistant',
  canActivate: [IsAssistantAdministratorGuard],
  loadComponent: () => import('@newheap/platform-ai-chat/admin').then(module => module.NhAssistantAdminComponent)
}
```

`nh-assistant-admin` shows a no-access state unless the server reports
`canAdminister`, and has three tabs:

- **Context**: the application context with its version and hash, the version
  history and a conflict message instead of overwriting a newer version.
- **Agents**: code agents (override, disable, reset) and agents created by
  administrators (edit, disable, delete). The editor chooses tool selectors from the
  tool catalog or as patterns and assigns MCP servers.
- **MCP servers**: server editor, connection test, tool sync and the tool table.
  The secret is write-only: the page shows only whether one is set and offers to
  replace or clear it. Synced tools start disabled and are treated as changes until
  an administrator enables them and chooses the effect; remote annotations such as
  `readOnlyHint` appear as hints only, and a changed remote input schema disables the
  tool and shows a warning.

`NhAssistantAdminApiService` (main entry) is the typed client of the `admin/*`
endpoints. A `409` becomes the code `assistant-version-conflict`.

## Theme

The components define light and dark defaults and follow `prefers-color-scheme`.
Map them onto the host's design tokens with custom properties:

```scss
:root {
  --nh-assistant-font: var(--font-sans);
  --nh-assistant-background: var(--surface);
  --nh-assistant-surface: var(--surface-subtle);
  --nh-assistant-text: var(--ink);
  --nh-assistant-text-muted: var(--muted);
  --nh-assistant-border: var(--line);
  --nh-assistant-accent: var(--primary);
  --nh-assistant-highlight: var(--brand);
  --nh-assistant-focus: var(--accent);
}
```

Further properties: `--nh-assistant-surface-strong`, `-accent-contrast`,
`-user-bubble`, `-user-text`, `-success`, `-success-soft`, `-warning`,
`-warning-soft`, `-danger`, `-danger-soft`, `-shadow`, `-radius`,
`-radius-control`, `-font-mono`, and `--nh-assistant-launcher-size`, `-color`,
`-background`, `-border` and `-badge-ring` for the launcher.

Use the host's icon library through `NH_ASSISTANT_ICONS`; icons without a class
use small built-in outline glyphs:

```typescript
{ provide: NH_ASSISTANT_ICONS, useValue: { assistant: 'ph ph-sparkle', close: 'ph ph-x', send: 'ph ph-paper-plane-right' } }
```

## Translations

All keys live under `nh-assistant.`. With `translations: 'bundled'` the library
merges its `en` and `nl` texts whenever the host loads a language; other
languages receive English. Keys the host defines under `nh-assistant.` win, so a
host adds only what the server decides:

```json
{
  "nh-assistant": {
    "agents": {
      "order-assistant": { "name": "Order assistant", "description": "Answers questions about orders." }
    },
    "errors": { "assistant-budget-exceeded": "The daily assistant budget is used up." }
  }
}
```

Error events are shown through their `messageKey`, then `nh-assistant.errors.<code>`,
then a generic text. Tool result codes use `nh-assistant.result-codes.<code>` and
fall back to the code. With `translations: 'host'`, copy the bundles from
`NH_ASSISTANT_TRANSLATIONS` or from `@newheap/platform-ai-chat/i18n/en.json` and
`nl.json`.

## Mock API

`@newheap/platform-ai-chat/testing` plays a scripted assistant without a back-end,
for tests, demos and front-end work before the API exists:

```typescript
import { provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';

providers: [
  provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null }),
  provideNhAssistantMockApi({
    agents: [{ id: 'demo', version: 1, displayNameKey: 'nh-assistant.agents.demo.name', descriptionKey: 'nh-assistant.agents.demo.description', canMutate: true }],
    turns: [
      {
        match: /hold/i,
        steps: [{
          approval: {
            toolId: 'orders.update-status', displayName: 'Update status', summary: 'Put order 42 on hold',
            argumentsPreview: '{"id":42}', targets: ['Order 42'],
            approved: [{ text: 'Order 42 is on hold.' }], rejected: [{ text: 'Nothing changed.' }]
          }
        }]
      },
      { steps: [{ tool: { toolId: 'orders.list', displayName: 'List orders' } }, { text: 'There are **3** open orders.' }] }
    ]
  })
]
```

Steps are `text`, `tool`, `approval`, `error` and `fail`. The optional `preferences`
and `admin` parts of the scenario seed the preference and administration endpoints:
context, tool catalog, agents, MCP servers with simulated remote tools, known
policies and hosts that may receive the user's token. The mock answers every
endpoint in memory and sends each turn as contract events over a chunked
`text/event-stream` response, so the real client, parser and store run unchanged.
Inject `NhAssistantMockBackend` to switch the simulated feature flag with
`setEnabled(false)`, the admin permission with `setCanAdminister(false)`, to change
the remote tools of a server with `setRemoteTools(...)` or to assert the received
`requests`.

The SampleProjectManagement management portal shows the complete integration
(sample cases SPM-248 and SPM-253).
