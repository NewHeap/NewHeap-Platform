# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: rules and a model in one skill, follow-ups in the skill

A skill may now work by rules first and use a model when they cannot decide: `UseRuleHandler` and
`UseProfile` together are allowed. When a skill's rules return `HandOver(reason)`, the run continues
in that skill instead of the agent's own model: the model gets the skill's tools and instructions,
the skill's profile or else the agent's, and the skill's run ceiling, budget and state apply; the
reason stays in the briefing. The follow-up run that `StartFollowUpRun` starts after a decided or
executed proposal continues in the skill of the run that made the proposal, while the agent
declares it: `skillChosenBy` is the new value `follow-up` (`NhAssistantSkillChoosers.FollowUp`), a
paused skill lets the follow-up wait and a stopped skill refuses it. Runs without a skill follow up
as before. No migration is needed.

| Breaking change | Required action |
|---|---|
| A skill's `HandOver` continues in the skill: the model sees only the skill's tools, its instructions and profile, under the skill's ceiling. | Give the skill the tools and instructions its model needs, or return `UseSkill` to a model skill. |
| A follow-up run after a proposal works in the skill of the run that made it, so that skill's rules or model handle it instead of the agent's routing. | Let the skill's rules recognize the `proposal-executed` and `proposal-decided` inputs. |
| `skillChosenBy` has the new value `follow-up`. | Handle it where you show or filter runs by who chose the skill. |

## @newheap/platform-ai-chat: follow-up runs in the run timeline

The run timeline names a skill chosen for a follow-up as "as the follow-up of a decision", in
English and Dutch; `AgentSkillChooser` gains `follow-up`.

## @newheap/platform-ai-chat and @newheap/platform-common: texts for regional languages

The assistant's bundled texts (`nh-assistant.`) and the texts of the two-factor components
(`nh-two-factor.`) also reach a host language with a region: `nl-NL` and `nl-BE` receive the Dutch
texts and `en-GB` the English ones, where every language other than `en` and `nl` received English
before. A host that merged the `nl` bundle into its regional language itself can remove that merge;
keys the host defines under these prefixes still win.

| Breaking change | Required action |
|---|---|
| A host language such as `nl-NL` receives the Dutch texts instead of the English ones. | None; remove your own merge of the bundles into regional languages if you have one. |
