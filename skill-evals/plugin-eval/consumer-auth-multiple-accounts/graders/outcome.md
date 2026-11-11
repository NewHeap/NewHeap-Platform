---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Let people stay signed in to their work and their private account in the same browser and switch between them, and stop two open tabs from signing each other out when they renew the session.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

An explicit authentication.multipleAccounts opt-in, renewals through renewSession instead of authenticateRefreshToken, switchAccount and logoutAccount for one account, logout() for the active one, no identity decision from the hint cookie, and the default single-account behavior left unchanged for applications that do not opt in.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
