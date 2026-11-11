---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-runtime-configuration; guidance rules nh-configuration-overrides; workspace management-portal."
tags: ["runtime-configuration"]
expected_outcome: "Shared appsettings with environment-aware secrets-path resolution, explicit args forwarding and CLI precedence over environment variables."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Run NewHeap tooling in a production pipeline on another host with environment and CLI overrides for configuration and secrets.
