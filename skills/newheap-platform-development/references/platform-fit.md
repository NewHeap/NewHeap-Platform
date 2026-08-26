# NewHeap platform fit

Use this reference for a non-trivial consumer change when NewHeap may already own a reusable or cross-cutting capability, when an available package or guidance version could change the implementation, or when a consumer workaround may reveal a reusable library gap. The goal is a deliberate ownership decision, not maximum Platform adoption.

## Discover current evidence

Do not choose from memory or from a package name alone.

1. Inspect the consumer's actual state: `newheap-consumer.json`, central package versions and project references, frontend manifests and lockfiles, and the pinned `.agents/skills/newheap-platform-development/.newheap-platform-install.json` when present.
2. When a NewHeap checkout is available, inspect `skills/skill-manifest.json` for the released package and guidance matrix, `release/manifest.json` for package ownership and source paths, and the relevant current source, tests, executable SampleProjectManagement evidence and recent focused Git history.
3. Distinguish released capability from unreleased source. A type in the current checkout is not consumer-ready until the consumer can resolve a compatible published package. Confirm public registry availability when adding or upgrading a dependency.
4. Establish checkout freshness when it matters. Inspect the branch, commit and working-tree state; compare with the remote or public registry without pulling, merging or disturbing local changes. State when freshness or registry availability could not be verified.
5. Compare behavior and constraints, not just names. Check authorization, tenancy, provider portability, lifecycle, observability, operational and compatibility requirements against the consumer's need.

For AI or application-question work, treat chat as only one possible surface. Inventory at least Agent Framework, MCP adapters and imports, ASP.NET invocation context, generated or governed tools, protected actions, usage/audit and evaluation, retrieval and ingestion, durable runs, and the read-only database tool. Then inspect the current manifests and skill references for additions instead of treating this list as complete. These are candidate seams, not a mandatory dependency set.

Load the foundation module's package-sources reference as well when installation, restore, source cleanup or a coordinated package upgrade is in scope.

## Decide the owner

Prefer an existing NewHeap capability when its public, released contract fits the requirement, preserves the consumer's security and provider constraints, and removes meaningful duplicated infrastructure.

Keep the implementation consumer-owned when the behavior expresses application domain rules, tenant policy, workflows, entities or persistence, or when the existing consumer code is simpler and safer than the available abstraction. Do not refactor correct code or add a dependency solely to increase NewHeap usage. Do not introduce a speculative abstraction for one call site.

Upgrade a package only for a relevant capability, defect fix, compatibility need or supported migration. Verify the affected release unit, public availability, transitive compatibility and focused consumer tests. A newer version by itself is not a reason to upgrade.

When the Platform is a partial fit, use the smallest stable public seam and keep application policy outside it. Do not copy unreleased Platform internals into the consumer to simulate availability.

## Suggest a Platform improvement

Surface a separate **NewHeap library suggestion** when evidence shows a reusable gap, even when the current task remains consumer-owned. A useful suggestion contains:

- the concrete consumer pain and evidence locations;
- why the concern is reusable rather than application policy;
- the smallest proposed package, contract or extension seam;
- compatibility, authorization, provider and migration risks;
- the executable sample and focused tests that would prove it.

Prefer a suggestion over an upstream change when the pattern has only one consumer, the public boundary is still uncertain, or the current task does not authorize Platform maintenance. Do not edit the NewHeap repository, bump packages or broaden the consumer task without explicit scope; use `newheap-library-maintenance` when an upstream change is requested.

## Record the decision

For a consequential architecture or dependency choice, include a short **NewHeap fit decision** in the handoff. State the evidence checked and one or more outcomes: reused an existing capability, upgraded a verified package, kept the concern consumer-owned, found no applicable released capability, or recorded a library suggestion. Also state any freshness, registry or compatibility evidence that remains unverified.
