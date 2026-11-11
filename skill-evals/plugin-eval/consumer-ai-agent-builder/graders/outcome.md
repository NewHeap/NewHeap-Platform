---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Give administrators an assistant in the administration that helps them build an agent from a description in plain language, including its instructions, triggers, technical account, gateway reads and a rule script, and that knows the application's data, but never handles MCP credentials or other secrets.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

The backend calls AddAgentBuilder with a model profile and gives it guidance of the application's own with UseGuidance and a trusted text asset; only people who pass the assistant's access policy and its administration policy can chat with the builder; the builder reads the catalog, the agent with its blockers, the accounts and the API gateway's resources with the administrator's rights, and every change, also of the accountability, the technical account and gateway reads, and every removal is a proposal the administrator approves in the chat on a card that says so plainly, acting with the administrator's own rights through the administration services, so ceilings, versions, audits and four eyes still apply; it never takes or returns MCP credentials or other secrets, connecting MCP servers stays in the screens, and it never goes beyond the code's ceilings; a discovery policy that allow-lists tools lets the builder's tool prefix through; the administration shows the builder on the agent page and for a new agent.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
