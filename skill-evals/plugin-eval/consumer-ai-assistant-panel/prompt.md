---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-frontend-development; guidance rules nh-ai-assistant-ui; workspace management-portal."
tags: ["frontend"]
expected_outcome: "The application calls provideNhAssistant once with the host token and access policy, places one launcher and one panel in the layout, lazily routes to the administration entry point behind the admin permission, loads the CDK overlay styles and tests against the scripted mock API."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Show the assistant panel in our Angular shell only for permitted users and give administrators a page to manage agents, MCP servers and the application context.
