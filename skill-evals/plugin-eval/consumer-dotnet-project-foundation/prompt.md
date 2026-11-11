---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-backend-project-foundation; workspace empty."
tags: ["backend"]
expected_outcome: "The solution and Directory props live in src/Back-end, Directory.Build.props defines the shared build baseline, Directory.Packages.props enables central package management, and project PackageReference items remain versionless."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Generate a new multi-project .NET consumer solution that uses NewHeap packages and keeps shared build and dependency settings consistent.
