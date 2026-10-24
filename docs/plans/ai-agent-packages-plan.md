# Agent Packages Plan

This document plans suggestion S7-2 of the agent runs adoption plan for `NewHeap.Platform.AI.Chat`,
`NewHeap.Platform.AI.Chat.AspNet` and `@newheap/platform-ai-chat`. It covers a versioned package format
for one assistant agent and its configuration, an import that goes through the same validators as
saving, and parts that a package locks against local changes. It is a plan, not usage documentation;
the guidance rules, the release notes and the sample cases are authoritative once it is built.

## Status

Decided by Lars on 2026-10-24: PK1 (b), several agents in one package, and PK2 to PK18 as advised. The
decisions are summarized below; the sections under "Choices" keep the options that were weighed. Built on
2026-10-24 in merge train 20 (`feature/agent-packages` with the `AgentPackages` migrations,
`feature/agent-packages-ui`); Lars kept the lanes' own choices PL1 to PL15 and PU1 to PU10 on 2026-10-24.

## Decisions

| # | Decision |
|---|---|
| PK1 | **Several agents in one package**, each with everything that belongs to it as data: the definition, run settings without the technical account, live data skills and triggers, data consultations, the data hand-offs it accepts, metadata entries and group memberships by name; never history, candidates, runs, feedback, run state, shadow overrides, MCP server definitions or secrets. |
| PK2 | **Agents created in the administration and agents in code**: an agent in code travels as an overlay of what data may change on it (data skills, triggers, consultations, hand-offs, `Editable()` metadata overrides, definition overrides); the import requires the same agent in code with `ConfigureAgent`. |
| PK3 | **Own versioned types, read strictly**: `NhAssistantAgentPackage` with `format: "newheap.assistant.agent-package"` and `formatVersion: 1`, mapped to the request DTOs; an unknown property or a newer format version is refused (`assistant-package-format-unsupported`); a SHA-256 of every part's canonical JSON; a JSON schema in the consumer guide; no signature. |
| PK4 | **Names travel and are checked, host-bound ids never travel**: every reference by name is checked with the save validators and refused with their codes; the technical account is named per agent in the import request (`serviceAccountActorId: required` otherwise); secrets never travel. |
| PK5 | **The package's ids**: an agent is created when its id is free and upgraded when it is linked to the same package; any other agent with the id refuses the whole package (`assistant-agent-exists`); trigger ids stay; an agent an upgrade no longer contains stays, unlinked, as `left-behind`. |
| PK6 | **A preview, then one transaction**: `PreviewImportAsync` reports `created`, `changed`, `unchanged`, `kept-local`, `left-behind` or `refused` per part with field errors; `ImportAsync` applies every agent and part in one database transaction with one stamp change and one reload, through a new transactional path over the stores; every part is audited as its save, plus `AdminAgentPackageImported`. |
| PK7 | **The rules of a save, with `apply: "shadow"`**: new skills and triggers start in shadow, a new agent follows `startsInShadow` and `startsOff`, changed skills and triggers become live versions, or candidates with `apply: "shadow"`. |
| PK8 | **Parts and named fields**: `locks` names parts per agent (`agent`, `runSettings`, `skill:{id}`, `trigger:{id}`, `consultation:{type}`, `handOff:{type}`, `metadata:{name}`), each whole (`*`) or with fields from a fixed set per kind. |
| PK9 | **Every save path refuses, operations stay free**: endpoints and services refuse a change of a locked field with `{field}: locked` in the usual validation answer, and removing a locked part or deleting a linked agent with `409 assistant-package-locked`; run state, pauses, the kill switch, shadow mode, the technical account and groups stay free; the administration shows locked fields read-only. |
| PK10 | **Unlink per part or per agent; an upgrade keeps unlinked parts**: `AdminAgentPackageUnlinked`; an unlinked part is `kept-local` on upgrade unless `relink` names it; an unlocked field changed locally is overwritten and shown as "changed locally" in the preview. |
| PK11 | **Link tables**: `AssistantAgentPackageLink` per agent part (package id and version, hash at import, locked fields, linked by and at) and `AssistantAgentPackage` per package (id, version, format version, agents, import time, actor); one migration for both providers; links go with their agent. |
| PK12 | **`INhAssistantAgentPackages` and four endpoints behind the administration policy**: `ExportAsync(agentIds, options)`, `PreviewImportAsync`, `ImportAsync` and `UnlinkAsync`; `POST admin/agent-packages/export`, `POST admin/agent-packages/preview`, `POST admin/agent-packages/import`, `POST admin/agents/{id}/package/unlink`; at most 1 MB and 20 agents; audit kinds `AdminAgentPackageExported`, `AdminAgentPackageImported`, `AdminAgentPackageUnlinked` with ids, versions and hashes only. |
| PK13 | **The exporter sets package id, version and locks; tools may write the JSON too**: the package id defaults to the first agent's id, the version to the next after the linked package's or 1; exporting linked agents keeps their package id. |
| PK14 | **Export, import and lock screens**: an export dialog (agents, package id, version, locks), an import dialog (file or pasted JSON, preview per part, accounts, `apply`, `relink`), locked fields read-only with the package's name and "Unlink" per part and agent; the mock API plays the same rules; English and Dutch. |
| PK15 | **One sample case, SPM-321, on the two desks of SPM-318**: export both desks as one package with the kickoff desk's instructions locked, upgrade in shadow and promote with the hand-off kept, `locked` on a change, unlink, `kept-local` on the next upgrade. |
| PK16 | **Train 20 with two lanes**: PL for the library, the migration, the locks in every save path and the sample's back end; PU for the Angular administration and the mock API, merged after PL. |
| PK17 | **One check of the whole package against the host as it would be after the import**: references between agents of the package resolve, cycles included; references outside it must exist on the host; the transaction writes in dependency order. |
| PK18 | **The agents the exporter chooses, with a warning for references outside**, and the export dialog adds those agents in one click. |

