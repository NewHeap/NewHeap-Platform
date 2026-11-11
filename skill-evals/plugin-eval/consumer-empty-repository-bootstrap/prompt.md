---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-consumer-development; guidance rules nh-consumer-scope-gate, nh-consumer-bootstrap-sequence, nh-consumer-management-portal, nh-consumer-optional-infrastructure; workspace empty."
tags: ["foundation"]
expected_outcome: "The explicit request supplies enough product context to select the management-portal profile without redundant questions; the pinned skill bootstraps src/Back-end and src/Front-end, package restore and install form a hard gate, only selected infrastructure is added, and foundation plus final validation pass."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Here is the verified URL for the versioned NewHeap plugin package. Prepare this empty repository as a PostgreSQL management portal, prove package access before adding features, use NewHeap controllers and modals, enable Aspire and Docker but leave Elasticsearch disabled, and audit the completed setup.
