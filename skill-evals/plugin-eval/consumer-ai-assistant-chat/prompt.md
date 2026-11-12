---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-assistant, nh-ai-protected-actions, nh-ai-mcp-imports; workspace management-portal."
tags: ["backend"]
expected_outcome: "The API registers AddNewHeapAssistant after the ASP.NET AI integration with an access and admin policy, applies the library-owned nhai migrations explicitly, keeps a feature flag off by default, binds approvals to exact proposals, imports MCP tools disabled by default with protected secrets and host allow-lists, and never writes conversation content to audit or logs."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Add an in-app assistant with durable conversations, streamed replies, in-chat approval of mutations, administrator-managed agents and MCP servers, an editable application context and per-user style preferences.
