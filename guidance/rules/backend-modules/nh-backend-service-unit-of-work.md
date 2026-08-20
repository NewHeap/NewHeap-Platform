---
id: nh-backend-service-unit-of-work
title: "Service-owned unit of work"
area: backend
reference: backend-unit-of-work
summary: "Let the concrete service own the transactional boundary, business rules, and event order while nested operations join the existing scope."
sample-cases: ["SPM-054", "SPM-190", "SPM-191", "SPM-192", "SPM-193", "SPM-194", "SPM-196", "SPM-197", "SPM-198", "SPM-200"]
public-symbols: ["StartOrGetTransactionScopeAsync", "UpdatePartialAsync", "INhDbTransactionScope"]
skills: ["newheap-backend-development", "newheap-background-processing"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Open the unit of work in the concrete service with `StartOrGetTransactionScopeAsync`. Let nested NewHeap operations reuse the same scope and allow only the owner to commit it. Publish transactional events before the commit so the outbox and domain change succeed or roll back together. Make partial and bulk behavior explicit, and report partial results only when that is the agreed contract.

Use provider-translatable LINQ and maintain transactional tests for SQL Server and PostgreSQL. With CAP, the publisher belongs in the same scope; a publish failure must not leave a committed domain change.

## Avoid

- A controller that opens or commits a domain transaction.
- A nested service committing a transactional scope it does not own.
- Publishing events only after commit when atomic outbox semantics are required.
- Using InMemory as evidence for rollback, constraints, isolation, or outbox behavior.

## Verification

Test success, a business failure, a publish failure, a nested operation, and rollback on both relational providers. Verify that exactly one owner commits and that no partial data or broker message remains after a failure.
