# v-next

## NewHeap.Platform.AI.Scripting: a tool's allocations no longer count as the script's memory

`NhAiScriptLimits.MaxMemoryBytes` counts what the program itself allocates, the tool results it reads
included. Jint's own memory limit measured the thread's allocations since the engine last reset it, and
the continuation after a tool call is no entry that resets it, so what a tool allocated on the script's
thread while it ran counted as the script's: a tool that allocated 32 MB stopped a script that kept
almost nothing with `ai-script-memory-limit`. The runner now rebases that limit at the start of every
segment the engine runs, so only the program's own allocations count. An application that raised
`MaxMemoryBytes` to work around this may lower it again.

| Breaking change | Required action |
|---|---|
| A script whose tools allocate more than `MaxMemoryBytes` while the program keeps little no longer stops with `ai-script-memory-limit`. | None; an application that raised `MaxMemoryBytes` for this may lower it again. |
