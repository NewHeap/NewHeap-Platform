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
- `builder_save_run_settings` takes `alertKinds` (FB93): the kinds in code that an agent created in the
  administration lists for its rule scripts and model; left out keeps the stored list, an empty list lists
  none, and the card names them. `builder_read_agent` shows the listed kinds.
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

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the agent builder's cards say what runs

- The input of every builder tool is checked against the tool's schema before it becomes a proposal, and the
  card and the change read the same checked input: a number given as a string, a string given as yes or no,
  `null` for a property that does not allow it, an empty text, a value outside its choices or bounds, and a
  property the tool does not take are refused with `ai-tool-input-invalid`, naming the property, and nothing
  is proposed. A blank text is refused also where `null` clears, and `null` clears a value only where the
  schema allows it (for example a retry count, a budget, a profile, a policy or a skill's instructions); the
  card then says the default applies. Two forms models often send are read as meant, for the card and the
  change alike: `null` for an optional property that takes no `null` is left out, so it keeps what the agent
  has, and a whole number written as `1.0` is that number. A nullable choice is published as the `anyOf` of the
  choice and `null`, as the generated tools publish nullable values, without `null` in its enum.
- A builder write is proposed only with the builder's own valid card, which shows every value of whose rights
  it changes, what it removes and what the agent may do: the card is built for the proposal, after the
  invoker's gate, in every UI culture of the application. When it cannot be built within the presentation's
  limits, or the builder's presenter fails or takes longer than 10 seconds, nothing is proposed: the call reads
  `not-executed` with `ai-tool-input-invalid` and a message that says what to shorten or split, and no other
  presenter's card or the generic summary stands in, also not for a proposal made through
  `INhAssistantProposals`. An approved write runs only while its card can still be built.
- The values of whose rights (such as the required policy, MCP servers, run and view policies, accountability
  and technical account, consultation routes and the parts a removal names) have fields of their own, first
  and in full. What an agent, skill or consultation may do (tools and tool selectors, autonomy, compositions,
  scripts, what a consultation offers, a rule script's writes) follows and is never shortened either; a list
  that one field does not hold spans more, as "Tools (1 of 2)". The other values follow, sharing fields when
  needed, each shortened as far as the card needs to keep within its limits also as stored JSON with its
  escapes; the card then says the technical details show them in full. A metadata value shows as the JSON it
  is saved as, and creating an agent with a required, run or view policy gets the rights notice.
- `builder_save_skill`, `builder_save_trigger`, `builder_save_consultation` and `builder_save_hand_off` keep what
  a change leaves out, as the card says (a trigger while its source stays the same); a description is needed
  only for a new skill or consultation, and the sources only for a new hand-off.
- The card says that a change alters whose rights an agent uses also for who may run it, who sees its results
  for the whole application, and a required policy that is cleared; another accountability without a
  technical account named shows that the technical account is removed. `builder_promote_shadow_variant` needs
  the variant's version (`expectedVariantVersion`), so a variant changed after its card is refused; its card
  names the live agent and what it takes over, the settings of whose rights first: the required policy, run
  and view policies, autonomy, what it works on, model profile, classification, compositions and scripts for an
  agent created in the administration, then the tools in fields of their own and, with `moveSkills`, the
  skills that go live. It no longer takes `asPackageVersion` or `uncarriedParts`: a variant that becomes its
  agent package's next version carries its run settings, technical account, skills, triggers, consultations,
  hand-offs and rule scripts as well, so it is promoted in the administration's screens.
- `builder_set_mcp_headless_use` changes only whether the server serves runs without a person, checked as the
  server editor checks that field and written only while the server's authentication mode and connection
  scope are still those it was checked against; what another administrator changed between the card and the
  approval, the connection and the secret stay.
- The builder's policy checks the administration policy without a resource, as the administration's endpoints
  do, so a policy that decides by a resource lets a person use the builder only where the screens let them in.
- The administration refuses to assign an MCP server to the builder (`PUT admin/mcp-servers/{id}/agents/nh-agent-builder`
  answers `400` with `agentId: not-allowed`), and the builder's turns never load MCP servers, also with an
  assignment stored before.

| Breaking change | Required action |
|---|---|
| The builder's tools refuse input that does not fit their schema, or whose card cannot show it in full, instead of reading it leniently or proposing it with the generic summary; `builder_save_skill`, `builder_save_trigger`, `builder_save_consultation` and `builder_save_hand_off` keep what a change leaves out instead of clearing it; `builder_promote_shadow_variant` requires `expectedVariantVersion` and no longer takes `asPackageVersion` or `uncarriedParts`. | None: the builder corrects a refused call itself. Promote a variant as its package's next version in the administration's screens. Decide or let expire the builder's waiting approvals before the upgrade. |
| An MCP server assigned to the agent builder is no longer offered to its turns, and the administration refuses such an assignment. | Remove an existing assignment with `DELETE admin/mcp-servers/{id}/agents/nh-agent-builder` if you want it gone from the list. |

## @newheap/platform-ai-chat: the agent builder follows its changes (FB94)

- The agent page of `nh-assistant-admin` reads the agent again after every change the builder made after an
  approval, and goes back to the agents when the builder removed the agent it shows.
- The texts of the builder say that it works with the administrator's own rights, says so when a change alters
  whose rights an agent uses or removes something, and never handles secrets, in English and Dutch.

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql, NewHeap.Platform.AI.Chat.AspNet and NewHeap.Platform.AI.Scripting: alert kinds of agents created in the administration (FB93)

- The run settings of an agent created in the administration list the alert kinds it may raise, out of every kind
  the code registers with `AddAlertKind`: `NhAssistantAgentRunSettingsRequest.AlertKinds` of
  `PUT admin/agents/{id}/run-settings` and `INhAssistantAgentConfiguration.SaveRunSettingsAsync`, shown as
  `NhAssistantAgentRunSettingsDto.AlertKinds`. A save refuses a malformed id or more than
  `NhAssistantAgentConfigurationLimits.MaxAlertKinds` (20) kinds with `alertKinds: invalid` and a kind the code does
  not register with `alertKinds: not-found`; `null` keeps the stored list and an empty list lists none.
- The listed kinds are the agent's `NhAssistantAgentRunOptions.AlertKinds`, so its rule results, `ResolveWhenNotRaised`,
  rule scripts and the model tool `assistant.alert.raise` work as for an agent in code, in shadow as shadow alerts.
  The kind in code stays the ceiling: its texts, recipients and delivery. A listed kind a later deploy removes leaves
  the run settings in effect without it, with the run configuration problem of kind `alert-kind`
  (`NhAssistantRunConfigurationProblems.AlertKindKind`, `UnknownAlertKind`), and its alerts are refused.
- `GET admin/run-catalog` lists every registered kind (`NhAssistantRunCatalogDto.AlertKinds`,
  `NhAssistantRunAlertKindDto`) and an agent in code shows its code's list (`NhAssistantCodeRunSettingsDto.AlertKinds`).
- Agent packages carry the list as `NhAssistantPackageRunSettings.AlertKinds`, written only when there are kinds so
  existing hashes stay, and lockable as the run settings field `alertKinds`; the import refuses a kind the host does
  not register; a host before this version refuses a package that lists kinds as an unknown property. A rule script
  that raises a kind its agent does not list now says that the list is in code or in the run settings.

| Breaking change | Required action |
|---|---|
| The `AgentRunSettingsAlertKinds` migrations add the nullable column `AlertKindsJson` to `AssistantAgentRunConfiguration` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |

## @newheap/platform-ai-chat: alert kinds in the run settings (FB93)

- The run settings form and the guided setup of `nh-assistant-admin` tick the alert kinds an agent created in the
  administration may raise, by their translated names (`displayNameKey`) and urgency, out of the run catalog's
  `alertKinds`. The form sends the list only when it changed, so a kind the application no longer registers stays
  until it is cleared. The Build tab's summary and the flow's results show the kinds, an agent in code's read-only,
  and a kind the application no longer registers shows as a problem at the results. The texts are
  `nh-assistant.admin.run-config.alert-kinds.*` and the problem texts in English and Dutch.
- The models gain `RunAlertKind`, `RunCatalog.alertKinds`, `AgentRunSettings.alertKinds`,
  `AgentRunSettingsRequest.alertKinds`, `CodeRunSettings.alertKinds`, `PackageRunSettings.alertKinds`, the problem
  kind `alert-kind` and `NH_ASSISTANT_MAX_RUN_ALERT_KINDS`; `NH_ASSISTANT_PACKAGE_LOCK_FIELDS.runSettings` gains
  `alertKinds`.
- The mock API of `@newheap/platform-ai-chat/testing` lists the scenario's alert kinds in its run catalog, keeps,
  replaces and refuses the kinds of run settings as the server does, and carries them in agent packages.
