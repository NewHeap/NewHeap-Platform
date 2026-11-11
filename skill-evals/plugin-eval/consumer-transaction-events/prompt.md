---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-background-processing; guidance rules nh-backend-service-unit-of-work, nh-operations-events-jobs-notifications; workspace management-portal."
tags: ["background-processing"]
expected_outcome: "Service-owned transaction, atomic outbox and scoped/idempotent processing."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Add a transactional mutation with a CAP event, an idempotent consumer, a job, and a notification.
