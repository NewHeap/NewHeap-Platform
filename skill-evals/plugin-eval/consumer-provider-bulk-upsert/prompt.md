---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-database-development; guidance rules nh-database-bulk-upsert; workspace management-portal."
tags: ["database"]
expected_outcome: "ExecuteUpsertAsync uses the configured unique business key, streams through SQL Server MERGE or PostgreSQL ON CONFLICT in one transaction, writes supported database-generated primary keys back to inserted entities, and rejects unsupported providers and duplicate source keys."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Import thousands of entities through an existing repository, matching each row on a composite business key and inserting or updating immediately without EF change tracking.
