---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
An order is shown as paid in the staging UI, but the API still returns pending. Can you find out why?
</request>

The agent worked without a person to answer questions. Its starting repository: The management-portal workspace with a pinned newheap-db tool at the repository root and one checked-in database-read profile, local instructions and typed requests under src/Back-end.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The router recognizes that persisted-state evidence could clarify the discrepancy, loads the database diagnostics guidance without requiring the user to name a tool, and directly uses the repository-pinned newheap-db profile and checked-in query contract. It does not inspect application code, appsettings, secrets or package internals merely to reconstruct routing or JSON fields; it makes only the required direct schema call and bounded parameterized query, relies on their internal validation instead of adding a validate call, reports the selected profile and provider with the requested evidence, and stops.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
