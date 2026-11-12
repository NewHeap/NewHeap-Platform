---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-testing; guidance rules nh-fast-consumer-tests; workspace management-portal."
tags: ["testing"]
expected_outcome: "One lazily started container per test assembly, a database of its own per test or host copied from a template migrated once or reset, a registered test TimeProvider moved forward instead of sleeping, parallel test classes without shared rows, no short wall-clock assertions, and focused tests while building with the full suite on real providers before merging."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Our consumer test run takes fifteen minutes: every test class starts its own PostgreSQL container, every test applies the migrations, and tests sleep to wait for debounced background work. Make it fast without losing relational evidence.
