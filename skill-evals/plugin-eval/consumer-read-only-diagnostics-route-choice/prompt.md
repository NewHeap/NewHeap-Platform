---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-database-development; guidance rules nh-read-only-database-diagnostics; workspace database-read."
tags: ["database"]
expected_outcome: "The agent configures the newheap-db mcp server once, with the profile and environment fixed on its command line, and calls its read-only schema and query tools with structured arguments, so it needs no request file and no shell quoting. The background service keeps one newheap-db session and detects it from the ready line, so it pays for a process start, a connection and a principal verification once. One-shot stdin stays for scripts and --request-file for checked-in requests and CI. Serialized JSON never goes on a process command line, because a command line has a length limit and every shell quotes JSON differently. No tool argument can select a profile, an environment or a connection."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Our agent writes a request file before every newheap-db query, and a background service starts newheap-db for each small read. Which route should each of them use, and can we keep the JSON on the command line instead?