## What S7-2 decided

> A versioned JSON format for an agent with its run settings, skills, triggers, consultations and
> metadata; import goes through the same validators as saving. A package may lock parts, such as a
> skill's instructions; the administration shows them read-only and a save refuses to change them
> (`locked`) until an administrator unlinks the part.

The rules of the adoption plan apply: opt-in, generic, NewHeap stores and enforces only, code and
migrations for SQL Server and PostgreSQL, authorization negatives, a release-notes section, guidance,
an executable sample case, and Angular parts with English and Dutch texts.

## What exists today

- **The parts of an agent and how they are saved.**
  - The agent definition (`AssistantAgent`): one row per agent, `Version` as a concurrency token, no
    history. Agents in code keep their code identity, and only some fields can be overridden.
  - Run settings (`AssistantAgentRunConfiguration`): one row, changed in place.
  - Data skills (`AssistantAgentSkillDefinition`) and data triggers (`AssistantAgentTriggerDefinition`):
    append-only versions with `live`, `candidate`, `superseded` and `discarded`.
  - Data consultations (`AssistantAgentConsultationDefinition`) and data hand-offs
    (`AssistantAgentHandOffDefinition`): one row each, changed in place.
  - Agent metadata (`AssistantAgentMetadata`): one row per entry. For an agent in code only `Editable()`
    entries can be overridden.
  - Groups, MCP server assignments, the technical account, run state, shadow mode and consultation
    routes: operational settings, mostly last-writer-wins.
- **Validation.** Every part has a validator that saving and loading share and that mirrors the startup
  checks of code: `ValidateRunSettings`, `ValidateSkill`, `ValidateTrigger`, `CheckConsultation`,
  `CheckHandOff`, `CheckShadowVariant`, and the metadata entries' own checks. The agent definition's
  checks are split between the endpoint and `NhAssistantAgentAdministration`.
- **Public services.**
  - `INhAssistantAgentConfiguration` creates agents and saves run settings, skills, triggers,
    consultations and metadata. It has no public update of an agent definition.
  - `INhAssistantDataHandOffs` saves hand-offs.
  - The interface's documentation already names "a package import" as a use. SPM-295 and SPM-317 show
    imports done by application code, one call after the other.
