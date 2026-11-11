---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-database-development; guidance rules nh-read-only-database-diagnostics; workspace management-portal."
tags: ["database"]
expected_outcome: "The agent creates a dedicated login or role that can only connect and select approved tables, views and columns, granted per object rather than through db_datareader, database-level or schema-level SELECT, pg_read_all_data or GRANT SELECT ON ALL TABLES. The grants leave out the refresh tokens (NhUserAuthRefreshToken), AspNetUserTokens and NotificationDeliveries, and grant AspNetUsers only by column without PasswordHash and SecurityStamp. The principal holds no write, DDL, function, procedure, sequence USAGE or UPDATE, impersonation, server-state or predefined monitoring or signalling permission, and the agent explains that principal verification accepts a database-wide read grant, so excluding the credential-bearing tables is the consumer's responsibility."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Create the read-only database principal that agents use for newheap-db diagnostics on our NewHeap application database, so they can investigate users, divisions and notifications.
