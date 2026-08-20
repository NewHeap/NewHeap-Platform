---
id: nh-backend-module-composition
title: "Wire a backend module end to end"
area: backend
reference: backend-module-composition
summary: "Build a consumer module as a complete vertical slice so contracts, entity, DbContext, repository, service, mappings, DI, and HTTP composition remain demonstrably in sync."
sample-cases: ["SPM-001", "SPM-009", "SPM-011", "SPM-013"]
public-symbols: ["BaseDbEntityService", "DbEntityProtectedNhBaseController", "FilterableAttribute", "NewHeapAspNetCommonOptionsBuilder"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Start from the consumer's existing pattern and deliver the module as one complete change. Define a view model and a separate mutate model, mark the view model `Id` with `Filterable`, add AutoMapper mappings, and route create, update, and delete operations through the concrete service. Register a `DbSet`, relationships, repository, and service in the composition root. Keep normalization, validation, and query composition in the service; the controller only translates HTTP into a typed service contract.

A mutate model does not contain `CreationDateTime` or `LastModifiedDateTime`. Database entities and migrations remain owned by the consumer implementation. For a schema change, verify that the migration is generated in that consumer's database project.

Register consumer AutoMapper profiles through `NewHeapAspNetCommonOptionsBuilder.ConfigureAutoMapper`. The NewHeap-managed mapper applies a recursion-depth guard to maps without an explicit limit while preserving explicit consumer limits. If an application constructs an independent `MapperConfiguration`, that mapper is outside the NewHeap registration boundary and needs its own tested depth guard.

## Avoid

- A documentation-only example without an executable path.
- Business logic, normalization, or transaction ownership in the controller.
- Consumer entities or consumer migrations in a reusable NewHeap library.
- Assuming a repository or mapping exists without proving its registration.
- Constructing an independent mapper for recursive input without a maximum-depth convention and a circular-map audit.

## Verification

Build the backend, run the service and controller tests, and inspect the OpenAPI output. Verify that every circular AutoMapper type map has a non-zero maximum depth. For a schema change, verify the real relational providers, not only EF Core InMemory.
