---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-database-development; guidance rules nh-read-only-database-diagnostics; workspace database-read."
tags: ["database"]
expected_outcome: "The agent treats production as a live large database, selects only required columns, uses selective typed parameters, applies SQL Server TOP or PostgreSQL LIMIT as well as small maximumRows and timeoutSeconds values, and makes at most one focused index-metadata check when selectivity is uncertain. It avoids broad counts, full scans, SELECT *, EXPLAIN ANALYZE and automatic retries with wider limits, stopping and reporting when a safe bounded query is not supported by the available index evidence."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Investigate on production why a customer event was not processed. The event table may be very large, and this must not put avoidable load on the database.
