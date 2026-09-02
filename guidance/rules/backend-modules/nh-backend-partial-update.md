---
id: nh-backend-partial-update
title: "Top-level JSON partial updates"
area: backend
reference: backend-partial-update
summary: "Send and map a top-level JSON partial update while preserving omitted values and the regular service validation, transaction, logging, and save pipeline."
sample-cases: ["SPM-037", "SPM-038"]
public-symbols: ["patch", "patchResult", "updatePartial", "DoUpdatePartial", "TryApplyPartialUpdate", "CanPartiallyUpdateProperty", "UpdatePartialAsync", "PreparePartialUpdateMutateModelAsync", "NhSetPropertyCalls"]
skills: ["newheap-backend-development"]
providers: ["frontend", "provider-neutral"]
risk: medium
---
## Preferred approach

Accept a `JObject` in a thin `HttpPatch` action and delegate it to the protected `DoUpdatePartial` base-controller method. Let the mutate model define the default writable surface and override `CanPartiallyUpdateProperty` when an endpoint must expose only a subset. Missing properties remain unchanged, explicit `null` clears nullable properties, and falsy values such as `false` and `0` remain real updates. JSON names and both serializer-level and property-level Newtonsoft converters are honored.

For a custom controller route whose domain service expects a complete model, load or map a detached mutate model first and call the protected `TryApplyPartialUpdate` helper inherited from `NhBaseController`. Return the populated `ModelState` when it returns `false`, otherwise pass the validated complete model to the custom service workflow. Supply a property predicate when that route exposes only part of the mutate model. The helper uses the same serializer, property resolution, converter, allow-list and mapping-error engine as `DoUpdatePartial`; it never applies any property when document mapping fails.

In Angular consumers derived from `NhBaseApiService`, call `updatePartial<TResponse>(id, partialObject)` for the standard entity route. Use `NhApiService.patch` directly for a custom route and `patchResult` only when the consumer deliberately uses the `TaskResult` convenience style. Send only the selected top-level properties; do not construct a complete mutate model with placeholder values. A standard `DoUpdatePartial` endpoint returns `204 No Content`, so update known local state after success or reload the resource when the server may normalize values that the UI cannot predict.

Keep domain orchestration in the concrete service. `DoUpdatePartial` calls the virtual `UpdatePartialAsync` service method, so override that method when the application must own a transaction or publish an event. Override `PreparePartialUpdateMutateModelAsync` for normalization that must run after the selected setters are applied and before validation. The existing service maps the entity to a complete mutate model and then reuses normal update validation, mapping, logging, and persistence.

This contract is a top-level partial JSON object. Treat a supplied object or collection property as a complete replacement. Use the `application/merge-patch+json` media type only when the application implements recursive JSON Merge Patch semantics.

## Avoid

- Adding `HasProperty` flags and a manual token-type branch for every mutate-model property.
- Silently ignoring unknown, duplicate, read-only, ignored, or forbidden properties.
- Binding directly to a regular mutate model and losing the distinction between an absent property and an explicit default value.
- Serializing an existing model to JSON, overlaying patch properties and deserializing it again. Use `TryApplyPartialUpdate` on an isolated mutate model.
- Passing a tracked entity to `TryApplyPartialUpdate`. Model-validation failures retain the attempted values on the target, so the target must be safe to discard when validation fails.
- Calling the full-update `update`/PUT helper when the client intends to change only selected properties.
- Expecting a response model from the standard `204 No Content` partial-update endpoint.
- Calling this JSON Patch: `JsonPatchDocument<T>` uses an operation array with `add`, `remove`, and `replace` semantics.
- Assuming an override of `UpdateAsync` also wraps `UpdatePartialAsync`; override the partial method when it has its own domain workflow.
- Normalizing through `beforeSave`; validation has already completed by then. Use `PreparePartialUpdateMutateModelAsync` instead.

## Verification

Test that the Angular client uses HTTP PATCH, preserves request headers and query options, sends only selected properties, and handles `204 No Content`. Also test single-field and multi-field updates, explicit `null`, falsy values, configured JSON names, serializer-level and property-level converters, an empty no-op document, unknown and forbidden properties, invalid values, pre-validation normalization, and service errors. For custom workflows, verify that `TryApplyPartialUpdate` preserves omitted values, validates the complete model and applies nothing when document mapping fails. Verify that an invalid document never calls the service and that regular, composite and custom controller routes share the partial-update engine.
