---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Review a small, correct consumer-specific workflow and see whether a current NewHeap package should replace it. Do not add a dependency merely because a similarly named capability exists, but tell us if the review exposes a reusable library opportunity.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The agent checks current package and source evidence, compares behavior and ownership boundaries, and deliberately keeps the workflow consumer-owned when it is application policy or the existing code is simpler. It avoids speculative refactoring while still recording a concrete upstream suggestion when evidence supports one.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
