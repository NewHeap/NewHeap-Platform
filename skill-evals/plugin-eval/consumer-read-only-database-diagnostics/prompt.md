---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-database-development; guidance rules nh-read-only-database-diagnostics; workspace management-portal."
tags: ["database"]
expected_outcome: "The consumer pins newheap-db and checks in bounded provider profiles, local database-read instructions, and typed query request files as the execution contract. A profile governs provider, configuration path, connection-string name, ceilings and a default environment. Validate runs locally and advertises outbound-network, while Codex starts the first schema or query invocation with network-enabled execution and a narrow reusable permission scoped only to newheap-db instead of probing the restricted sandbox. An explicitly requested runtime environment is passed with --environment to schema and query without requiring a duplicate profile solely for Production or Staging. The agent uses direct schema search-and-describe without a JSON schema file, uses --request-file instead of serialized query arguments, resolves a dedicated read-only credential through NewHeap secrets, returns strict JSON stdout, and proves both successful reads and rejected writes on SQL Server and PostgreSQL."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Give Codex read-only staging and production database diagnostics without passing connection strings or query values through command-line arguments.
