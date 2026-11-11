---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Let the project assistant answer chained questions, such as the overdue tasks of my projects and their owners, with one script over its tools instead of many model rounds, also for MCP clients, and let one of its scripts propose a status change.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The backend installs the scripting package, allows scripts only for the agent and the MCP exposure that need them under the application's switch, keeps scripts read-only unless a small mutation limit is set, lets every call get the decision it gets on its own so a deferred proposal continues and an inline approval stops the script, and proves the limits, signatures and per-call decisions with engine tests and both providers.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