- **No unit of work.** Each store call has its own transaction. A multi-part change is a series of
  calls without compensation; the run configuration stamp reloads every host after each.
- **Global trigger ids.** A trigger id belongs to one agent in the whole application.
- **Environment-bound references.** These are names that another host may not have:
  - authorization and agent policies, approval policies, model profiles and rule handlers;
  - trigger events and filters, subject sets, subject types and tool selectors;
  - agent groups, MCP server ids, instruction asset references, other agents (hand-off sources,
    `ShadowOf`), time zones;
  - the technical account's actor id.

  Secrets live only in MCP server definitions.
- **Nothing locked yet.** No `locked` code, no package or provenance columns, no agent export. The only
  lock is a shadow variant's fixed shadow mode.
- **Export precedent.** `GET agents/{id}/evaluation/export` and `feedback/export` stream JSON Lines
  downloads behind the administration policy, and are audited.
- **Tenancy.** The administration and its tables keep no tenant rule (ST14). Packages follow it.

## Choices

Each choice lists the options that were weighed. The advice is marked "(advice)"; Lars followed it for
PK2 to PK18 and chose (b) for PK1.

### PK1. What one package holds

**Decided by Lars (2026-10-24): (b), several agents in one package.** Each agent in it carries what (a)
lists for one agent; PK17 and PK18 settle how the agents refer to each other and which agents an export
takes.

- **(a) One agent and everything that belongs to it as data (my advice, not chosen).** It holds:
  - the definition: display name, description, instructions or asset reference, tool selectors,
    required policy, autonomy, works-on, chat offer, profile and data classification;
  - run settings, without the technical account;
  - data skills, live versions only;
  - data triggers, live versions only;
  - data consultations;
  - the data hand-offs the agent accepts;
  - metadata entries;
  - its group memberships, as names.

  It does not hold history, candidates, runs, feedback, run state, shadow overrides, MCP server
  definitions or secrets. This matches S7-2 and adds hand-offs from S9, so a package describes the
  agent's whole data configuration.
- **(b) Several agents in one package**, such as an agent with its shadow variants or its hand-off
  partners. This is more powerful, but import order, cross references and locks across agents grow
  quickly.
- **(c) Parts only**, such as a skill package or a trigger package, without the agent.

### PK2. Agents in code

- **(a) Both kinds (advice).** For an agent created in the administration, the package carries the
  whole agent. For an agent in code, it carries only what data may change on it: data skills,
  triggers, consultations and hand-offs, overrides of `Editable()` metadata entries, and definition
  overrides. The import requires the same agent in code with `ConfigureAgent`. This is the case
  SPM-295 already shows ("as an import of a skill package would").
- **(b) Only agents created in the administration.** This is simpler, but skills and triggers for an
  agent in code cannot travel.

### PK3. The format

- **(a) Its own versioned types, read strictly (advice).**
  - Public records `NhAssistantAgentPackage` with `format: "newheap.assistant.agent-package"`,
    `formatVersion: 1`, `package` (`id`, `version`, `description`), `agent`, `runSettings`, `skills`,
    `triggers`, `consultations`, `handOffs`, `metadata`, `groups` and `locks`.
  - The types map to the existing request DTOs, so a later change of a DTO never silently changes the
    format.
  - Reading is strict: an unknown property, or a newer `formatVersion`, is refused
    (`assistant-package-format-unsupported`), so nothing is dropped unnoticed.
  - A JSON schema is published in the consumer guide.
  - Every part carries the SHA-256 of its canonical JSON, used for locks and drift.
- **(b) The request DTOs themselves as the format.** This is quicker, but every DTO change becomes a
  format change.
- **(c) As (a), plus a signature** over the package, so a host accepts only packages of a known
  publisher. Not advised now: locks guard against accidental drift, not against administrators, who
  can unlink anyway, and signing needs key management.

### PK4. References that depend on the host

