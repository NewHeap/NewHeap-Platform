---
id: nh-backend-models-localization
title: "Model contracts, validation, and localization"
area: backend
reference: backend-models-localization
summary: "Keep read and write contracts separate, make filterability explicit, and provide complete localized validation resources."
sample-cases: ["SPM-009", "SPM-049", "SPM-092", "SPM-093"]
public-symbols: ["FilterableAttribute", "CollectionProcessingOptions"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Use a view model for read contracts and a mutate model for input. Mark only allowed filter fields with `Filterable`, validate input with the existing NewHeap attributes, and register mappings centrally. Keep resource files complete for every supported language and test for missing keys. Use stable dash-case keys for frontend translations inside the object for the relevant module.

## Avoid

- Including audit fields in mutate models.
- Returning a database entity directly as an HTTP contract.
- Implicitly enabling filtering for every field.
- Duplicating hard-coded validation text across controller, service, and frontend.

## Verification

Run model, mapping, and resource-completeness tests. Test a valid model and at least one localized validation error through the real HTTP contract.
