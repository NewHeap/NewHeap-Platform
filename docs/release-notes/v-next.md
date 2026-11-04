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
