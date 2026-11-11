---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Start a project intake reviewer agent when a project is created and let a second project approver decide its proposed status change later, also from the assistant panel.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The agent starts from the committed event with a dedupe key, runs as an agent-owned background operation in one thread per project whose access follows the subject resolver, records a deferred proposal under a named four-eyes approval policy with an application presentation, and the thread conversation tells clients per proposal who may decide it, so the decision goes with the proposal hash through the agent-run endpoint.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
