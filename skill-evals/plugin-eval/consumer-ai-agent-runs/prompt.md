---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-agent-runs; workspace management-portal."
tags: ["backend"]
expected_outcome: "The agent starts from the committed event with a dedupe key, runs as an agent-owned background operation in one thread per project whose access follows the subject resolver, records a deferred proposal under a named four-eyes approval policy with an application presentation, and the thread conversation tells clients per proposal who may decide it, so the decision goes with the proposal hash through the agent-run endpoint."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Start a project intake reviewer agent when a project is created and let a second project approver decide its proposed status change later, also from the assistant panel.
