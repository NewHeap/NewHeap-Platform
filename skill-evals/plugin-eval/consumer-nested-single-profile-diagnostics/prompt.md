---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-platform-development; guidance rules nh-read-only-database-diagnostics; workspace database-read."
tags: ["router", "database", "runtime-configuration"]
expected_outcome: "The router makes one bounded tracked-file search for the profile catalog, local database-read README and request files before declaring the contract missing. When it finds exactly one nested catalog and the user did not name another environment, it uses the directory containing .newheap as the diagnostic working directory and lets newheap-db select the catalog's only profile and default environment without another question. The profile's provider, configuration path, connection-string name and ceilings remain authoritative; newheap-db resolves the configured name through the existing NewHeap configuration and secrets flow without the agent reading or exposing the connection string. The agent runs exactly one schema --search customer --describe-if-single invocation and one bounded query invocation, with no separate validate, package/source/configuration inspection or repeated call, and stops with the count plus newest customer evidence."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

From this monorepo root, count the customer records and return the newest customer by CreationDateTime. The local .NET tool manifest is at the repository root, while the database-read contract is somewhere below it.
