---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Run a long-lived AI report workflow that ingests versioned project documents, suspends for a signal, resumes through Agent Framework checkpoints and survives duplicate delivery.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The workflow uses deterministic vector identities and replacement semantics, binds durable checkpoints to the exact adapter and workflow version, persists content-free resume state, reauthorizes after wake-up, and handles signals and retries idempotently.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
