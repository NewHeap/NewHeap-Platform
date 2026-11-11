---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-consumer-development; guidance rules nh-consumer-scope-gate, nh-consumer-bootstrap-sequence, nh-consumer-management-portal; workspace empty."
tags: ["foundation"]
expected_outcome: "The request is enough to select the public-website profile without a management portal: the agent summarizes an API with a public Angular front end and no sign-in, runs the bootstrap with --profile public-website --database none and without --authentication, lets it pin Angular to the lowest version that the peer range of @newheap/platform-common accepts, proves the anonymous NuGet restore and npm installation with resolved peers, and runs the inspector in foundation and validate mode without the portal's identity, API-base and modal requirements."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Set up this empty repository for our product information site. Visitors read the pages without signing in, the pages come from our own API, and the site keeps no data of its own yet.
