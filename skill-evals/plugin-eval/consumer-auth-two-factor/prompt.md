---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-authentication; guidance rules nh-auth-two-factor, nh-auth-session-lifecycle; workspace management-portal."
tags: ["authentication"]
expected_outcome: "AddTwoFactor with authenticator and recovery codes, a derived service that passes the two-factor context and completes the PIN through CompleteFirstFactorAsync, a frontend that uses authenticateInteractive and verifyTwoFactor, and relational tests for challenge, replay and session invalidation."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Let users protect their account with an authenticator app and recovery codes, including the PIN sign-in that the application already offers.
