---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-backend-partial-update; workspace management-portal."
tags: ["backend"]
expected_outcome: "The Angular client uses NhBaseApiService.updatePartial or NhApiService.patch with only selected fields and handles the standard 204 response; the controller delegates JSON mapping and validation to DoUpdatePartial, explicitly allow-lists patchable fields, and reaches UpdatePartialAsync with typed NhSetPropertyCalls while service-owned transactions and side effects remain intact."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Expose a PATCH endpoint that updates only the supplied top-level JSON properties while preserving omitted values and the normal service update pipeline, then call it from an Angular API service without sending a complete mutate model.
