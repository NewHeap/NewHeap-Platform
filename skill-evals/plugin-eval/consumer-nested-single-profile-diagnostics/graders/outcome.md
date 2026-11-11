---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
From this monorepo root, count the customer records and return the newest customer by CreationDateTime. The local .NET tool manifest is at the repository root, while the database-read contract is somewhere below it.
</request>

The agent worked without a person to answer questions. Its starting repository: The management-portal workspace with a pinned newheap-db tool at the repository root and one checked-in database-read profile, local instructions and typed requests under src/Back-end.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The router makes one bounded tracked-file search for the profile catalog, local database-read README and request files before declaring the contract missing. When it finds exactly one nested catalog and the user did not name another environment, it uses the directory containing .newheap as the diagnostic working directory and lets newheap-db select the catalog's only profile and default environment without another question. The profile's provider, configuration path, connection-string name and ceilings remain authoritative; newheap-db resolves the configured name through the existing NewHeap configuration and secrets flow without the agent reading or exposing the connection string. The agent runs exactly one schema --search customer --describe-if-single invocation and one bounded query invocation, with no separate validate, package/source/configuration inspection or repeated call, and stops with the count plus newest customer evidence.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
