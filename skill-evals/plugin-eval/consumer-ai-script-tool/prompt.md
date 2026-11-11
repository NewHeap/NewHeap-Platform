---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-scripting, nh-ai-protected-actions; workspace management-portal."
tags: ["backend"]
expected_outcome: "The backend installs the scripting package, allows scripts only for the agent and the MCP exposure that need them under the application's switch, keeps scripts read-only unless a small mutation limit is set, lets every call get the decision it gets on its own so a deferred proposal continues and an inline approval stops the script, and proves the limits, signatures and per-call decisions with engine tests and both providers."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Let the project assistant answer chained questions, such as the overdue tasks of my projects and their owners, with one script over its tools instead of many model rounds, also for MCP clients, and let one of its scripts propose a status change.
