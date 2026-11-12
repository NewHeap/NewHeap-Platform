---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-agent-builder, nh-ai-rule-scripts; workspace management-portal."
tags: ["backend"]
expected_outcome: "The backend calls AddAgentBuilder with a model profile and gives it guidance of the application's own with UseGuidance and a trusted text asset; only people who pass the assistant's access policy and its administration policy can chat with the builder; the builder reads the catalog, the agent with its blockers, the accounts and the API gateway's resources with the administrator's rights, and every change, also of the accountability, the technical account and gateway reads, and every removal is a proposal the administrator approves in the chat on a card that says so plainly, acting with the administrator's own rights through the administration services, so ceilings, versions, audits and four eyes still apply; it never takes or returns MCP credentials or other secrets, connecting MCP servers stays in the screens, and it never goes beyond the code's ceilings; a discovery policy that allow-lists tools lets the builder's tool prefix through; the administration shows the builder on the agent page and for a new agent."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Give administrators an assistant in the administration that helps them build an agent from a description in plain language, including its instructions, triggers, technical account, gateway reads and a rule script, and that knows the application's data, but never handles MCP credentials or other secrets.
