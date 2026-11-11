---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-frontend-development; guidance rules nh-frontend-lifecycle-modals; workspace management-portal."
tags: ["frontend"]
expected_outcome: "Await is treated as an ordering decision: dependent work is awaited, while truly independent work remains explicitly detached with observed errors, destruction-safe cancellation, and protection against stale or repeated route loads."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Review an appOnInitAndLoad hook where this.load().then() intentionally lets page metadata and the remaining lifecycle continue before the request completes. Modernize it safely, but await the load when later initialization really depends on its result.