- **(a) Names travel and are checked; host-bound ids never travel (advice).**
  - Policies, profiles, approval policies, rule handlers, events, filters, subject sets, subject types,
    tool selectors, groups, MCP server ids, asset references, time zones and other agent ids travel as
    names. The import checks them with the save validators, and a missing one is refused with the
    validator's own code: for example `assistant-trigger-filter-not-found`, which already exists "so an
    import can tell it apart".
  - The technical account never travels. When the run settings answer to an account, the import
    request names the account on this host; otherwise the import is refused with
    `serviceAccountActorId: required`.
  - Secrets never travel.
- **(b) A mapping table in the import request** that renames any reference (`profiles`, `policies`,
  ...). This is flexible, but every reference kind then needs mapping code and tests.

### PK5. Agent and trigger ids on import

- **(a) The package's ids, created or upgraded (advice).**
  - Every agent keeps its id from the package.
  - An import creates each agent whose id is free. It upgrades each agent that is linked to the same
    package id.
  - Any other agent with one of those ids is refused (`assistant-agent-exists`), and then the whole
    package is refused.
  - Trigger ids stay as they are, so a trigger id owned by an agent outside the package is refused, as
    today.
  - An upgrade that no longer contains an agent the package created earlier leaves that agent in place,
    unlinks it and reports it as `left-behind`. It never deletes an agent.
- **(b) A target agent id chosen at import**, with trigger ids rewritten to
  `{targetAgentId}.{triggerId}`, so one package can create several agents. More flexible, but rewritten
  ids break references in rules and texts, and an upgrade must find them again.

### PK6. Preview and atomic apply

- **(a) A preview, then one transaction (advice).**
  - `PreviewImportAsync` returns per part `created`, `changed`, `unchanged`, `kept-local` (unlinked) or
    `refused`, with field errors.
  - `ImportAsync` applies every agent and part in one database transaction over the stores, with one
    stamp change and one reload. Either the whole package is in effect, or nothing is. With several
    agents, this matters more: a half-imported package can leave hand-offs to agents that do not exist
    yet.
  - Each part is audited as its normal save, plus one `AdminAgentPackageImported` event.
  - This needs a transactional path over the stores, which do not share a unit of work today.
- **(b) Sequential calls through the public services, with compensation** on failure. This reuses
  everything, but a failure halfway leaves a half-imported agent for seconds and compensation is
  fragile.
- **(c) Sequential calls without compensation, with a report.** This is what applications do today.

### PK7. How imported changes take effect

- **(a) The rules of a save, with an option to evaluate first (advice).**
  - New skills and new triggers start in shadow, as every new data skill and trigger does.
  - A new agent follows the package's `startsInShadow` and `startsOff`.
  - Changed skills and triggers become new live versions.
  - The import option `apply: "shadow"` makes them candidates next to the live version instead (S8),
    to compare and promote.
- **(b) Always live**, which is quick but skips evaluation.
- **(c) Always as candidates**, which is safe but every upgrade needs a promote per part.

### PK8. What a package can lock

- **(a) Parts and named fields of a part (advice).**
  - `locks` lists parts (`agent`, `runSettings`, `skill:{id}`, `trigger:{id}`, `consultation:{type}`,
    `handOff:{type}`, `metadata:{name}`).
  - Each part either locks whole (`*`) or locks a fixed set of fields. For example `instructions`,
    `tools` and `description` of a skill; `instructions`, `toolSelectors` and `autonomy` of the agent.
- **(b) Whole parts only.** Simpler, but "a skill's instructions", which S7-2 names as its example,
  would lock the whole skill.
- **(c) The whole package.**

### PK9. How a lock is enforced

- **(a) Every save path refuses, and operations stay free (advice).**
  - The endpoints, `INhAssistantAgentConfiguration`, `INhAssistantDataHandOffs`, metadata saves,
    agent updates and resets, and candidate promotes all refuse a change of a locked field with the
    field error `{field}: locked` in the usual validation answer.
  - Removing a locked part or deleting a linked agent is refused with
    `409 assistant-package-locked` until it is unlinked.
  - Run state, pause, kill switch, shadow mode, the technical account and groups stay free. They are
    operations, not content.
  - The administration shows locked fields read-only.
- **(b) Locks only in the administration screens.** Not advised: applications and services could still
  change locked content.

