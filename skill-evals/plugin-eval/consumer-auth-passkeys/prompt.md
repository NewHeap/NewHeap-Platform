---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-authentication; guidance rules nh-auth-passkeys, nh-auth-two-factor; workspace management-portal."
tags: ["authentication"]
expected_outcome: "IncludeIdentityPasskeys with one migration that only adds AspNetUserPasskeys, EnablePasskeys with the frontend domain and allowed origins, AddPasskeyEndpoints, a frontend that uses signInWithPasskey, verifyTwoFactorWithPasskey and registerPasskey or the passkey components, localhost instead of an IP address for development, and relational tests with a software authenticator for single-use ceremonies and origin validation."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Let users sign in with a passkey instead of a password and use their passkey as the second step after a password.
