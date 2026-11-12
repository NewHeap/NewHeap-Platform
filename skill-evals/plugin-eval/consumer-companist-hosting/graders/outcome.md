---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Make a new NewHeap management portal ready to host in Companist with employee login and current application permissions.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

Use the v2 hosting manifest and whole-value Compose mappings, declare managed Companist Auth registration, keep tokens in the BFF, validate the exact tenant/environment/application/deployment scope and project current application permissions into NewHeap claims. Run the hosting validator and real login/revocation checks; do not grant users automatically or block unrelated apps on optional integration advice.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
