---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-authentication; guidance rules nh-auth-microsoft-oauth; workspace management-portal."
tags: ["authentication"]
expected_outcome: "AddMicrosoftOAuth with the tenant in AllowedTenantIds, the callback registered behind the /api proxy so the correlation cookie reaches it, return URLs limited by NhReturnUrls, accounts linked by tenant and object id after one mail link, and provider tests with NhMicrosoftOAuthTestPlatform instead of Microsoft."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Add Microsoft sign-in for our tenant to an application whose Angular front end calls the API through an /api proxy, keep existing accounts working, and cover it with tests.