### PK10. Unlinking and a later upgrade

- **(a) Unlink per part or per agent; an upgrade keeps unlinked parts and says so (advice).**
  - An administrator unlinks one part or the whole agent. This is audited as
    `AdminAgentPackageUnlinked`.
  - An unlinked part is local. An upgrade leaves it as it is and reports it as `kept-local`. The import
    option `relink` takes the package's version again for parts the administrator names.
  - An unlocked field that someone changed locally is overwritten by an upgrade, and the preview shows
    it as `changed` with "changed locally".
- **(b) An upgrade overwrites unlinked parts too.** Simple, but unlinking would only last until the
  next upgrade.

### PK11. What is stored

- **(a) A package link per part (advice).**
  - New table `AssistantAgentPackageLink`, keyed on agent, part kind and part key. It records package
    id and version, the hash at import, the locked fields, and who linked it and when.
  - New table `AssistantAgentPackage` per package: package id, version, format version, its agents,
    import time and actor.
  - The hash at import lets the administration show "changed locally" for unlocked fields.
  - One migration for both providers. Removing an agent removes its links.
- **(b) Columns on every part's table.** No new table, but six tables change and parts without
  versions would carry package columns.

### PK12. The API

- **(a) One public service and four endpoints behind the administration policy (advice).**
  - `INhAssistantAgentPackages` with `ExportAsync(agentIds, options)`, `PreviewImportAsync(package,
    options)`, `ImportAsync(package, options, actorId)` and `UnlinkAsync(agentId, part?, reason,
    actorId)`.
  - `POST admin/agent-packages/export` takes the agent ids and the options, and downloads the package as
    JSON (`agent-package-{packageId}.json`).
  - `POST admin/agent-packages/preview` and `POST admin/agent-packages/import` take the package and the
    options.
  - `POST admin/agents/{id}/package/unlink` unlinks.
  - A package is at most 1 MB and 20 agents. The import options are a technical account per agent
    that needs one, `apply` and `relink`.
  - Audit kinds `AdminAgentPackageExported`, `AdminAgentPackageImported` and
    `AdminAgentPackageUnlinked` record ids, versions and hashes, never content.
- **(b) Members on `INhAssistantAgentConfiguration`.** Not advised: implementations of that interface
  would break (HD8 chose the same for hand-offs).

### PK13. Who decides the locks, and the package's identity

