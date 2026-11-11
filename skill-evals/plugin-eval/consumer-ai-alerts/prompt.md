---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-background-processing; guidance rules nh-ai-alerts, nh-ai-agent-runs; workspace management-portal."
tags: ["background-processing"]
expected_outcome: "The backend declares an alert kind in code with AddAlertKind, with a display name key, an urgency, a title per culture from named parameters, the subject's responsible person as recipient, a RepeatAfter and a morning digest as default delivery; an agent with a rule handler lists the kind with Alerts(...) and a scheduled trigger starts it on the tasks due today; the rules raise one alert per task and due date with WithAlerts and resolve it with ResolveAlerts once the task is done, relying on one open alert per kind, subject and key, the recipient check and the expiry; the application's own code uses INhAssistantAlerts; the Alerts migration is applied on SQL Server or PostgreSQL and the behaviour is tested on both."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Every working-day morning, warn the person responsible for each task that is due today and not reported done, without asking anyone to approve anything, and stop warning once the task is done.
