---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-api-bridge, nh-ai-mcp-tools; workspace management-portal."
tags: ["backend"]
expected_outcome: "The API registers the MVC bridge with explicit controller includes, keeps RequireExplicitPolicy and the default DELETE and upload exclusions, supplies its own discovery policy through UseInnerDiscoveryPolicy, relies on controller authorization at execution, and keeps composite workflows as curated generated tools."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Let the assistant call our existing policy-protected API controllers as the signed-in user without duplicating authorization in new tool code.
