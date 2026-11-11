---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Every night we need to collect a delivery file from an existing partner system, check it, and send a result back. Nobody needs screens or user accounts for the first version, and the partner remains the source of the information.
</request>

The agent worked without a person to answer questions. Its starting repository: An empty Git repository.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The agent uses the supplied product context without asking the user to choose frameworks, proposes a service with no application-owned database, authentication or frontend, keeps only the frontend placeholder, and preserves standard backend extension seams for later APIs or interfaces.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
