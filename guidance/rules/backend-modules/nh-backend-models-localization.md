---
id: nh-backend-models-localization
title: "Model contracts, validation, and localization"
area: backend
reference: backend-models-localization
summary: "Keep read and write contracts separate, make filterability explicit, and provide complete localized validation resources."
sample-cases: ["SPM-009", "SPM-049", "SPM-092", "SPM-093"]
public-symbols: ["FilterableAttribute", "CollectionProcessingOptions", "MultiLanguageIdentityErrorDescriber"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Use a view model for read contracts and a mutate model for input. Mark only allowed filter fields with `Filterable`; for a nested filter such as `owner.email` or `tasks{any}.title`, mark the navigation or collection and each nested view-model member that may be filtered, because collection processing checks every segment of a filter key. Mark sortable fields with `Orderable` the same way: an order key such as `owner.email` needs the navigation and the nested member marked, and a sortable scalar ends the key. Validate input with the existing NewHeap attributes, and register mappings centrally. Keep resource files complete for every supported language and test for missing keys. Use stable dash-case keys for frontend translations inside the object for the relevant module.

`WithIdentity` registers `MultiLanguageIdentityErrorDescriber`, which ships English (neutral) and Dutch (nl-NL) resources for every Identity error message, so password and account errors follow the request culture without application resources. To add or replace a language, add a culture resource for the describer to an application assembly registered in `NhLocalizationOptions` with an `Order` below 99, so it is consulted before the platform's resources; with a higher order the platform's English neutral resource answers first.

## Avoid

- Including audit fields in mutate models.
- Returning a database entity directly as an HTTP contract.
- Implicitly enabling filtering for every field.
- Duplicating hard-coded validation text across controller, service, and frontend.
- Replacing the Identity error describer only to translate its messages into Dutch.

## Verification

Run model, mapping, and resource-completeness tests. Test a valid model and at least one localized validation error through the real HTTP contract. Verify that every Identity error message is Dutch for nl-NL and English for a culture without resources, through the platform registration.
