---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
type: llm
focus: last_message
weight: 1
---

The user gave an AI coding agent this request:

<request>
Require two-step verification for every finance administrator, let them enroll during their next sign-in, offer e-mail codes to other users, remember trusted devices and let support reset a user who lost their phone.
</request>

The agent worked without a person to answer questions. Its starting repository: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
When it could not run a command, a command it states with its exact arguments counts as run; do not fail it only for missing command output.

Expected outcome, to be treated as a checklist:

RequireFor with the finance role, an enrollment step handled with the enrollment endpoints or NhTwoFactorEnrollmentComponent, EnableEmailCodes that does not satisfy the role requirement, EnableRememberDevice, UseSecurityNotifications with WithNotifications, UseAdministrationPolicy with AddTwoFactorOperations for reset, reminders and session revocation, and relational tests for enrollment, refresh refusal, e-mail code reuse and remembered devices.

PASS if the final response shows completed work or a concrete plan that satisfies every element of the expected outcome and contradicts none of them.
FAIL if any element is missing, contradicted or only mentioned as a vague intention.
Judge the substance, not the formatting or wording.
