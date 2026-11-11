---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-testing; guidance rules nh-ai-observability-evaluation; workspace management-portal."
tags: ["testing"]
expected_outcome: "Versioned datasets and baselines use the Microsoft evaluator contract, unknown and inconclusive results fail closed, deterministic fakes cover failure paths, and telemetry records bounded identifiers and counts without prompts or model content."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Add deterministic regression evaluation and content-free telemetry for an AI feature, and fail the release gate on unknown or inconclusive evaluator outcomes.
