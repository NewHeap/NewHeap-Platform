---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-consumer-development; guidance rules nh-consumer-scope-gate, nh-consumer-bootstrap-sequence; workspace empty."
tags: ["foundation"]
expected_outcome: "The agent uses the supplied product context without asking the user to choose frameworks, proposes a service with no application-owned database, authentication or frontend, keeps only the frontend placeholder, and preserves standard backend extension seams for later APIs or interfaces."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Every night we need to collect a delivery file from an existing partner system, check it, and send a result back. Nobody needs screens or user accounts for the first version, and the partner remains the source of the information.
