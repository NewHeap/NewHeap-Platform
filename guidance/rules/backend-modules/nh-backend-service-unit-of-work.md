---
id: nh-backend-service-unit-of-work
title: "Service-owned unit of work"
area: backend
reference: backend-unit-of-work
summary: "Let the concrete service own the transactional boundary, business rules, and event order while nested operations join the existing scope."
sample-cases: ["SPM-054", "SPM-190", "SPM-191", "SPM-192", "SPM-193", "SPM-194", "SPM-196", "SPM-197", "SPM-198", "SPM-200"]
public-symbols: ["StartOrGetTransactionScopeAsync", "UpdatePartialAsync", "INhDbTransactionScope", "NhTransactionFactoryAsync", "NhTransactionFactory"]
skills: ["newheap-backend-development", "newheap-background-processing"]
providers: ["sql-server", "postgresql"]
risk: critical
---
## Preferred approach

Open the unit of work in the concrete service with `StartOrGetTransactionScopeAsync`. Let nested NewHeap operations reuse the same scope and allow only the owner to commit it. Publish transactional events before the commit so the outbox and domain change succeed or roll back together. Make partial and bulk behavior explicit, and report partial results only when that is the agreed contract.

Use provider-translatable LINQ and maintain transactional tests for SQL Server and PostgreSQL. With CAP, the publisher belongs in the same scope; a publish failure must not leave a committed domain change.

Pass the caller's cancellation token to `StartOrGetTransactionScopeAsync`. The scope opens its transaction through `StartTransactionAsync`, which runs the registered transaction factories: the synchronous `NhTransactionFactory` delegates first, then the asynchronous `NhTransactionFactoryAsync` delegates, each in registration order and each receiving the transaction the one before returned; without a factory the context opens it with `BeginTransactionAsync`. A cancelled token stops the call before anything opens, and when a factory throws or is cancelled the repository disposes the transaction an earlier factory returned, so the context holds no transaction afterwards. Register a factory of your own as a singleton `NhTransactionFactoryAsync`: open the transaction with `BeginTransactionAsync(cancellationToken)` when it receives `null`, otherwise wrap or return the transaction it received, and dispose only a transaction it opened itself when it fails. `NhTransactionFactory` remains for compatibility; with CAP, a synchronous factory opens the transaction that CAP's asynchronous factory then wraps.

## Avoid

- A controller that opens or commits a domain transaction.
- A nested service committing a transactional scope it does not own.
- Publishing events only after commit when atomic outbox semantics are required.
- Using InMemory as evidence for rollback, constraints, isolation, or outbox behavior.
- A new synchronous `NhTransactionFactory`, or a factory that opens with `BeginTransaction()`, on an asynchronous path; register an `NhTransactionFactoryAsync` that passes the caller's token.
- A factory that expects CAP's transaction from a synchronous factory registered after `AddCap`: synchronous factories run before CAP's asynchronous one.

## Verification

Test success, a business failure, a publish failure, a nested operation, and rollback on both relational providers. Verify that exactly one owner commits and that no partial data or broker message remains after a failure. For a transaction factory of your own, also verify on both providers that cancellation before and while the transaction opens, and a failing factory, leave no transaction on the context and that the next scope owns a transaction of its own.
