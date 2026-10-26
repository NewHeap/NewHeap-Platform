using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// How the runner records what a handler returned or how it stopped, on both relational providers.
/// A failed or retry result whose failure code or message key does not fit its column completes
/// with the generic code or key instead of failing the completion write, so no fenced attempt stays
/// unfinished. A handler that stops on a durable cancellation it saw before the heartbeat did ends
/// cancelled; only a soft time-out that fired ends timed out. An error that the handler throws
/// outside a step while its cancellation is requested ends the operation cancelled without a retry,
/// while a lost fence and contention still win and an error without a request still retries.
/// </summary>
public sealed class NhBackgroundOperationOutcomeProviderTests
{
    private const string FailingOperationType = "outcome-failing";
    private const string StoppingOperationType = "outcome-stopping";
    private const string TimingOutOperationType = "outcome-timing-out";
    private const string StoppingAfterTimeOutOperationType = "outcome-stopping-after-time-out";

    /// <summary>
    /// The error that a handler throws outside a step, as a client library that reports a
    /// cancelled call as an error of its own would.
    /// </summary>
    private const string HandlerError = "The project archive did not answer.";

    /// <summary>
    /// The heartbeat of these tests falls due only after a minute, so every stop the runner sees
    /// comes from the handler or the soft time-out, never from a heartbeat.
    /// </summary>
    private static readonly TimeSpan SlowHeartbeat = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A heartbeat for the tests in which the heartbeat stops the attempt.
    /// </summary>
    private static readonly TimeSpan FastHeartbeat = TimeSpan.FromMilliseconds(100);

    public enum FailureKind
    {
        Failed,
        Retry
    }

    public enum StopMode
    {
        ChecksDurableCancellation,
        ThrowsItsOwnCancellation,
        ThrowsAfterLosingItsFence,
        ThrowsItsOwnError,
        ThrowsItsOwnErrorOnceStopped,
        WritesAfterLosingItsFence
    }

    /// <summary>
    /// Who finds the cancellation request after the handler's error: the runner's durable check,
    /// or the heartbeat, which stopped the attempt before the handler threw.
    /// </summary>
    public enum CancellationSeenBy
    {
        DurableCheck,
        Heartbeat
    }

    /// <summary>
    /// A failure code of 150 characters and a message key of 400 do not fit the columns of 100 and
    /// 300. The failed result still ends the operation terminally, and the retry result still
    /// schedules its retry, with <c>handler-rejected</c> and the generic failure key; the attempt is
    /// completed, and the returned text reaches the operational log only.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, FailureKind.Failed)]
    [InlineData(RelationalTestProvider.SqlServer, FailureKind.Retry)]
    [InlineData(RelationalTestProvider.PostgreSql, FailureKind.Failed)]
    [InlineData(RelationalTestProvider.PostgreSql, FailureKind.Retry)]
    public async Task A_failure_code_and_message_key_longer_than_their_columns_fall_back_and_complete_the_attempt(
        RelationalTestProvider provider,
        FailureKind kind)
    {
        await using var host = await CreateHostAsync(provider);
        var code = "rejected-" + new string('c', 141);
        var messageKey = "outcome.rejected." + new string('k', 383);
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new FailingRequest(code, messageKey, kind == FailureKind.Retry), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();

        await host.CreateRunner(logger).RunAsync(operationId, generation).WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.CurrentAttemptId.Should().BeNull();
        operation.FailureCode.Should().Be("handler-rejected");
        operation.FailureMessageKey.Should().Be("background-operation.failed");
        var attempt = operation.Attempts.Should().ContainSingle().Subject;
        attempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Failed);
        attempt.CompletedAt.Should().NotBeNull();
        attempt.FailureCode.Should().Be("handler-rejected");
        var root = operation.Steps.Single(step => step.StepKey == "root");
        if (kind == FailureKind.Failed)
        {
            operation.Status.Should().Be(NhBackgroundOperationStatus.Failed);
            operation.NextDispatchAt.Should().BeNull();
            operation.CompletedAt.Should().NotBeNull();
            root.Status.Should().Be(NhBackgroundOperationStepStatus.Failed);
        }
        else
        {
            operation.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
            operation.NextDispatchAt.Should().NotBeNull();
            operation.CompletedAt.Should().BeNull();
            root.Status.Should().Be(NhBackgroundOperationStepStatus.Pending);
        }

