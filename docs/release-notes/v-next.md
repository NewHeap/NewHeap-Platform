# v-next

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql, NewHeap.Platform.AI.Chat.AspNet: shared conversations, live updates and Web Push

The owner of a conversation can share it through a revocable invitation link
(`POST/DELETE conversations/{id}/share-link`, `POST conversations/{id}/join`) or invite
colleagues from a host directory (`UseParticipantDirectory<T>()`,
`participant-candidates`, `participants`). Each participant sends turns with their own
permissions and budget; only the person whose turn created a proposal decides it.
`POST conversations/{id}/read` keeps a read position per person. A SignalR hub
(`NhAssistantHub`) pushes conversation changes, read positions and the turn events of
other participants. With VAPID keys (`ConfigurePush`) the server sends Web Push
notifications when a longer turn finishes or someone is invited; each person can turn
them off (`GET/PUT notifications`, `PUT/DELETE notifications/push-subscription`).
`AddMcpContextBinding` sends the assistant context (acting agent, accountable person,
conversation, turn and invocation ids and a bounded conversation snapshot) as `_meta`
to one reviewed MCP tool; `INhAssistantConversationSnapshotProvider` returns the same
snapshot inside a tool call after verifying the call, the turn and the person's access.

| Breaking change | Required action |
|---|---|
| The `Collaboration` migration adds participant, push-subscription and notification-setting tables and new conversation and message columns in the `nhai` schema. Existing conversations need no backfill. | Apply the migration; with `RunMigrations` the host applies it at startup. |
| `AddNewHeapAssistant` registers SignalR and `MapNewHeapAssistant` maps the hub at `NhAssistantOptions.HubPath` (`/hub/assistant`) behind the access policy. | Hosts without NewHeap.Platform.AspNet.Common must read `access_token` from the query string for that path. Use a SignalR backplane with several instances, or set `HubPath` to an empty string to turn live updates off. |
| Invitation tokens are protected with ASP.NET Core Data Protection. | Persist the key ring to storage shared by every instance. |
| Web Push stays off until `NhAssistantPushOptions` has a public key, private key and subject; an incomplete or invalid configuration now fails startup. Delivery only reaches `AllowedEndpointHosts`. | Generate keys with `NhAssistantWebPushKeys.Generate()`, keep the private key secret and allow outbound HTTPS to the push services. |
| A participant's `DELETE conversations/{id}` leaves the conversation; only the owner archives it for everyone. Deciding another person's approval returns 403 `assistant-approval-forbidden`; only the owner and the person whose turn runs can cancel it (403 `assistant-turn-forbidden`). | Map the new error codes in custom clients. |
| No administrator-connected MCP server receives assistant context unless the host binds its server id, URL and tool with `AddMcpContextBinding`. | No action. |
| Conversation, summary, message and status responses gain `role`, `participantCount`, `lastMessageSequence`, `lastReadSequence`, `activeActorId`, `members`, `shareToken`, `currentActorId`, `sequence`, `authorActorId` and `collaboration`. | Accept the additional fields in strict clients. |

## NewHeap.Platform.AI.Mcp: request metadata for reviewed imported tools

An `INhAiMcpInvocationBinder` registered with `AddNewHeapPlatformAIMcpInvocationBinder`
for one server and remote tool adds per-call `_meta` to imported calls whose policy sets
`InvocationBinderId`. It runs inside the shared invoker after authorization, capability,
approval, budget and idempotency checks, counts toward `MaxInputBytes`, and a failed
binding stops the call with an audited result code. `NhAiMcpInvocationIdentity`
separates the acting principal from the accountable person and carries the run and
invocation ids.

| Breaking change | Required action |
|---|---|
| A policy that names an `InvocationBinderId` without a binder registered for its server and tool fails the import. The binder id and version become part of that tool's contract hash, so pending approvals of the tool need a new decision. | Register the binder before importing and deploy it when no approval of the tool is pending. Tools without a binder keep their contract hash. |

## @newheap/platform-ai-chat: parallel and shared conversations, read state and push notifications

The store keeps one session per conversation, so several conversations can run at the
same time. `nh-assistant-activity` shows the other running, waiting and unread
conversations as one compact strip and the launcher badge counts them. The share view
(`nh-assistant-share`) creates invitation links and invites colleagues, the thread
names the author of each message, and the preferences offer a desktop notification
setting that is on by default.

| Breaking change | Required action |
|---|---|
| New peer dependency `@microsoft/signalr` ^10.0.11, loaded only when live updates connect. | Install it. Set `NhAssistantConfig.hubBaseUrl` when a proxy rewrites the API path, and allow the hub URL (including `wss:`) in `connect-src`. |
| `NhAssistantIconName` gains `users`, `copy`, `bell` and `leave`. | Add them to complete `Record<NhAssistantIconName, string>` icon maps. |
| `startNewConversation`, `selectAgent` and `loadConversation` no longer wait for a running turn; `streaming()`, `error()` and `notice()` describe the active conversation. | Use `activity()`, `runningCount()` or `unreadCount()` for other conversations in custom layouts. |
| A conversation counts as read only while it is open in a visible, focused window. `nh-assistant-panel` reports this. | Custom layouts without the panel call `NhAssistantStore.setViewing(true/false)`. |
| Push notifications need the bundled service worker. | Copy `@newheap/platform-ai-chat/push/nh-assistant-push-worker.js` to the host assets and set `NhAssistantConfig.push`; without it the setting shows as unavailable. |
| A participant's delete button in the conversation list leaves the conversation, and a running conversation cannot be deleted. | No action. |
