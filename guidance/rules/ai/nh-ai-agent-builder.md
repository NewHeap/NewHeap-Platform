---
id: nh-ai-agent-builder
title: "Let administrators build agents with the agent builder, every change after their approval"
area: backend
reference: ai-assistant
summary: "Switch the agent builder on with AddAgentBuilder and a model profile, optionally with guidance of the application's own (UseGuidance); administrators chat with it on an agent's Build tab and with New agent with help; it does what they can do in the administration's screens except secrets, reads the catalog, the agents with their blockers, accounts and the API gateway's resources, dry-runs rule scripts, and every change waits for their approval in the chat and runs with their own rights through the administration's services; UseAgentRightsPolicy says who may change an agent's rights anywhere and WithoutRightsChanges keeps those settings out of the builder and in the screens."
sample-cases: ["SPM-394", "SPM-396"]
public-symbols: ["AddAgentBuilder", "NhAssistantAgentBuilderOptions", "UseGuidance", "MaxGuidanceLength", "WithoutRightsChanges", "AllowsRightsChanges", "UseAgentRightsPolicy", "NhAssistantAgentBuilderExtensions", "NhAssistantAgentBuilderIds", "setPageContextOverride"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

The agent builder is an assistant agent of the platform that helps an administrator build and change a whole
agent in plain language, and can do for an agent what the administrator can do in the administration's
screens, except secrets: what it is (name, description, instructions, tool selectors, autonomy, required
policy, MCP servers and what it works on), how it runs and whose rights it uses (run settings with
accountability, technical account, delegation, gateway and elevated reads), its skills, triggers,
consultations, hand-offs, metadata, groups, shadow variants, switches, state and rule scripts, and removing and
discarding them. Switch it on in the assistant's registration with
`AddAgentBuilder(builder => builder.ProfileName = "...")`, from `NewHeap.Platform.AI.Chat.AspNet`; without a
profile it uses the assistant's chat profile, so call `UseChatProfile` first. Without the call nothing
changes.

Give it guidance of the application's own with `builder.UseGuidance(asset)`: an `NhAiTextAsset` in code, such
as one loaded with `NhAiTextAsset.FromEmbeddedResource`, about the domain, the data and the conventions of the
application, such as which gateway resource holds which data. Only the builder reads it, after the platform's
rules and before the application context, in a section framed as background: it never changes the platform's
rules, the builder's instructions, its tools or the approvals. It must be trusted system instructions of the
application, not blank, at most `NhAssistantAgentBuilderOptions.MaxGuidanceLength` (4,000) characters, with
the hash of its content; its id and version are part of the builder's prompt version.

It is the agent `NhAssistantAgentBuilderIds.AgentId` (`nh-agent-builder`) with the policy
`NhAssistantAgentBuilderIds.Policy`, which requires the assistant's access policy and its administration
policy together, the latter without a resource as the administration's endpoints check it: nobody else sees
it, chats with it or is offered its tools, and a person who loses the administration policy cannot continue
a chat with it. It gets no MCP server (`agentId: not-allowed`) and its turns load none. Its id is reserved
and its definition is the platform's: the administration refuses an agent created with its id and a change
of its definition
(`id: not-allowed`), and no builder tool works on the builder itself. No other agent is offered its tools
(`NhAssistantAgentBuilderIds.ToolIdPrefix`, `assistant.agent-builder.*`), whatever its tool selectors say;
they are not exported to MCP clients and the administration's tool list leaves them out. An application whose
discovery policy allow-lists tools lets the prefix through, as it does for the consultation tools: the tools'
own policy decides.

Its tools are governed calls through the shared invoker, each audited as a call of the builder for the person
who chats:

- reads, without approval: `builder_read_catalog` (tools, trigger events with their filters, object types,
  subject sets, rule handlers, approval policies, model profiles, agent policies, alert kinds, agent groups,
  the ceiling of rule scripts, the gateway's tools and the MCP servers an agent may be assigned),
  `builder_list_agents`, `builder_read_agent` (definition, what it works on, state, run settings, skills,
  triggers and consultations in code and as data, hand-offs, metadata, shadow variants, switches, rule script
  summary and blockers), `builder_read_rule_script`, `builder_search_accounts` (people and technical accounts,
  as the administration's account search) and `builder_read_gateway` (the API gateway's own
  `search-resources` and `describe-resource` with the administrator's rights: filter fields and operators,
  search and order fields, whether a query can only count; without a gateway it says there is none);
- `builder_dry_run_rule_script`, without approval because it changes nothing;
- writes, each a proposal that waits for the person's approval on an inline card that says what changes:
  `builder_create_agent` (also a shadow variant with `shadowOf`), `builder_update_agent`,
  `builder_save_run_settings` (also `alertKinds`: the kinds in code an agent created in the administration
  lists for its rule scripts and model, see `nh-ai-alerts`), `builder_save_skill`, `builder_save_trigger`, `builder_save_rule_script`,
  `builder_save_consultation`, `builder_save_hand_off`, `builder_save_metadata`, `builder_set_live`,
  `builder_set_shadow`, `builder_set_state`, `builder_set_technical_account` (an agent in code),
  `builder_set_consultation_routes`, `builder_set_mcp_server` (assign an existing server or remove the
  assignment), `builder_set_mcp_headless_use`, `builder_set_switch` (tools without approval, the alert model),
  `builder_set_groups`, `builder_promote_shadow_variant` and `builder_remove`.

Input that a call leaves out keeps what the agent has, also for the skills, triggers (while their source
stays the same), consultations and hand-offs their services store whole: nothing is reset that the
administrator did not mention. Every call's input is checked against its tool's schema before it becomes a
proposal, and the card and the change read the same checked input: a number as a string, a string as yes or
no, `null` where the schema has none, a blank text, a value outside its choices or bounds and an unknown
property are refused (`ai-tool-input-invalid`); `null` clears a value only where the schema allows it, and the
card says the default applies. `null` for an optional property that takes none reads as left out, and a whole
number written as `1.0` as that number. A write is proposed only with the builder's own valid card, built
for the proposal after the invoker's gate in every culture: the values of whose rights it changes and what it
removes have fields of their own, first and in full, what an agent, skill or consultation may do follows,
never shortened, with lists spanning fields, and the other values follow, shortened as far as the card needs
to fit also as stored JSON. A call whose card cannot be built so, or whose presenter fails or times out, is
not proposed and reads why; no other card stands in. A change of whose rights an agent uses (accountability,
technical account, delegation, gateway or elevated reads, required policy, who may run it or see its results
for the whole application, MCP servers and their use without a person, consultation routes, the promotion of a
shadow variant, whose card names the live agent and what it takes over, at the variant's version the call must
name; a variant that becomes its package's next version is promoted in the screens) and every removal get a
card whose notice says so plainly. `builder_set_mcp_headless_use` changes only
the server's use without a person, while its authentication is still what it was checked against; its
connection, secret and other settings stay.

Every call runs only in a person's own chat with the builder, never in a run, a consultation or another
agent's turn (`assistant-forbidden`), and with the person as the actor through `INhAssistantAgentConfiguration`,
`INhAssistantAgentDefinitions`, `INhAssistantAgentControl`, `INhAssistantRuleScripts`,
`INhAssistantMcpServers`, `INhAssistantDataHandOffs`, `INhAssistantWithoutApprovalSwitches`,
`INhAssistantAlertModelSwitches` and `INhAssistantAgentGroups`. Their validation, versions, ceilings, package
locks, four eyes and audit apply unchanged: a technical account that would lend the agent rights the person
does not hold is refused (`assistant-service-account-not-permitted`), and so are new tools on an agent that answers
to one when the account may use them and the person may not, before any card; a rule script's write outside the
ceiling fails with `assistant-rule-script-write-not-allowed`, and under four eyes the person who saved a
version, also through the builder, cannot set it live (`assistant-rule-script-four-eyes`). A rule script
version records the person as author and the builder as `AuthorAgentId`, and its audit names the builder as
`AskingAgentId`.

Two settings decide about an agent's rights: whose rights it uses (accountability, technical account,
delegation, gateway and elevated reads), who reaches it (required, run and view policy, MCP servers and their
settings, a consultation's routes, asker policy, roles and visibility to MCP clients), who decides its proposals
(approval policies, a consultation's proposal policy) and what runs without approval. They answer different
questions, and an application may use both.

- `UseAgentRightsPolicy(policy)` on the assistant's builder says **who**. A person needs that policy on top of
  the administration policy to change those settings, in the administration's screens, through the builder and
  through an import or promotion of an agent package alike; the application registers the policy and startup
  fails when it does not exist. Use it when the people who manage agents should not lend anyone's rights, for
  example a project manager who builds agents next to an administrator who decides about rights. A person
  without it gets `403 assistant-agent-rights-forbidden` with a field error `rights-required` per setting, and
  `GET admin/run-catalog` says `mayChangeAgentRights`, so the administration disables those fields beforehand.
- `AddAgentBuilder(builder => builder.WithoutRightsChanges())` says **where**. The builder stays on for
  instructions, skills, triggers, consultations, tools and rule scripts, and those settings stay in the screens
  for everyone, also for an administrator. It is a ceiling on the builder, not on a person: the same person still
  changes them in the screens, so it is no second pair of eyes. Use it when changes of rights should be a
  deliberate step in a screen rather than something a chat proposes.

Both compare what a save would store with what is stored, so saving a value again needs nothing. New plain run
settings, answering to the agent itself, a new consultation with its defaults and a required policy on a new agent,
which only narrows who reaches it, stay open. So does what only takes something away: removing an MCP server,
disabling its tool, using it never without a person, and switching a tool's run without approval off or to shadow.
On an agent whose runs use another account's rights, more tools or more autonomy count as well, because they put
those rights to more use. Application code with an actor the platform knows as no person decides for itself, as it
does for a technical account. `builder_read_catalog` tells the builder with `mayChangeAgentRights` whether it may
change them here and with `agentRightsInScreens` whether this person may change them in the screens, so it names
the setting and where to change it instead of proposing; a call that names such a setting anyway is refused before
an approval card. The preview of an agent package says per part what it changes in `rightsChanges`.

It never handles secrets: no tool takes or returns MCP credentials, keys or other secrets; MCP servers are
listed without their address, connection, references or credentials; and a call that names a property its
tool does not take, such as `secret`, is refused (`ai-tool-input-invalid`) before it becomes a proposal.
Connecting an MCP server, its address and its credentials stay in the administration's screens.

`builder_read_agent` names what blocks the agent, each blocker with a code, the setting and the tool that
changes it, or none when only the application's code or configuration does: `not-configured`,
`gateway-reads-off`, `gateway-reads-need-accountability`, `technical-account-missing`, `alert-kind-not-listed`,
`no-read-issuer`, `not-active`, `agent-work-off`, `no-trigger`, `in-shadow`, `run-settings-out-of-effect`,
`configuration-problem`, `instructions-unavailable`, and `run-failed` with the code its recent runs failed with,
such as a cause of a failed gateway read (`NhAiDelegatedReadFailureCodes`, see `nh-ai-api-bridge`). The
builder names the setting in plain words and offers to change it after an approval.

In `@newheap/platform-ai-chat` the Build tab of an agent's page in `nh-assistant-admin` holds "Build with
help": the assistant's own chat panel in page mode with the builder, about that agent. "New agent with
help" in the list of agents opens it for a new agent and, once the builder created the agent after the
approval, continues on that agent's page at Build with the same chat. After every change the builder made
after an approval the page reads the agent again, and it goes back to the agents when the builder removed the
agent it shows. The agent goes along as the page context of every message
(`NhAssistantStore.setPageContextOverride`, entity type `assistant-agent`), a hint for the model that never
authorizes: every tool call names its agent and the card shows it. Both show only when the person may chat
with the builder, and never on the builder's own page.

## Avoid

- Writing a builder of your own that calls the administration's services without the person's approval
  of each change, or with another account's rights.
- Giving another agent the builder's tools through its tool selectors; they are never offered.
- Treating the page context as the agent the builder may change: it is a hint, the approval card is what
  the person decides.
- Expecting the builder to connect MCP servers or handle their credentials or other secrets, or to go beyond
  the code's ceilings; those stay in the administration's screens and in code.
- Putting rules in the guidance that the builder must obey: it is background, and the platform's rules and
  the builder's instructions win.
- Treating `WithoutRightsChanges` as a limit on a person: use `UseAgentRightsPolicy` when someone may not change
  an agent's rights at all, because the screens stay open otherwise.
- Relying on a person's approval card as a second pair of eyes: with one administrator the person who asks is the
  person who approves.

## Verification

On SQL Server and PostgreSQL: a person without the administration policy neither sees nor chats with the
builder and an administrator's model is offered its tools and nothing else, while another agent whose
selectors name every assistant tool gets none; a save waits for the approval and changes nothing before
it, a rejection changes nothing, and an approved save records the person as author with the builder as
the acting agent; reads and the dry run run at once; a write outside the code's ceiling is refused with
the rule scripts' code; four eyes refuses the administrator who saved the version through the builder and
another administrator sets it live; the builder never reads or changes itself; an agent created through
the builder after the approval is audited for the person; the administration refuses the builder's id and a
change of its definition; the builder searches the accounts and changes whose rights an agent uses only
after an approval whose card says so, and a later change that names only the concurrency keeps them; a
removal waits for a card that names what goes, a rejection removes nothing, an agent in code is not deleted
and an agent created in the administration is, audited for the person; an existing MCP server is listed
without its secret, a call that sends a secret is refused before any approval, the assignment and the use
without a person change after their approvals and the server keeps its secret and what another
administrator changed meanwhile; input its card could read otherwise is refused before any approval, and a
change of a skill, a trigger or a hand-off keeps what it leaves out, also when the model sends null for it
and a version as 1.0; a call whose card cannot show its MCP servers in full is refused before any approval;
the builder gets no MCP server and loads none,
also with an assignment stored before; an administration policy that decides by a resource lets nobody use
the builder where the screens refuse them; the gateway's resources are
read with the administrator's rights, the blockers name gateway reads, the accountability, an unlisted alert
kind and the missing trigger, the builder lists the kind after an approval whose card names it and the blocker is gone, and the guidance reaches the builder after the rules and before the
application context and no other agent. Without a database: 27 tools, every write needs an approval, no
tool takes a secret, every write's card keeps within the presentation's limits in English and Dutch with the
longest input its schema allows or refuses it with a reason, values of whose rights stay whole in fields of
their own while escaped and non-Latin values are shortened until the stored card fits, input of the wrong
type, a blank text, null or an unknown property is refused, the card shows the values that change, a
promotion names the live agent and what it takes over and refuses another version of the variant, a
write whose card cannot be built or whose presenter fails, times out or builds none is not proposed and no
other card stands in, what an agent may do is never shortened and spans fields, a change of rights and every removal say so, a call outside a person's chat with the builder is refused, the
guidance is checked, the blockers name why recent runs failed and a host without read credentials, and the
API bridge's gateway resources find and describe only what the person may use. In the administration, an
Angular spec on the mock API proves the chat on the Build tab with its page context, "New agent with help"
continuing on the created agent's page with the same chat, the page going back to the agents after the
builder removed its agent, no builder on its own page and nothing for a person who may not chat with it.
Under `UseAgentRightsPolicy`, on SQL Server and PostgreSQL: a person without the policy creates an agent with plain
run settings, and is refused with the settings named when lending rights, changing a required policy, widening the
tools of an agent that uses another account's rights, assigning an MCP server or changing its settings, binding a
technical account, switching consultation routes, or going live without approval, with nothing stored; saving the
same value again and what only takes away go ahead; a person with the policy and application code go ahead; an
agent package that changes rights says so in its preview and its import stores nothing for a person without the
policy; and the builder refuses such a call before any card. Under `WithoutRightsChanges` the builder refuses
them for everyone and names the screens, and the same person changes them in the screens. A policy the application
did not register stops the start. SPM-394 is the executable reference for building a rule script with the builder
and for who changes an agent's rights, SPM-396 for an
application-wide weekly count through the gateway with a technical account, gateway reads and a listed
alert kind (FB93).
