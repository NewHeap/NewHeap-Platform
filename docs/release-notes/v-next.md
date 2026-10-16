# v-next

## @newheap/platform-ai-chat: follow-up runs in the run timeline

The run timeline names a skill chosen for a follow-up as "as the follow-up of a decision", in
English and Dutch; `AgentSkillChooser` gains `follow-up`.

## @newheap/platform-ai-chat: texts for regional languages

The assistant's bundled texts (`nh-assistant.`) also reach a host language with a region: `nl-NL`
and `nl-BE` receive the Dutch texts and `en-GB` the English ones, where every language other than
`en` and `nl` received English before. A host that merged the `nl` bundle into its regional
language itself can remove that merge; keys the host defines under `nh-assistant.` still win.

| Breaking change | Required action |
|---|---|
| A host language such as `nl-NL` receives the Dutch texts instead of the English ones. | None; remove your own merge of the bundles into regional languages if you have one. |
