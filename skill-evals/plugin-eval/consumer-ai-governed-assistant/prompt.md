---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-model-profiles, nh-ai-assets, nh-ai-context-retrieval, nh-ai-local-tool-contract, nh-ai-protected-actions, nh-ai-mcp-tools, nh-ai-mcp-imports; workspace management-portal."
tags: ["backend"]
expected_outcome: "The backend module keeps provider selection named and application-owned, treats retrieved content as scoped untrusted data, versions prompt and tool contracts, applies the shared authorization and approval path to mutations, and exposes or imports only explicitly allowlisted MCP tools."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Add a provider-neutral project assistant with named model profiles, versioned prompts, authorized retrieval, generated local tools, protected mutations and an allowlisted MCP integration.
