---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Our application has its own EF context and its own user table and does not use NewHeap Identity. Add NewHeap background operations, in-app notifications and an agent that runs on our projects.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The modules are mapped into the application's context with AddNewHeapBackgroundOperations and AddNewHeapNotifications in their own schemas, registered with the standalone AddNewHeap registrations and AddNewHeapHangfireProcessing; the application implements the actor directory and tenant access, forwards the platform's authentication schemes, calls INhRetention from its deletion flow and enqueues inside its own transaction.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
