---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 4
---

The user gave an AI coding agent this request:

<request>
Here is the verified URL for the versioned NewHeap plugin package. Prepare this empty repository as a PostgreSQL management portal, prove package access before adding features, use NewHeap controllers and modals, enable Aspire and Docker but leave Elasticsearch disabled, and audit the completed setup.
</request>

The agent worked without a person to answer questions. Its starting repository: An empty Git repository.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The explicit request supplies enough product context to select the management-portal profile without redundant questions; the pinned skill bootstraps src/Back-end and src/Front-end, package restore and install form a hard gate, only selected infrastructure is added, and foundation plus final validation pass.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
