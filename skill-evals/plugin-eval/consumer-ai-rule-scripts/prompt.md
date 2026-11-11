---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-rule-scripts, nh-ai-alerts, nh-ai-agent-runs; workspace management-portal."
tags: ["backend"]
expected_outcome: "The backend registers the scripting package and allows rule scripts in code with UseRuleScriptsAsData for that agent, with only the writing tools it may call and a maximum of writes per run; an alert kind exists in code and the agent lists it; a schedule with a subject set registered in code starts the agent per project; the administrator saves the script as a version, runs a dry run on chosen projects that records what it would do without effects, and sets it live or tries it in shadow first, with four eyes where the application requires it; the script reads through tools, raises the alert and proposes the review through the tool's own approval policy; the RuleScripts migration is applied on SQL Server or PostgreSQL and the behaviour is tested on both."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Let an administrator write, without a deploy, a daily check for one agent that warns the responsible person when a project is due within three days and still misses a document, and lets it request a review where needed, running without a language model.
