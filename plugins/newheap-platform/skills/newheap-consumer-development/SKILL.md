---
name: newheap-consumer-development
description: Bootstrap, inspect, repair or upgrade a repository that consumes NewHeap Platform packages. Use for first-time project setup, choosing a service/API/management-portal foundation, public package access, optional infrastructure, or validating an existing NewHeap foundation. Do not use for ordinary feature implementation in an already configured consumer.
---

# NewHeap consumer foundation

Create the smallest useful NewHeap foundation supported by the confirmed product scope. Keep unrequested capabilities as extension seams rather than installed frameworks.

## Choose the foundation

Infer the profile from the product outcome rather than asking the user to choose frameworks:

- automatic work without screens maps to `service`;
- system-to-system HTTP work maps to `api`;
- confirmed interactive administration maps to `management-portal`.

Select persistence, authentication, Aspire, Docker and Elasticsearch only when the outcome requires them or the repository already establishes them. Before creating files, summarize the proposed result and what remains deferred.

Read only the reference needed for the current setup task:

- [scope gate](references/consumer-scope-gate.md) when the product outcome does not yet determine the foundation;
- [bootstrap sequence](references/consumer-bootstrap-sequence.md) for an empty repository or foundation audit;
- [management portal](references/consumer-management-portal.md) for a confirmed interactive administration interface;
- [optional infrastructure](references/consumer-optional-infrastructure.md) for Aspire, Docker or Elasticsearch;
- [package sources](references/package-sources.md) for installation, restore, source cleanup or coordinated package upgrades.

## Bootstrap or inspect

For an empty repository, run:

```text
node <skill-directory>/scripts/bootstrap-newheap-consumer.mjs <consumer-root> --name <Application.Name> --profile <service|api|management-portal> --database <none|postgresql|sqlserver>
```

Add `--authentication`, `--aspire`, `--docker` or `--elasticsearch` only for confirmed needs. Do not use `--skip-install` as proof of a ready foundation.

For an existing consumer, inventory it before changing foundation or package policy:

```text
node <skill-directory>/scripts/inspect-newheap-consumer.mjs <consumer-root>
```

Package restore and the foundation audit are hard gates. Resolve stale private-feed overrides or public-registry connectivity before feature work.

## Complete the handoff

Run the profile-specific builds and:

```text
node <skill-directory>/scripts/inspect-newheap-consumer.mjs <consumer-root> --mode validate
```

Report the selected profile, enabled capabilities, package versions, checks actually run and anything still unverified. Once the foundation is ready, use the focused NewHeap domain skill discovered for each requested feature.
