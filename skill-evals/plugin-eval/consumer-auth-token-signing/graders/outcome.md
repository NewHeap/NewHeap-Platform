---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Let our planning service validate the application's access tokens and the assistant's delegated MCP tokens without giving it the signing key, and rotate that key every year without signing anybody out.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

Signing with ES256 or RS256 through NhJwtSigningKeys with the private key and key id in the secrets file, MapNewHeapJwks on the application, UseNewHeapJwks with the issuer and the service's own origin as audiences on the planning service, rotation by moving the old public key into PreviousKeys until its tokens expired, AcceptSharedKeyTokens only while moving from HS256, and delegated tokens whose aud is the service.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
