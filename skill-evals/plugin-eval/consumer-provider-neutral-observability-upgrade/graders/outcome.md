---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Upgrade this API, worker and Angular consumer to the NewHeap provider-neutral observability release, remove Sentry, eliminate duplicate ServiceDefaults telemetry and preserve safe structured domain logs.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The router selects backend, frontend and runtime-configuration guidance; NewHeap owns common ILogger/OpenTelemetry registration once, consumer ServiceDefaults retains health/discovery/resilience, Angular uses NH_ERROR_HANDLERS, and secrets, payloads and raw error messages stay out of telemetry.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