        var warnings = logger.At(LogLevel.Warning).Select(entry => entry.Message).ToArray();
        warnings.Should().Contain(message => message.Contains(code, StringComparison.Ordinal)
                                             && message.Contains(operationId.ToString(), StringComparison.Ordinal));
        warnings.Should().Contain(message => message.Contains(messageKey, StringComparison.Ordinal)
                                             && message.Contains(operationId.ToString(), StringComparison.Ordinal));
    }

    /// <summary>
    /// A failure code and a message key that fill their columns exactly are stored as returned, and
    /// the columns are as long as the runner's check assumes.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_failure_code_and_message_key_that_fill_their_columns_are_stored_as_returned(
        RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var code = "rejected-" + new string('c', NhBackgroundOperationColumnLimits.FailureCode - 9);
        var messageKey = "outcome.rejected." + new string('k', NhBackgroundOperationColumnLimits.FailureMessageKey - 17);
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new FailingRequest(code, messageKey, Retry: false), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();

        await host.CreateRunner(logger).RunAsync(operationId, generation).WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.Failed);
        operation.FailureCode.Should().Be(code);
        operation.FailureMessageKey.Should().Be(messageKey);
        operation.Attempts.Should().ContainSingle().Which.FailureCode.Should().Be(code);
        logger.At(LogLevel.Warning).Should().BeEmpty();

        await using var scope = host.Services.CreateAsyncScope();
        var model = scope.ServiceProvider.GetRequiredService<BackgroundOperationProviderDbContext>().Model;
        model.FindEntityType(typeof(NhBackgroundOperation))!
            .FindProperty(nameof(NhBackgroundOperation.FailureCode))!
            .GetMaxLength().Should().Be(NhBackgroundOperationColumnLimits.FailureCode);
        model.FindEntityType(typeof(NhBackgroundOperation))!
            .FindProperty(nameof(NhBackgroundOperation.FailureMessageKey))!
            .GetMaxLength().Should().Be(NhBackgroundOperationColumnLimits.FailureMessageKey);
        model.FindEntityType(typeof(NhBackgroundOperationAttempt))!
            .FindProperty(nameof(NhBackgroundOperationAttempt.FailureCode))!
            .GetMaxLength().Should().Be(NhBackgroundOperationColumnLimits.FailureCode);
    }

    /// <summary>
    /// The owner requests the cancellation between heartbeats, and the handler stops at its own
    /// <c>ThrowIfCancellationRequestedAsync</c> before the heartbeat sees the request. The operation
    /// ends cancelled, not timed out.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_durable_cancellation_the_handler_sees_before_the_heartbeat_ends_the_operation_cancelled(
        RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StoppingRequest(StopMode.ChecksDurableCancellation), owner);
        var generation = await host.DispatchAsync(operationId);
        var run = host.CreateRunner(new CapturingLogger<NhBackgroundOperationRunner>()).RunAsync(operationId, generation);
        await state.Started.Task.WaitAsync(BackgroundOperationProviderHost.Patience);

        await host.RequestCancellationAsync(operationId, owner);
        state.Proceed.TrySetResult();
        await run.WaitAsync(BackgroundOperationProviderHost.Patience);

        state.SawCancellation.Should().BeTrue();
        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        operation.FailureCode.Should().Be("cancelled");
        operation.FailureMessageKey.Should().Be("background-operation.cancelled");
        operation.CurrentAttemptId.Should().BeNull();
        operation.Attempts.Should().ContainSingle().Which.Status.Should().Be(NhBackgroundOperationAttemptStatus.Cancelled);
        operation.Steps.Single(step => step.StepKey == "root").Status.Should().Be(NhBackgroundOperationStepStatus.Cancelled);
        operation.Events.Should().Contain(operationEvent => operationEvent.MessageKey == "background-operation.cancelled");
    }

    /// <summary>
    /// A soft time-out that fires still ends the operation timed out.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_soft_timeout_that_fired_ends_the_operation_timed_out(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new TimingOutRequest(), owner);
        var generation = await host.DispatchAsync(operationId);

        await host.CreateRunner(new CapturingLogger<NhBackgroundOperationRunner>())
            .RunAsync(operationId, generation)
            .WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.TimedOut);
        operation.FailureCode.Should().Be("soft-timeout");
        operation.FailureMessageKey.Should().Be("background-operation.timed-out");
        operation.Attempts.Should().ContainSingle().Which.CompletedAt.Should().NotBeNull();
    }

    /// <summary>
    /// A cancellation that neither the soft time-out nor a durable cancellation caused, such as a
    /// handler's own client time-out, is reported as a time-out, as before.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_cancellation_of_the_handlers_own_is_still_reported_as_a_time_out(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StoppingRequest(StopMode.ThrowsItsOwnCancellation), owner);
        var generation = await host.DispatchAsync(operationId);
        state.Proceed.TrySetResult();

        await host.CreateRunner(new CapturingLogger<NhBackgroundOperationRunner>())
            .RunAsync(operationId, generation)
            .WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.TimedOut);
        operation.FailureCode.Should().Be("soft-timeout");
    }

    /// <summary>
    /// The operation moved to another attempt whose cancellation was requested. The durable check
    /// is fenced on this attempt, so the stopped attempt does not take that cancellation for its
    /// own, and its completion writes nothing over the other attempt's operation.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_cancellation_requested_for_another_attempt_leaves_the_fenced_attempt_without_a_write(
        RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StoppingRequest(StopMode.ThrowsAfterLosingItsFence), owner);
        var generation = await host.DispatchAsync(operationId);
        var run = host.CreateRunner(new CapturingLogger<NhBackgroundOperationRunner>()).RunAsync(operationId, generation);
        await state.Started.Task.WaitAsync(BackgroundOperationProviderHost.Patience);
        var running = await host.ReadOperationAsync(operationId);
        var ownAttemptId = running.CurrentAttemptId!.Value;

        await host.MoveToAnotherAttemptAsync(operationId, NhBackgroundOperationStatus.CancelRequested);
        var moved = await host.ReadOperationAsync(operationId);
        state.Proceed.TrySetResult();
        await run.WaitAsync(BackgroundOperationProviderHost.Patience);

        var after = await host.ReadOperationAsync(operationId);
        after.Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        after.CurrentAttemptId.Should().Be(moved.CurrentAttemptId);
        after.Version.Should().Be(moved.Version);
        after.Events.Should().HaveCount(moved.Events.Count);
        var ownAttempt = after.Attempts.Single(attempt => attempt.Id == ownAttemptId);
        ownAttempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Running);
        ownAttempt.CompletedAt.Should().BeNull();
    }

    /// <summary>
    /// The owner cancels, and the handler throws an error of its own outside a step: an error that
    /// has nothing to do with the request, while the runner's durable check finds it, or a
    /// cancelled call that a client reports as its own error after the heartbeat stopped the
    /// attempt. The operation, which may retry twice, ends cancelled as a cancellation does, and
    /// nothing retries the error; the error is logged as a warning with the operation id.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, CancellationSeenBy.DurableCheck)]
    [InlineData(RelationalTestProvider.SqlServer, CancellationSeenBy.Heartbeat)]
    [InlineData(RelationalTestProvider.PostgreSql, CancellationSeenBy.DurableCheck)]
    [InlineData(RelationalTestProvider.PostgreSql, CancellationSeenBy.Heartbeat)]
    public async Task A_handler_error_outside_a_step_during_a_requested_cancellation_ends_cancelled_without_a_retry(
        RelationalTestProvider provider,
        CancellationSeenBy seenBy)
    {
        await using var host = await CreateHostAsync(
            provider,
            seenBy == CancellationSeenBy.Heartbeat ? FastHeartbeat : SlowHeartbeat);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var mode = seenBy == CancellationSeenBy.Heartbeat
            ? StopMode.ThrowsItsOwnErrorOnceStopped
            : StopMode.ThrowsItsOwnError;
        var operationId = await host.EnqueueAsync(new StoppingRequest(mode), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        var run = host.CreateRunner(logger).RunAsync(operationId, generation);
        await state.Started.Task.WaitAsync(BackgroundOperationProviderHost.Patience);

        await host.RequestCancellationAsync(operationId, owner);
        if (seenBy == CancellationSeenBy.Heartbeat)
        {
            await state.Stopped.Task.WaitAsync(BackgroundOperationProviderHost.Patience);
        }

        state.Proceed.TrySetResult();
        await run.WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        operation.FailureCode.Should().Be("cancelled");
        operation.FailureMessageKey.Should().Be("background-operation.cancelled");
        operation.DiagnosticCorrelationId.Should().BeNull();
        operation.CurrentAttemptId.Should().BeNull();
        operation.NextDispatchAt.Should().BeNull();
        operation.CompletedAt.Should().NotBeNull();
        operation.Attempts.Should().ContainSingle().Which.Status.Should().Be(NhBackgroundOperationAttemptStatus.Cancelled);
        operation.Steps.Single(step => step.StepKey == "root").Status.Should().Be(NhBackgroundOperationStepStatus.Cancelled);
        operation.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == "background-operation.cancelled");
        logger.At(LogLevel.Error).Should().BeEmpty();
        logger.At(LogLevel.Warning).Should().Contain(entry =>
            entry.Exception is InvalidOperationException
            && entry.Exception.Message == HandlerError
            && entry.Message.Contains(operationId.ToString(), StringComparison.Ordinal));

        // Nothing retries it: a later dispatch round leaves the cancelled operation alone.
        await host.Services.GetRequiredService<NhBackgroundOperationDispatchService>().DispatchAvailableAsync(CancellationToken.None);
        var later = await host.ReadOperationAsync(operationId);
        later.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        later.Attempts.Should().ContainSingle();
        later.Version.Should().Be(operation.Version);
    }

    /// <summary>
    /// Without a cancellation request, the same error fails the attempt as <c>handler-failed</c>
    /// with a diagnostic correlation and schedules the operation's retry, as before.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_handler_error_outside_a_step_without_a_cancellation_request_stays_handler_failed_and_retries(
        RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StoppingRequest(StopMode.ThrowsItsOwnError), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        state.Proceed.TrySetResult();

        await host.CreateRunner(logger).RunAsync(operationId, generation).WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        operation.FailureCode.Should().Be("handler-failed");
        operation.FailureMessageKey.Should().Be("background-operation.failed");
        operation.DiagnosticCorrelationId.Should().NotBeNullOrEmpty();
        operation.NextDispatchAt.Should().NotBeNull();
        operation.CompletedAt.Should().BeNull();
        var attempt = operation.Attempts.Should().ContainSingle().Subject;
        attempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Failed);
        attempt.FailureCode.Should().Be("handler-failed");
        logger.At(LogLevel.Error).Should().ContainSingle(entry =>
            entry.Exception is InvalidOperationException
            && entry.Exception.Message == HandlerError);
    }

    /// <summary>
    /// The soft time-out fired before the owner cancelled, and the handler then threw an error of
    /// its own. The time-out keeps its classification, as for a cancelled execution: the operation
    /// ends timed out, and nothing retries the error.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_handler_error_after_the_soft_time_out_fired_during_a_requested_cancellation_ends_timed_out(
        RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StoppingAfterTimeOutRequest(), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        var run = host.CreateRunner(logger).RunAsync(operationId, generation);
        await state.Stopped.Task.WaitAsync(BackgroundOperationProviderHost.Patience);

        await host.RequestCancellationAsync(operationId, owner);
        state.Proceed.TrySetResult();
        await run.WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.TimedOut);
        operation.FailureCode.Should().Be("soft-timeout");
        operation.FailureMessageKey.Should().Be("background-operation.timed-out");
        operation.NextDispatchAt.Should().BeNull();
        operation.Attempts.Should().ContainSingle().Which.CompletedAt.Should().NotBeNull();
        logger.At(LogLevel.Error).Should().BeEmpty();
    }

    /// <summary>
    /// The operation moved to another attempt whose cancellation was requested, and the handler then
    /// threw an error of its own or wrote through its context and found the fence lost. The lost fence
    /// wins over the cancellation request: the stopped attempt records nothing over the other
    /// attempt's operation, neither a failure nor a cancellation.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, StopMode.ThrowsItsOwnError)]
    [InlineData(RelationalTestProvider.SqlServer, StopMode.WritesAfterLosingItsFence)]
    [InlineData(RelationalTestProvider.PostgreSql, StopMode.ThrowsItsOwnError)]
    [InlineData(RelationalTestProvider.PostgreSql, StopMode.WritesAfterLosingItsFence)]
    public async Task A_lost_fence_still_wins_over_a_requested_cancellation_after_a_handler_error(
        RelationalTestProvider provider,
        StopMode mode)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StoppingRequest(mode), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        var run = host.CreateRunner(logger).RunAsync(operationId, generation);
        await state.Started.Task.WaitAsync(BackgroundOperationProviderHost.Patience);
        var running = await host.ReadOperationAsync(operationId);
        var ownAttemptId = running.CurrentAttemptId!.Value;

        await host.MoveToAnotherAttemptAsync(operationId, NhBackgroundOperationStatus.CancelRequested);
        var moved = await host.ReadOperationAsync(operationId);
        state.Proceed.TrySetResult();
        await run.WaitAsync(BackgroundOperationProviderHost.Patience);

        var after = await host.ReadOperationAsync(operationId);
        after.Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        after.CurrentAttemptId.Should().Be(moved.CurrentAttemptId);
        after.FailureCode.Should().BeNull();
        after.Version.Should().Be(moved.Version);
        after.Events.Should().HaveCount(moved.Events.Count);
        var ownAttempt = after.Attempts.Single(attempt => attempt.Id == ownAttemptId);
        ownAttempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Running);
        ownAttempt.CompletedAt.Should().BeNull();
        if (mode == StopMode.WritesAfterLosingItsFence)
        {
            logger.At(LogLevel.Warning).Should().Contain(entry =>
                entry.Message.Contains("losing durable ownership", StringComparison.Ordinal)
                && entry.Message.Contains(operationId.ToString(), StringComparison.Ordinal));
            logger.At(LogLevel.Error).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Another connection holds the operation's lock, so the heartbeat stops the attempt as
    /// contention, and the handler reports the cancelled call as an error of its own. Contention
    /// still wins: the operation is rescheduled as busy, and the abandoned attempt spends no retry.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Contention_the_heartbeat_recorded_still_wins_over_a_handler_error(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, FastHeartbeat);
        var state = host.Services.GetRequiredService<StoppingState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StoppingRequest(StopMode.ThrowsItsOwnErrorOnceStopped), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        var run = host.CreateRunner(logger).RunAsync(operationId, generation);
        await state.Started.Task.WaitAsync(BackgroundOperationProviderHost.Patience);

        await using (await host.HoldOperationLockAsync(operationId))
        {
            await state.Stopped.Task.WaitAsync(BackgroundOperationProviderHost.Patience);
        }

        state.Proceed.TrySetResult();
        await run.WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        operation.FailureCode.Should().Be("concurrency-busy");
        operation.FailureMessageKey.Should().Be("background-operation.waiting-for-resource");
        operation.Attempts.Should().ContainSingle().Which.Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
        (await host.Persistence.GetFailedAttemptCountAsync(operationId, CancellationToken.None)).Should().Be(0);
        logger.At(LogLevel.Error).Should().BeEmpty();
        logger.At(LogLevel.Warning).Should().Contain(entry =>
            entry.Exception is InvalidOperationException
            && entry.Exception.Message == HandlerError);
    }

    private static Task<BackgroundOperationProviderHost> CreateHostAsync(
        RelationalTestProvider provider,
        TimeSpan? heartbeat = null)
    {
        return BackgroundOperationProviderHost.CreateAsync(
            provider,
            operations =>
            {
                operations.Add<FailingRequest, FailingHandler>(
                    FailingOperationType,
                    operation => operation.WithRetry(2));
                operations.Add<StoppingRequest, StoppingHandler>(
                    StoppingOperationType,
                    operation => operation.WithRetry(2));
                operations.Add<TimingOutRequest, TimingOutHandler>(
                    TimingOutOperationType,
                    operation => operation.WithSoftTimeout(TimeSpan.FromMilliseconds(300)));
                operations.Add<StoppingAfterTimeOutRequest, StoppingAfterTimeOutHandler>(
                    StoppingAfterTimeOutOperationType,
                    operation => operation
                        .WithRetry(2)
                        .WithSoftTimeout(TimeSpan.FromMilliseconds(300)));
            },
            services =>
            {
                services.AddSingleton<StoppingState>();
                services.AddTransient<FailingHandler>();
                services.AddTransient<StoppingHandler>();
                services.AddTransient<TimingOutHandler>();
                services.AddTransient<StoppingAfterTimeOutHandler>();
            },
            heartbeat ?? SlowHeartbeat);
    }

    private sealed record FailingRequest(string Code, string MessageKey, bool Retry);

    private sealed record StoppingRequest(StopMode Mode);

    private sealed record TimingOutRequest;

    private sealed record StoppingAfterTimeOutRequest;

    /// <summary>
    /// Returns the failure code and message key it was given, as a failed result or as a retry.
    /// </summary>
    private sealed class FailingHandler : INhBackgroundOperationHandler<FailingRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            FailingRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            TaskResult result = request.Retry
                ? NhBackgroundOperationRetryResult.Retry(request.Code, request.MessageKey, TimeSpan.FromMinutes(5))
                : TaskResult.Failed(request.Code, request.MessageKey);
            return Task.FromResult(result);
        }
    }

    private sealed class StoppingState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Set when the handler's cancellation token was cancelled, by the heartbeat or the soft
        /// time-out.
        /// </summary>
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; set; }
    }

    /// <summary>
    /// Waits until the test lets it proceed, then stops as its mode says. In
    /// <see cref="StopMode.ThrowsItsOwnErrorOnceStopped"/> it first waits until its token is
    /// cancelled.
    /// </summary>
    private sealed class StoppingHandler(StoppingState state) : INhBackgroundOperationHandler<StoppingRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            StoppingRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            state.Started.TrySetResult();
            if (request.Mode == StopMode.ThrowsItsOwnErrorOnceStopped)
            {
                return await ThrowItsOwnErrorOnceStoppedAsync(state, cancellationToken);
            }

            await state.Proceed.Task.WaitAsync(BackgroundOperationProviderHost.Patience, cancellationToken);
            switch (request.Mode)
            {
                case StopMode.ChecksDurableCancellation:
                    try
                    {
                        await context.ThrowIfCancellationRequestedAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        state.SawCancellation = true;
                        throw;
                    }

                    return TaskResult.Failed("cancellation-not-seen", "outcome.cancellation-not-seen");
                case StopMode.ThrowsItsOwnError:
                    throw new InvalidOperationException(HandlerError);
                case StopMode.WritesAfterLosingItsFence:
                    await context.Messages.PublishAsync(
                        NhBackgroundOperationMessage.Info("outcome.after-losing-the-fence"),
                        cancellationToken);
                    return TaskResult.Failed("fence-not-lost", "outcome.fence-not-lost");
                default:
                    throw new OperationCanceledException("The handler's own client gave up.");
            }
        }

        /// <summary>
        /// Waits until its token is cancelled, then, once the test lets it proceed, reports the
        /// cancelled call as an error of its own, as some client libraries do.
        /// </summary>
        internal static async Task<TaskResult> ThrowItsOwnErrorOnceStoppedAsync(
            StoppingState state,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                state.Stopped.TrySetResult();
            }

            await state.Proceed.Task.WaitAsync(BackgroundOperationProviderHost.Patience);
            throw new InvalidOperationException(HandlerError);
        }
    }

    /// <summary>
    /// Waits until its soft time-out cancels it, then throws an error of its own once the test lets
    /// it proceed.
    /// </summary>
    private sealed class StoppingAfterTimeOutHandler(StoppingState state) : INhBackgroundOperationHandler<StoppingAfterTimeOutRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            StoppingAfterTimeOutRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            state.Started.TrySetResult();
            return StoppingHandler.ThrowItsOwnErrorOnceStoppedAsync(state, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until its soft time-out cancels it.
    /// </summary>
    private sealed class TimingOutHandler : INhBackgroundOperationHandler<TimingOutRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            TimingOutRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TaskResult.Succeeded();
        }
    }
}
