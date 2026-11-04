# v-next

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: gateway reads say why they fail (FB96)

- A gateway read of a headless agent run that gets no usable delegated read credential fails with its cause as
  the code (`NhAiDelegatedReadFailureCodes`), which the model, the run's tool call, the administration and the
  agent builder show:
  - `ai-delegated-read-target-unknown`: the host authenticated the credential before routing knew its target.
    Call `UseRouting` before `UseAuthentication`; with `UseNewHeapPlatformAspNetCommon`, route controllers with
    `UseEndpointRoutingOnly()`. Under `UseMvc` the bridge's MVC filter authenticates the scheme with the
    executing action as its target (`AuthenticateForTargetAsync`), so no middleware may authenticate it first,
    as it does for the default authentication scheme.
  - `ai-delegated-read-no-issuer`: `AddNewHeapPlatformAIDelegatedReads` is missing, or its built-in issuer runs
    on a host without `INhAuthenticationService`; register an issuer with `UseIssuer<T>()`.
  - `ai-delegated-read-issuer-refused`: the issuer refused the person, failed, or returned a token without
    exactly its binding claims or that lives too long.
  - `ai-delegated-read-rejected`: the host's bearer scheme rejected the token, or the API answered the read
    with `401` for another reason.
- Each cause logs one content-free warning per host that names the fix (events 6 to 9 of
  `NhAiDelegatedReadService`); the warnings of each occurrence (events 1 to 5) stay. The host names an unknown
  target for a call it answered itself; a call another instance answered fails as `ai-delegated-read-rejected`.
- A person who may not lend rights now (no fresh context under `UsePrincipalsFromAccessSource`, or a subject's
  responsible person who changed after the run was queued) still fails with `api-bridge-unauthenticated`.

| Breaking change | Required action |
|---|---|
| A gateway read of a headless run that gets no usable delegated read credential fails with its cause (`NhAiDelegatedReadFailureCodes`) instead of `api-bridge-unauthenticated`; a locked-out or removed person now gets `ai-delegated-read-issuer-refused`. | Where you match `api-bridge-unauthenticated` for gateway reads, also match `NhAiDelegatedReadFailureCodes.All`. |

## @newheap/platform-ai-chat: why a gateway read failed (FB96)

- The causes of a gateway read that got no usable delegated read credential (`ai-delegated-read-target-unknown`,
  `ai-delegated-read-no-issuer`, `ai-delegated-read-issuer-refused` and `ai-delegated-read-rejected`) have a text
  in English and Dutch under `nh-assistant.errors`, so a tool call, a rule script's call and a run say what
  happened and who fixes it.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the agent builder does what administrators can, except secrets (FB94)

- The agent builder (`AddAgentBuilder`) gets governed tools for what an administrator does for an agent in the
  administration's screens, each change its own approval in the chat, through the same services and checks:
  `builder_save_run_settings` now takes whose rights the agent's runs use (`accountability`,
  `serviceAccountActorId`, `delegateFromInitiator`, `allowGatewayReads`, `elevatedReads`) and the file settings;
  `builder_update_agent` takes the required policy, MCP servers, what the agent works on, profile,
  classification, compositions and scripts; `builder_create_agent` also creates a shadow variant (`shadowOf`);
  new are `builder_set_technical_account`, `builder_set_consultation_routes`, `builder_set_mcp_server` (assign
  an existing server), `builder_set_mcp_headless_use`, `builder_save_consultation`, `builder_save_hand_off`,
  `builder_save_metadata`, `builder_set_state`, `builder_set_switch` (tools without approval, the alert model),
  `builder_set_groups`, `builder_promote_shadow_variant` and `builder_remove` (an agent created in the
  administration, an override, run settings, skills, triggers, consultations, hand-offs, metadata, candidates
  and rule script versions).
- Input that is left out keeps what the agent has; nothing is reset that the administrator did not mention.
- A change of whose rights an agent uses and every removal get a card that says so plainly, in English and
  Dutch. The application's four eyes hold as before (a rule script's step live).
- No tool takes or returns a secret: MCP servers are listed without their address, connection, references or
  credentials, and a call that names a property its tool does not take is refused before it becomes a
  proposal. Connecting a server and its credentials stay in the screens. The builder acts with the
  administrator's own rights, so a technical account that would lend more rights than they hold is refused
  (`assistant-service-account-not-permitted`), and its own id, tools, policy and definition stay reserved.
- `builder_search_accounts` finds people and technical accounts as the administration's account search does.
- The builder's instructions are version 2 of `nh-agent-builder-instructions`.

| Breaking change | Required action |
|---|---|
| The agent builder can now change whose rights an agent uses (accountability, technical account, delegation, gateway and elevated reads, required policy, MCP servers and their use without a person, consultation routes) and remove what an administrator can, each after the administrator's approval; `builder_save_run_settings` no longer keeps those settings when the call names them. | Check who passes the assistant's administration policy: they reach this through the builder as they do in the screens. Leave `AddAgentBuilder` out to switch the builder off. |
| The contracts of the builder's tools changed, so an approval of a builder change that waits from before the upgrade no longer matches its tool. | Decide or let expire the builder's waiting approvals before the upgrade; ask the builder again afterwards. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the agent builder knows the application (FB95)

- `builder_read_gateway` searches and describes the resources of the application's API gateway with the
  administrator's own rights (the gateway's own `search-resources` and `describe-resource`): filter fields
  and operators, search and order fields, whether a query can only count, the id of `get` and the result
  fields, bounded in size. Without a gateway it says there is none. The catalog names the gateway's tools and
  the MCP servers an agent may be assigned.
- `builder_read_agent` lists blockers: what stops the agent from working as it should, each with a code, the
  setting and the tool that changes it, or none when only code or configuration does: `not-configured`,
  `gateway-reads-off`, `gateway-reads-need-accountability`, `technical-account-missing`, `alert-kind-not-listed`,
  `no-read-issuer`, `not-active`, `agent-work-off`, `no-trigger`, `in-shadow`, `run-settings-out-of-effect`,
  `configuration-problem`, `instructions-unavailable` and `run-failed` with the code its recent runs failed
  with, such as the causes of a failed gateway read (`NhAiDelegatedReadFailureCodes`). The builder names the
  setting and offers to change it after an approval.
- `NhAssistantAgentBuilderOptions.UseGuidance(NhAiTextAsset)` gives the builder guidance of the application's
  own: a text asset only the builder reads, after the platform's rules and before the application context, in
  a section framed as background that never changes the rules, the builder's instructions, its tools or the
  approvals. It must be trusted system instructions of the application, not blank, at most
  `NhAssistantAgentBuilderOptions.MaxGuidanceLength` (4,000) characters, with the hash of its content; its id
  and version are part of the builder's prompt version.

## @newheap/platform-ai-chat: the agent builder follows its changes (FB94)

- The agent page of `nh-assistant-admin` reads the agent again after every change the builder made after an
  approval, and goes back to the agents when the builder removed the agent it shows.
- The texts of the builder say that it works with the administrator's own rights, says so when a change alters
  whose rights an agent uses or removes something, and never handles secrets, in English and Dutch.