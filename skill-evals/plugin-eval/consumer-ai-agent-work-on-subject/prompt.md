---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-agent-runs; workspace management-portal."
tags: ["backend"]
expected_outcome: "The list asks POST subjects/{type}/summary once for all visible projects instead of once per row, the project page reads GET subjects/{type}/{id}/work for every agent's open proposals with live decision rights, the back-end can include the same data through INhAssistantSubjectWork, the conversation opens through threads/open without starting a run, and the pages refresh on AgentWorkChanged by subject."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Show how many agent suggestions wait for me on every row of our project list, list a project's suggestions on its own page, and let people open the agent conversation about that project from there.
