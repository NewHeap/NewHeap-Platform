---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Give Codex read-only staging and production database diagnostics without passing connection strings or query values through command-line arguments.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The consumer pins newheap-db and checks in bounded provider profiles, local database-read instructions, and typed query request files as the execution contract. A profile governs provider, configuration path, connection-string name, ceilings and a default environment. Validate runs locally and advertises outbound-network, while Codex starts the first schema or query invocation with network-enabled execution and a narrow reusable permission scoped only to newheap-db instead of probing the restricted sandbox. An explicitly requested runtime environment is passed with --environment to schema and query without requiring a duplicate profile solely for Production or Staging. The agent uses direct schema search-and-describe without a JSON schema file, uses --request-file instead of serialized query arguments, resolves a dedicated read-only credential through NewHeap secrets, returns strict JSON stdout, and proves both successful reads and rejected writes on SQL Server and PostgreSQL.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
