---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-frontend-development; guidance rules nh-frontend-select; workspace management-portal."
tags: ["frontend"]
expected_outcome: "nh-combobox with search, errorText and selectedLabel and nh-multiselect with create are bound as form controls that stay pristine for written values, texts come from one provideNhSelect registration, and nh-form-dropdown is named only as the legacy alternative."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Add a searchable owner field backed by a server search and a tag field that creates new tags to an Angular reactive form, usable with the keyboard and inside a modal.
