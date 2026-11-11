---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Investigate on production why a customer event was not processed. The event table may be very large, and this must not put avoidable load on the database.
</request>

The agent worked without a person to answer questions. Its starting repository: The management-portal workspace with a pinned newheap-db tool at the repository root and one checked-in database-read profile, local instructions and typed requests under src/Back-end.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The agent treats production as a live large database, selects only required columns, uses selective typed parameters, applies SQL Server TOP or PostgreSQL LIMIT as well as small maximumRows and timeoutSeconds values, and makes at most one focused index-metadata check when selectivity is uncertain. It avoids broad counts, full scans, SELECT *, EXPLAIN ANALYZE and automatic retries with wider limits, stopping and reporting when a safe bounded query is not supported by the available index evidence.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
