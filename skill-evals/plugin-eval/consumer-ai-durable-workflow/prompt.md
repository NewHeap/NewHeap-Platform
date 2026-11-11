---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-background-processing; guidance rules nh-ai-ingestion, nh-ai-durable-runs, nh-ai-agent-framework, nh-background-operation-suspension; workspace management-portal."
tags: ["background-processing"]
expected_outcome: "The workflow uses deterministic vector identities and replacement semantics, binds durable checkpoints to the exact adapter and workflow version, persists content-free resume state, reauthorizes after wake-up, and handles signals and retries idempotently."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Run a long-lived AI report workflow that ingests versioned project documents, suspends for a signal, resumes through Agent Framework checkpoints and survives duplicate delivery.
