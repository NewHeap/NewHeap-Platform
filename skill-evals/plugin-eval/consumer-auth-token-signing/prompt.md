---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-authentication; guidance rules nh-auth-token-signing; workspace management-portal."
tags: ["authentication"]
expected_outcome: "Signing with ES256 or RS256 through NhJwtSigningKeys with the private key and key id in the secrets file, MapNewHeapJwks on the application, UseNewHeapJwks with the issuer and the service's own origin as audiences on the planning service, rotation by moving the old public key into PreviousKeys until its tokens expired, AcceptSharedKeyTokens only while moving from HS256, and delegated tokens whose aud is the service."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Let our planning service validate the application's access tokens and the assistant's delegated MCP tokens without giving it the signing key, and rotate that key every year without signing anybody out.