- **(a) The exporter sets them; the format can be written by tools too (advice).**
  - The export request names the package id (by default the first agent's id), the version (by default
    the next one after the linked package's, otherwise 1) and the locks.
  - An application or a build tool can write the same JSON itself, for example to ship packages with a
    deploy.
  - Exporting a linked agent keeps its package id.
- **(b) Locks only in packages written by tools**, with an export that never locks.

### PK14. The administration

- **(a) Export, import and lock screens in the agent administration (advice).**
  - **Export:** an export dialog that starts from an agent (which agents, package id, version, which
    parts and fields to lock).
  - **Import:** an import dialog for a file or pasted JSON. It shows the preview table per part with
    codes, the account to answer to, `apply` and `relink`.
  - **Locks:** locked fields show read-only with a lock and the package's name, with an "Unlink" action
    per part and per agent.
  - **Mock API:** it plays the same rules.
  - English and Dutch texts.
- **(b) Endpoints and the service only, without screens.** Not advised: S7-2 asks the administration
  to show locked parts read-only.

### PK15. The sample

- **(a) One case on the two desks of SPM-318 (advice).** SPM-321:
  - the operator exports the intake desk and the kickoff desk, which hands work between them, as one
    package with the kickoff desk's instructions locked;
  - a changed copy is imported as an upgrade in shadow and promoted, and the hand-off between them stays
    in effect;
  - an administrator's change of the locked instructions is refused with `locked`;
  - after unlinking, the instructions can change and the next upgrade keeps them as `kept-local`.

  Library tests cover both providers, every refusal and the atomic rollback.
- **(b) Two cases**, as (a) plus one for the overlay on an agent in code (SPM-295's planner).

### PK16. Delivery

- **(a) Train 20 with two lanes (advice).**
  - **Lane PL:** the library, the migration, the locks in every save path, and the sample case's back
    end.
  - **Lane PU:** the Angular administration and the mock API, against the contract this plan fixes.
  - PU merges after PL, or rebases on it.
- **(b) One lane**, simpler but slower.

### PK17. References between agents in one package

- **(a) One check of the whole package against the host as it would be after the import (advice).**
  - The import builds the configuration the host would have with the package applied: the package's
    agents and parts on top of what the host has.
  - It checks every part with the save validators against that configuration, so a hand-off source, a
    `ShadowOf`, a consultation asker or a skill a trigger names may be another agent or part of the same
    package.
  - A reference to an agent outside the package must exist on the host, as in PK4.
  - The transaction writes in dependency order: agents, then run settings, skills, triggers,
    consultations, hand-offs and variants.
- **(b) Agents checked one by one in an order the package gives**, each against the host plus the agents
  before it. Simpler checks, but a cycle, such as two agents that hand work to each other, cannot be
  imported, and the package author must get the order right.

### PK18. Which agents an export takes

- **(a) The agents the exporter chooses, with a warning for what lies outside (advice).**
  - The export takes exactly the chosen agents.
  - It warns about every reference to an agent outside the selection: hand-off sources, `ShadowOf` of a
    variant, consultation askers. That agent must then exist on the target host.
  - The export dialog offers to add those agents with one click.
- **(b) The chosen agents plus everything they reference, automatically.** Complete, but one agent can
  pull in a large part of the host.

## Evidence to build

- Library tests on SQL Server and PostgreSQL:
  - export and import round-trip to the same configuration;
  - preview codes per part;
  - an atomic rollback when one part is refused;
  - missing references refused with the validators' codes;
  - the technical account required and checked;
  - an upgrade with `apply: shadow` making candidates;
  - every save path refusing a locked field;
  - unlink, and `kept-local` and `relink` on upgrade;
  - code-agent overlays;
  - strict format reading.
- Authorization negatives: no administration policy, and an import that names an account that cannot
  sign in.
- A sample case with focused evidence, guidance rules (agent runs administration, packages), the consumer
  guide via regeneration, and release notes per release unit.

## Lane choices

| # | Choice |
|---|---|
| PL1 | Built by lane PL (2026-10-24): **the package lists its agents under `agents`, each with `id`, `source` (`admin` or `code`) and its definition under `definition`**, with the part key `agent` for it; PK3's single-agent fields move into each agent, kept by Lars (2026-10-24). |
| PL2 | Built by lane PL (2026-10-24): **strict reading uses a source-generated context that refuses unknown and missing properties, case differences and numbers as strings**; the HTTP preview and import take the package as raw JSON next to the options, so only the package is read strictly, kept by Lars (2026-10-24). |
| PL3 | Built by lane PL (2026-10-24): **a part's hash is the SHA-256 of its JSON without nulls, properties ordered by name and no white space**; the export writes `hashes` per agent, an import checks a hash that is given and accepts a package without them, and a link stores the hash of the part as the host has it after the import, kept by Lars (2026-10-24). |
| PL4 | Built by lane PL (2026-10-24): **a lock stores the package's value of every locked field, and a save may keep the value it has or take the package's**, so promoting a candidate an import made is allowed while another value is refused with `locked`; instructions are compared by hash or asset, never by text, kept by Lars (2026-10-24). |
| PL5 | Built by lane PL (2026-10-24): **a refused import answers `409 assistant-package-refused` with the plain field error `parts: invalid`**, because the client's field-name pattern refuses `{agentId}/{part}/{field}`; why each part was refused is in the preview's part outcomes, which the import dialog shows. This replaces the per-part field names of the contract commit, kept by Lars (2026-10-24). |
| PL6 | Built by lane PL (2026-10-24): **the export's warnings stay in the header `X-NewHeap-Package-Warnings`, so the download remains the package itself**; a host that serves the administration from another origin exposes it with `UseCors(cors => cors.WithExposedHeaders(NhAssistantAgentPackageRoutes.WarningsHeader))`, as the guidance says, and NewHeap.Platform.AspNet.Common keeps its default exposed headers, kept by Lars (2026-10-24). |
| PL7 | Built by lane PL (2026-10-24): **the first import of a package may take over agents a host already has when `relink` names them as a whole**, so the sample links the desks it created by hand; without it an agent of the same id refuses the package (`assistant-agent-exists`), kept by Lars (2026-10-24). |
| PL8 | Built by lane PL (2026-10-24): **an agent that is not yet in the package takes the package's version of the parts it already has (an overlay of an agent in code, or a relinked agent)**, while a member agent's part without a link stays `kept-local`, kept by Lars (2026-10-24). |
| PL9 | Built by lane PL (2026-10-24): **a part an earlier version linked and the new version no longer contains is `left-behind` like an agent**: it stays, unlinked, and is audited as `AdminAgentPackageUnlinked` with `left-behind`, kept by Lars (2026-10-24). |
| PL10 | Built by lane PL (2026-10-24): **groups are set only when the import creates an agent**; an upgrade and an overlay keep the host's groups, because groups stay free (PK9), kept by Lars (2026-10-24). |
| PL11 | Built by lane PL (2026-10-24): **an upgrade that names no technical account keeps the one the host's run settings have; another account named for an upgrade is a change of the run settings**, kept by Lars (2026-10-24). |
| PL12 | Built by lane PL (2026-10-24): **the transactional path is a set of static store methods that take the caller's context, which the normal saves call inside their own transaction**, and `NhAssistantAgentPackageStore.ImportAsync` runs them, replaces the links and changes the stamp once; audits and the one reload follow the commit, as after a save, kept by Lars (2026-10-24). |
| PL13 | Built by lane PL (2026-10-24): **a package is never imported at a lower version than the host has (`package.version: invalid`)**; the same version again is allowed and changes nothing, kept by Lars (2026-10-24). |
| PL14 | Built by lane PL (2026-10-24): **`ExportAsync` takes the actor as a parameter for its audit, and the interface has `GetLinkAsync(agentId)` next to the four members of PK12**, kept by Lars (2026-10-24). |
| PL15 | Built by lane PL (2026-10-24): **a package of more than 20 agents answers `413 assistant-package-too-large` like one above 1 MB, and the import options must name only agents of the package**, kept by Lars (2026-10-24). |
| PU1 | Built by lane PU (2026-10-24): **the export dialog reads the parts and references from the administration's reads, and only "Download package" exports**: the parts of each chosen agent come from `getAgentConfiguration`, `getAgentHandOffs` and `getAgentMetadata`, the references to agents outside the selection (hand-off sources, consultation askers, the live agent of a variant and its variants) from the same reads and the agent list, so choosing agents and locks changes nothing and writes no `AdminAgentPackageExported`; the download sends the chosen locks, saves the server's JSON as `agent-package-{id}.json` and shows the references the server reported. Alternative (b): an export without locks on every change of the selection, for the server's parts and warnings; not chosen, because every change would be an audited export, kept by Lars (2026-10-24). |
| PU2 | Built by lane PU (2026-10-24): **`exportAgentPackage` answers `AgentPackageExport` with the package, its JSON exactly as the server wrote it and the warnings of the header `X-NewHeap-Package-Warnings`**, through an internal transport request for a POST that answers a file with its headers; only warnings with a safe code and string ids are kept. A cross-origin host must expose that header (`Access-Control-Expose-Headers`), or the dialog shows only the references it read itself. Alternative (b): a `Promise<Blob>` as the other downloads; not chosen, because the dialog needs the package and the warnings, kept by Lars (2026-10-24). |
| PU3 | Built by lane PU (2026-10-24): **the import dialog checks again after `assistant-package-refused` and offers to take over an existing agent**: the refusal carries only the plain field error `parts: invalid` (PL5), so the dialog shows the code and the per-part outcomes of a new preview, with each refused part's code and field errors; the mock answers the refusal the same way. For `assistant-agent-exists` of an agent created here that belongs to no package, the dialog says that the agent exists on this host and is not linked to the package, and offers "Take over the existing agent", a `relink` naming the whole agent (`{ agentId, part: null }`, PL7), or removing it first; an agent of another package must be unlinked from it first, and one in code cannot be replaced. The options keep only the accounts and relinks of the package's agents, as the server refuses others. Alternative (b): per-part field names such as `{agentId}/{part}/{field}` in the refusal, with a wider field-name pattern in the transport; not chosen, because the pattern is compatibility-sensitive and the preview already carries every refusal per part, kept by Lars (2026-10-24). |
| PU4 | Built by lane PU (2026-10-24): **a locked field is disabled, with a lock next to its label that names the package and version**: native `disabled` on the control, or a `fieldset` around controls that lock together (instructions with their asset choice, accepting files with the file limits, the routes with the answer-or-propose choice); the forms of a linked part show the package notice without "Unlink", the lists of the run configuration and the hand-offs show it with "Unlink" and an optional reason, and removing a part with locks is disabled with the reason. Alternative (b): `readonly` text inputs that stay focusable; not chosen, because selects, radios, check boxes and the pickers have no read-only state, kept by Lars (2026-10-24). |
| PU5 | Built by lane PU (2026-10-24): **the agent's package on its card, the definition's lock in the editor**: the card shows a "Package {id} {version}" badge and the notice with "Unlink the agent" (all parts); the editor shows the part `agent` with "Unlink"; deleting a linked agent is disabled, and so is resetting an agent in code while its package locks fields of its definition, although the server refuses a reset only when it would change a locked field. Alternative (b): offer the reset and show the server's `409 assistant-package-locked`; not chosen, because a reset of a locked definition almost always changes a locked field, kept by Lars (2026-10-24). |
| PU6 | Built by lane PU (2026-10-24): **the mock applies a package through its own endpoints on a copy of its stores**: every store of the mock administration can copy and restore its state; a preview applies every part in dependency order (agents, run settings, skills, triggers, consultations, hand-offs, metadata; shadow variants after their live agents) and restores, an import writes only the parts the check found new or changed and restores everything on a refusal, and `changed` or `unchanged` compares the host's part before and after the check's write. Alternative (b): rules of their own in the mock's preview; not chosen, because they would drift from the validators of the save paths, kept by Lars (2026-10-24). |
| PU7 | Built by lane PU (2026-10-24): **the mock's locks are the server's**: a save may keep a locked field's value or take the package's, never another (`{field}: locked`, the change undone), so promoting a candidate an import made is allowed; removing a part with locks and deleting a linked agent answer `409 assistant-package-locked`, as does a reset that would change a locked field; a linked part without locks may be removed and leaves the package. Alternative (b): refuse every change of a locked field; not chosen, because a package's own candidate could then never be promoted, kept by Lars (2026-10-24). |
| PU8 | Built by lane PU (2026-10-24): **the mock hashes with SHA-256 but does not check given hashes**: exports carry the SHA-256 of each part's canonical JSON (properties ordered, `null` left out), computed synchronously so an import can hash between its writes, and change detection uses the same hashes; a hash in an imported package is not compared. Alternative (b): refuse a package whose given hash differs; not chosen, because a package of a real server may canonicalize a part slightly differently and the mock would then refuse it, kept by Lars (2026-10-24). |
| PU9 | Built by lane PU (2026-10-24): **one new icon, `lock`**: the export and import actions use the existing `file` icon; the release notes name `lock` for complete icon maps and the sample maps it to its icon library. Alternative (b): also `download` and `upload`; not chosen, because each name breaks complete icon maps, kept by Lars (2026-10-24). |
| PU10 | Built by lane PU (2026-10-24): **the sample's playground starts with an imported package**: `admin.agentPackages` of the mock scenario imports a package of a project intake desk and a project kickoff desk with a hand-off between them, the intake desk's tools and the kickoff desk's instructions and hand-off locked, so the administration shows linked agents and locked fields without a sample case id of its own. Alternative (b): an empty playground where the operator imports a package first; not chosen, because the locks would not show until then, kept by Lars (2026-10-24). |
