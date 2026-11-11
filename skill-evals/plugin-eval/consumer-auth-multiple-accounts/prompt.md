---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-authentication; guidance rules nh-auth-multiple-accounts, nh-auth-session-lifecycle; workspace management-portal."
tags: ["authentication"]
expected_outcome: "An explicit authentication.multipleAccounts opt-in, renewals through renewSession instead of authenticateRefreshToken, switchAccount and logoutAccount for one account, logout() for the active one, no identity decision from the hint cookie, and the default single-account behavior left unchanged for applications that do not opt in."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Let people stay signed in to their work and their private account in the same browser and switch between them, and stop two open tabs from signing each other out when they renew the session.
