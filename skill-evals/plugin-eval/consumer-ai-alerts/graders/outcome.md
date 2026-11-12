---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Every working-day morning, warn the person responsible for each task that is due today and not reported done, without asking anyone to approve anything, and stop warning once the task is done.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The backend declares an alert kind in code with AddAlertKind, with a display name key, an urgency, a title per culture from named parameters, the subject's responsible person as recipient, a RepeatAfter and a morning digest as default delivery; an agent with a rule handler lists the kind with Alerts(...) and a scheduled trigger starts it on the tasks due today; the rules raise one alert per task and due date with WithAlerts and resolve it with ResolveAlerts once the task is done, relying on one open alert per kind, subject and key, the recipient check and the expiry; the application's own code uses INhAssistantAlerts; the Alerts migration is applied on SQL Server or PostgreSQL and the behaviour is tested on both.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
