---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-frontend-development; guidance rules nh-frontend-relative-time; workspace management-portal."
tags: ["frontend"]
expected_outcome: "The template uses nhRelativeTime, which follows the active language of the module configuration and refreshes itself through one shared timer also in OnPush components, without a component interval; the absolute date stays available in a title or time element, and tests pass a fixed now or use fakeAsync and destroy the view."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Show "3 minutes ago" style times in the task list in the user's language, keep them current while the page stays open, and make the component test deterministic.
