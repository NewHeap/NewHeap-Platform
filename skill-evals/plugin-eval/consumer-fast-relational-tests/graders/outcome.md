---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Our consumer test run takes fifteen minutes: every test class starts its own PostgreSQL container, every test applies the migrations, and tests sleep to wait for debounced background work. Make it fast without losing relational evidence.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

One lazily started container per test assembly, a database of its own per test or host copied from a template migrated once or reset, a registered test TimeProvider moved forward instead of sleeping, parallel test classes without shared rows, no short wall-clock assertions, and focused tests while building with the full suite on real providers before merging.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
