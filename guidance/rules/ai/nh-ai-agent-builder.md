---
id: nh-ai-agent-builder
title: "Let administrators build agents with the agent builder, every change after their approval"
area: backend
reference: ai-assistant
summary: "Switch the agent builder on with AddAgentBuilder and a model profile; administrators chat with it on an agent's Build tab and with New agent with help, it reads the catalog and the agents, dry-runs rule scripts without effects, and every save and step live waits for their approval in the chat and runs with their own rights through the administration's services."
sample-cases: ["SPM-394"]
public-symbols: ["AddAgentBuilder", "NhAssistantAgentBuilderOptions", "NhAssistantAgentBuilderExtensions", "NhAssistantAgentBuilderIds", "setPageContextOverride"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

The agent builder is an assistant agent of the platform that helps an administrator build a whole agent
in plain language: what it is (name, description, instructions, tool selectors, autonomy and what it works
on), its run settings, data skills, data triggers and rule scripts. Switch it on in the assistant's
registration with `AddAgentBuilder(builder => builder.ProfileName = "...")`, from
`NewHeap.Platform.AI.Chat.AspNet`; without a profile it uses the assistant's chat profile, so call
`UseChatProfile` first. Without the call nothing changes.

It is the agent `NhAssistantAgentBuilderIds.AgentId` (`nh-agent-builder`) with the policy
`NhAssistantAgentBuilderIds.Policy`, which requires the assistant's access policy and its administration
policy together: nobody else sees it, chats with it or is offered its tools, and a person who loses the
administration policy cannot continue a chat with it. Its id is reserved and its definition is the
platform's: the administration refuses an agent created with its id and a change of its definition
(`id: not-allowed`). No other agent is offered its tools (`NhAssistantAgentBuilderIds.ToolIdPrefix`,
`assistant.agent-builder.*`), whatever its tool selectors say; they are not exported to MCP clients and
the administration's tool list leaves them out. An application whose discovery policy allow-lists tools
lets the prefix through, as it does for the consultation tools: the tools' own policy decides.

Its tools are governed calls through the shared invoker, each audited as a call of the builder for the
person who chats:

- reads, without approval: `builder_read_catalog` (the tools with their effect, trigger events with their
  filters, object types, subject sets, rule handlers, approval policies, model profiles, agent policies,
  alert kinds and the ceiling of rule scripts), `builder_list_agents`, `builder_read_agent` (definition,
  run settings, skills and triggers in code and as data, rule script summary) and
  `builder_read_rule_script` (revision, live version and candidate, the candidate's latest dry run, the
  ceiling and what a script may call with every tool's signature);
- `builder_dry_run_rule_script`, without approval because it changes nothing;
- writes, each a proposal that waits for the person's approval on an inline card that says what changes:
  `builder_create_agent`, `builder_update_agent`, `builder_save_run_settings`, `builder_save_skill`,
  `builder_save_trigger`, `builder_save_rule_script`, `builder_set_live` (a rule script's, a data skill's
  or a data trigger's candidate) and `builder_set_shadow` (a rule script's candidate, a skill, a trigger or
  the agent).

Every call runs only in a person's own chat with the builder, never in a run, a consultation or another
agent's turn (`assistant-forbidden`), never on the builder itself, and with the person as the actor
through `INhAssistantAgentConfiguration`, `INhAssistantAgentDefinitions`, `INhAssistantAgentControl`
and `INhAssistantRuleScripts`. Their validation, versions, ceilings, package locks, four eyes and audit
apply unchanged: a rule script's write outside the ceiling fails with
`assistant-rule-script-write-not-allowed`, and under four eyes the person who saved a version, also through
the builder, cannot set it live (`assistant-rule-script-four-eyes`); another administrator does. A rule
script version records the person as author and the builder as `AuthorAgentId`, and its audit names the
builder as `AskingAgentId`. There is no tool for grants, technical accounts, MCP servers or credentials,
the code's ceilings or a deletion; a change of run settings keeps whose rights the agent uses
(accountability, technical account, delegation, gateway and elevated reads) and new settings answer to
the agent itself.

In `@newheap/platform-ai-chat` the Build tab of an agent's page in `nh-assistant-admin` holds "Build with
help": the assistant's own chat panel in page mode with the builder, about that agent. "New agent with
help" in the list of agents opens it for a new agent and, once the builder created the agent after the
approval, continues on that agent's page at Build with the same chat. The agent goes along as the page
context of every message (`NhAssistantStore.setPageContextOverride`, entity type `assistant-agent`), a hint
for the model that never authorizes: every tool call names its agent and the card shows it. Both show only
when the person may chat with the builder, and never on the builder's own page.

## Avoid

- Writing a builder of your own that calls the administration's services without the person's approval
  of each change, or with another account's rights.
- Giving another agent the builder's tools through its tool selectors; they are never offered.
- Treating the page context as the agent the builder may change: it is a hint, the approval card is what
  the person decides.
- Expecting the builder to change grants, technical accounts, MCP servers or the code's ceilings, or to
  delete anything; those stay in the administration's screens and in code.

## Verification

On SQL Server and PostgreSQL: a person without the administration policy neither sees nor chats with the
builder and an administrator's model is offered its tools and nothing else, while another agent whose
selectors name every assistant tool gets none; a save waits for the approval and changes nothing before
it, a rejection changes nothing, and an approved save records the person as author with the builder as
the acting agent; reads and the dry run run at once; a write outside the code's ceiling is refused with
the rule scripts' code; four eyes refuses the administrator who saved the version through the builder and
another administrator sets it live; the builder never reads or changes itself; an agent created through
the builder after the approval is audited for the person; and the administration refuses the builder's id
and a change of its definition. Without a database: the catalog has no tool for grants, accounts, MCP,
ceilings or deletions, every write needs an approval, and a call outside a person's chat with the builder
is refused. In the administration, an Angular spec on the mock API proves the chat on the Build tab with
its page context, "New agent with help" continuing on the created agent's page with the same chat, no
builder on its own page and nothing for a person who may not chat with it. SPM-394 is the executable
reference.
