---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Our agent writes a request file before every newheap-db query, and a background service starts newheap-db for each small read. Which route should each of them use, and can we keep the JSON on the command line instead?
</request>

The agent worked without a person to answer questions. Its starting repository: The management-portal workspace with a pinned newheap-db tool at the repository root and one checked-in database-read profile, local instructions and typed requests under src/Back-end.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The agent configures the newheap-db mcp server once, with the profile and environment fixed on its command line, and calls its read-only schema and query tools with structured arguments, so it needs no request file and no shell quoting. The background service keeps one newheap-db session and detects it from the ready line, so it pays for a process start, a connection and a principal verification once. One-shot stdin stays for scripts and --request-file for checked-in requests and CI. Serialized JSON never goes on a process command line, because a command line has a length limit and every shell quotes JSON differently. No tool argument can select a profile, an environment or a connection.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
