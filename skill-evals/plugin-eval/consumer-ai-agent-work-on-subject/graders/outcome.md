---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Show how many agent suggestions wait for me on every row of our project list, list a project's suggestions on its own page, and let people open the agent conversation about that project from there.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The list asks POST subjects/{type}/summary once for all visible projects instead of once per row, the project page reads GET subjects/{type}/{id}/work for every agent's open proposals with live decision rights, the back-end can include the same data through INhAssistantSubjectWork, the conversation opens through threads/open without starting a run, and the pages refresh on AgentWorkChanged by subject.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
