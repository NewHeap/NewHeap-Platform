---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-platform-development; guidance rules nh-backend-module-composition; workspace management-portal."
tags: ["router", "backend"]
expected_outcome: "The agent checks current package and source evidence, compares behavior and ownership boundaries, and deliberately keeps the workflow consumer-owned when it is application policy or the existing code is simpler. It avoids speculative refactoring while still recording a concrete upstream suggestion when evidence supports one."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Review a small, correct consumer-specific workflow and see whether a current NewHeap package should replace it. Do not add a dependency merely because a similarly named capability exists, but tell us if the review exposes a reusable library opportunity.
