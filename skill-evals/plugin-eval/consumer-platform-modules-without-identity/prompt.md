---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-background-processing; guidance rules nh-platform-modules-without-identity, nh-operations-events-jobs-notifications, nh-ai-agent-runs; workspace management-portal."
tags: ["background-processing"]
expected_outcome: "The modules are mapped into the application's context with AddNewHeapBackgroundOperations and AddNewHeapNotifications in their own schemas, registered with the standalone AddNewHeap registrations and AddNewHeapHangfireProcessing; the application implements the actor directory and tenant access, forwards the platform's authentication schemes, calls INhRetention from its deletion flow and enqueues inside its own transaction."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Our application has its own EF context and its own user table and does not use NewHeap Identity. Add NewHeap background operations, in-app notifications and an agent that runs on our projects.
