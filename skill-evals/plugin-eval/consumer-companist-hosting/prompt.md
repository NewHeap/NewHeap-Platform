---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-consumer-development; guidance rules nh-consumer-companist-hosting; workspace management-portal."
tags: ["foundation"]
expected_outcome: "Use the v2 hosting manifest and whole-value Compose mappings, declare managed Companist Auth registration, keep tokens in the BFF, validate the exact tenant/environment/application/deployment scope and project current application permissions into NewHeap claims. Run the hosting validator and real login/revocation checks; do not grant users automatically or block unrelated apps on optional integration advice."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Make a new NewHeap management portal ready to host in Companist with employee login and current application permissions.
