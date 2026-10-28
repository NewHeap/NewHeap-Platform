# v-next

## @newheap/platform-ai-chat: agent administration by agent page

The administration opens on Agents and lists every agent once, with one status, where it works, where
it comes from and what needs attention; a row opens the agent's page, whose header holds the status
changes, "Test in chat", "Run now" and "More" (export, reset to code, delete), and whose tabs Overview,
Build, Rollout and Activity replace the separate editor, run configuration, runs, feedback and
evaluation views. Every feature stays; hosts can keep the open agent in their URL with `location` and
`locationChange`. `nh-assistant-panel` gains `mode="page"` for a chat in a page of its own.

| Breaking change | Required action |
|---|---|
| `NhAssistantAdminComponent.initialTab` defaults to `agents` instead of `context`, and the sections read Agents, Tools & connections, Application context. | Pass `initialTab="context"` to keep opening on the application context. |
| The agent cards and their buttons are gone: state, shadow, account, run now, export, reset and delete moved to the agent's page, the editor and the run configuration to its Build tab, runs and feedback to Activity, shadow variants and evaluation to Rollout. | Update end-to-end tests and documentation that clicked card buttons: open the agent with its list row (`[data-agent-row]`), then the tab (`[data-agent-tab]`) or the header menus (`[data-status-menu]`, `[data-more]`). |
