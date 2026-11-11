---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Add Microsoft sign-in for our tenant to an application whose Angular front end calls the API through an /api proxy, keep existing accounts working, and cover it with tests.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

AddMicrosoftOAuth with the tenant in AllowedTenantIds, the callback registered behind the /api proxy so the correlation cookie reaches it, return URLs limited by NhReturnUrls, accounts linked by tenant and object id after one mail link, and provider tests with NhMicrosoftOAuthTestPlatform instead of Microsoft.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
