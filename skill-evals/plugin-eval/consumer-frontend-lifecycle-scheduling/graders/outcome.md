---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Review an appOnInitAndLoad hook where this.load().then() intentionally lets page metadata and the remaining lifecycle continue before the request completes. Modernize it safely, but await the load when later initialization really depends on its result.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

Await is treated as an ordering decision: dependent work is awaited, while truly independent work remains explicitly detached with observed errors, destruction-safe cancellation, and protection against stale or repeated route loads.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
