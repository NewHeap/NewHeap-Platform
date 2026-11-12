---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Add application-question answering to an existing consumer. Before coding, check the current NewHeap checkout and this consumer's pinned packages for Agent Framework, MCP, ASP.NET context, governed tools, usage/audit and read-only database support. Use what fits, keep application policy local, and identify any reusable Platform gap without changing the Platform as part of this task.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The router loads only the relevant modules plus the Platform fit reference, verifies released capabilities and consumer pins instead of relying on memory, adopts the smallest compatible public seams, keeps server-resolved application scope and policy consumer-owned, and records any credible NewHeap library suggestion separately without expanding task scope.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
