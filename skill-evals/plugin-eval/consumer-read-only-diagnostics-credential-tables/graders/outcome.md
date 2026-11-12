---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Create the read-only database principal that agents use for newheap-db diagnostics on our NewHeap application database, so they can investigate users, divisions and notifications.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The agent creates a dedicated login or role that can only connect and select approved tables, views and columns, granted per object rather than through db_datareader, database-level or schema-level SELECT, pg_read_all_data or GRANT SELECT ON ALL TABLES. The grants leave out the refresh tokens (NhUserAuthRefreshToken), AspNetUserTokens and NotificationDeliveries, and grant AspNetUsers only by column without PasswordHash and SecurityStamp. The principal holds no write, DDL, function, procedure, sequence USAGE or UPDATE, impersonation, server-state or predefined monitoring or signalling permission, and the agent explains that principal verification accepts a database-wide read grant, so excluding the credential-bearing tables is the consumer's responsibility.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
