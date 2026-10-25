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
/// cancelled; only a soft time-out that fired ends timed out.
/// </summary>
public sealed class NhBackgroundOperationOutcomeProviderTests
{
    private const string FailingOperationType = "outcome-failing";
    private const string StoppingOperationType = "outcome-stopping";
    private const string TimingOutOperationType = "outcome-timing-out";

    /// <summary>
    /// The heartbeat of these tests falls due only after a minute, so every stop the runner sees
    /// comes from the handler or the soft time-out, never from a heartbeat.
    /// </summary>
    private static readonly TimeSpan SlowHeartbeat = TimeSpan.FromMinutes(1);

    public enum FailureKind
    {
        Failed,
        Retry
    }

    public enum StopMode
    {
        ChecksDurableCancellation,
        ThrowsItsOwnCancellation,
        ThrowsAfterLosingItsFence
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

    private static Task<BackgroundOperationProviderHost> CreateHostAsync(RelationalTestProvider provider)
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
            },
            services =>
            {
                services.AddSingleton<StoppingState>();
                services.AddTransient<FailingHandler>();
                services.AddTransient<StoppingHandler>();
                services.AddTransient<TimingOutHandler>();
            },
            SlowHeartbeat);
    }

    private sealed record FailingRequest(string Code, string MessageKey, bool Retry);

    private sealed record StoppingRequest(StopMode Mode);

    private sealed record TimingOutRequest;

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

        public bool SawCancellation { get; set; }
    }

    /// <summary>
    /// Waits until the test lets it proceed, then stops as its mode says.
    /// </summary>
    private sealed class StoppingHandler(StoppingState state) : INhBackgroundOperationHandler<StoppingRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            StoppingRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            state.Started.TrySetResult();
            await state.Proceed.Task.WaitAsync(BackgroundOperationProviderHost.Patience, cancellationToken);
            if (request.Mode == StopMode.ChecksDurableCancellation)
            {
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
            }

            throw new OperationCanceledException("The handler's own client gave up.");
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
