---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-ai-agent-runs; workspace management-portal."
tags: ["backend"]
expected_outcome: "After the rejection the client offers the optional feedback of PUT approvals/{approvalId}/feedback with a category and text instead of requiring a reason before the decision; the next run on the same project receives the rejection and its feedback in its continuity block as notes of people, never other subjects' feedback; administrators compare rejection rates and categories per tool and agent version through the feedback summary, list and audited export, an AddProposalFeedbackSink forwards feedback to analytics without logging its text, and not-now feedback is not counted as the agent's mistake."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

When an approver turns down our project agent's proposal, let them optionally say why, make the agent stop suggesting the same thing for that project, and give us a way to see which tools get rejected most.
