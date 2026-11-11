---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-frontend-development; guidance rules nh-frontend-context-menu; workspace management-portal."
tags: ["frontend"]
expected_outcome: "NhContextMenuService opens the menu from the row and from an actions button through NhContextMenu.fromElement, titles stay text, icons use iconClass, and onClick failures close the menu and reach the ErrorHandler."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Add a right-click menu with icons to a project list row whose title includes the project name, and make it usable with the keyboard.
