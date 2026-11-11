---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-authentication; guidance rules nh-auth-two-factor, nh-auth-session-lifecycle; workspace management-portal."
tags: ["authentication"]
expected_outcome: "RequireFor with the finance role, an enrollment step handled with the enrollment endpoints or NhTwoFactorEnrollmentComponent, EnableEmailCodes that does not satisfy the role requirement, EnableRememberDevice, UseSecurityNotifications with WithNotifications, UseAdministrationPolicy with AddTwoFactorOperations for reset, reminders and session revocation, and relational tests for enrollment, refresh refusal, e-mail code reuse and remembered devices."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Require two-step verification for every finance administrator, let them enroll during their next sign-in, offer e-mail codes to other users, remember trusted devices and let support reset a user who lost their phone.
