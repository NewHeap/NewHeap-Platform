---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Let an administrator write, without a deploy, a daily check for one agent that warns the responsible person when a project is due within three days and still misses a document, and lets it request a review where needed, running without a language model.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The backend registers the scripting package and allows rule scripts in code with UseRuleScriptsAsData for that agent, with only the writing tools it may call and a maximum of writes per run; an alert kind exists in code and the agent lists it; a schedule with a subject set registered in code starts the agent per project; the administrator saves the script as a version, runs a dry run on chosen projects that records what it would do without effects, and sets it live or tries it in shadow first, with four eyes where the application requires it; the script reads through tools, raises the alert and proposes the review through the tool's own approval policy; the RuleScripts migration is applied on SQL Server or PostgreSQL and the behaviour is tested on both.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
