---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-database-development; guidance rules nh-read-only-database-diagnostics; workspace database-read."
tags: ["database"]
expected_outcome: "Validate is a local CI or dry-run contract check and is not required before schema or query because both execution commands validate internally. Codex runs the first schema or query attempt with outbound-network access or escalation and requests a narrow reusable permission scoped only to newheap-db, such as the dotnet tool run newheap-db prefix. It does not probe the restricted sandbox first and does not broaden the permission to arbitrary dotnet, shell or network commands. A network-access-required response remains host-neutral because a sandbox policy, firewall, DNS failure or network ACL can cause it."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Use Codex to run the checked-in newheap-db schema and query diagnostics. Should it try the normal sandbox first or run validate before connecting?
