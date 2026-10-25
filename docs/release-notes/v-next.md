# v-next

## NewHeap.Platform.AspNet.Common: background-operation failures and cancellations

Three fixes keep a background operation's outcome recordable and true to what stopped it.

- The runner checks a failed or retry result's failure code and message key against their columns
  (100 and 300 characters) before it writes them. A code that is too long is stored as
  `handler-rejected` and a key as the generic `background-operation.failed`; the returned text goes
  to the operational log only. The completion no longer fails on the database (PostgreSQL 22001),
  so the attempt always completes, an expected failure stays terminal and a retry result keeps its
  retry.
- A progress step whose action threw keeps its own exception, a cancellation included, when the
  write that records the step as failed or cancelled fails; that failure is logged as a warning with
  the operation and attempt. A lost fence, lock contention and a durable cancellation request that
  the write finds after the step's own error still stop the attempt.
- A cancellation the heartbeat did not record is a time-out only when the soft time-out fired.
  Otherwise the runner checks the attempt's durable cancellation and ends the operation `Cancelled`
  when one was requested; any other cancellation is reported as a time-out, as before.

| Breaking change | Required action |
|---|---|
| A handler that stops on a durable cancellation before the heartbeat sees it, through `ThrowIfCancellationRequestedAsync` or a write of the attempt, ends the operation `Cancelled` (`cancelled`, `background-operation.cancelled`) instead of `TimedOut` (`soft-timeout`), and the default notification policy no longer reports a time-out for a cancellation the owner requested. | None, unless monitoring or a notification policy counted these cancellations as time-outs; expect `Cancelled`. |
| A failure code of 101 to 200 characters is stored as `handler-rejected`, and a message key longer than 300 characters as `background-operation.failed`, instead of failing the completion write. | Return failure codes of at most 100 characters and translation keys of at most 300; read the returned text from the operational log. |
| When the write of a failed or cancelled step fails, the handler and the runner receive the step's own exception instead of the database exception. | None, unless a handler catches the database exception of that write; catch the step's own exception instead. |
| A write of an attempt that lost its fence throws an internal subclass of `InvalidOperationException` with the same message. | Assert it in tests with a check that accepts derived types, such as `Assert.ThrowsAnyAsync<InvalidOperationException>`. |
