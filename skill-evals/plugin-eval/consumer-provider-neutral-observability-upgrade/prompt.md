---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-platform-development; guidance rules nh-observability-logging; workspace management-portal."
tags: ["router", "backend", "frontend", "runtime-configuration"]
expected_outcome: "The router selects backend, frontend and runtime-configuration guidance; NewHeap owns common ILogger/OpenTelemetry registration once, consumer ServiceDefaults retains health/discovery/resilience, Angular uses NH_ERROR_HANDLERS, and secrets, payloads and raw error messages stay out of telemetry."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Upgrade this API, worker and Angular consumer to the NewHeap provider-neutral observability release, remove Sentry, eliminate duplicate ServiceDefaults telemetry and preserve safe structured domain logs.
