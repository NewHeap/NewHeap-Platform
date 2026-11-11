---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-platform-development; guidance rules nh-read-only-database-diagnostics; workspace database-read."
tags: ["router", "database", "runtime-configuration"]
expected_outcome: "The router recognizes that persisted-state evidence could clarify the discrepancy, loads the database diagnostics guidance without requiring the user to name a tool, and directly uses the repository-pinned newheap-db profile and checked-in query contract. It does not inspect application code, appsettings, secrets or package internals merely to reconstruct routing or JSON fields; it makes only the required direct schema call and bounded parameterized query, relies on their internal validation instead of adding a validate call, reports the selected profile and provider with the requested evidence, and stops."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

An order is shown as paid in the staging UI, but the API still returns pending. Can you find out why?
