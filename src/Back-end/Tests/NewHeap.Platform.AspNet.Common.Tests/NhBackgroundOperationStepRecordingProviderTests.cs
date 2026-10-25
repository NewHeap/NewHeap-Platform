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
/// A progress step whose action throws is recorded as failed or cancelled before the exception
/// travels on. When that write fails, as a database failure does, the step's own exception still
/// reaches the handler and the runner, and the failed write is logged with the operation. A lost
/// fence and contention for the operation's lock still stop the attempt, as does a durable
/// cancellation request that the write finds after a step failed with an error of its own. On both
/// relational providers, for root and nested steps.
/// </summary>
public sealed class NhBackgroundOperationStepRecordingProviderTests
{
    private const string OperationType = "step-recording";
    private const string FailingStepKey = "failing-step";
    private const string OuterStepKey = "outer-step";

    private static readonly TimeSpan SlowHeartbeat = TimeSpan.FromMinutes(1);

    public enum StepLevel
    {
        Root,
        Nested
    }

    public enum StepOutcome
    {
        Error,
        Cancellation
    }

    /// <summary>
    /// The write that records the failed or cancelled step fails. The step's own exception, a
    /// handler error or a cancellation, reaches the caller unchanged, and the failed write is logged
    /// with the operation and attempt. A nested step's exception also reaches its parent step, which
    /// records itself as usual.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, StepLevel.Root, StepOutcome.Error)]
    [InlineData(RelationalTestProvider.SqlServer, StepLevel.Root, StepOutcome.Cancellation)]
    [InlineData(RelationalTestProvider.SqlServer, StepLevel.Nested, StepOutcome.Error)]
    [InlineData(RelationalTestProvider.SqlServer, StepLevel.Nested, StepOutcome.Cancellation)]
    [InlineData(RelationalTestProvider.PostgreSql, StepLevel.Root, StepOutcome.Error)]
    [InlineData(RelationalTestProvider.PostgreSql, StepLevel.Root, StepOutcome.Cancellation)]
    [InlineData(RelationalTestProvider.PostgreSql, StepLevel.Nested, StepOutcome.Error)]
    [InlineData(RelationalTestProvider.PostgreSql, StepLevel.Nested, StepOutcome.Cancellation)]
    public async Task The_steps_own_exception_survives_a_failed_write_of_the_step(
        RelationalTestProvider provider,
        StepLevel level,
        StepOutcome outcome)
    {
        await using var host = await CreateHostAsync(provider);
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StepRecordingRequest(), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        var (claim, context) = await host.StartAttemptAsync(operationId, generation, logger);
        Exception original = outcome == StepOutcome.Error
            ? new StepFailedException()
            : new OperationCanceledException("The step was cancelled.");
        host.WriteFailures.FailCompletionOfStep(FailingStepKey);

        var run = () => RunFailingStepAsync(context, level, () => throw original);

        (await run.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(original);
        var warning = logger.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Exception.Should().BeOfType<DbUpdateException>();
        warning.Message.Should().Contain(FailingStepKey)
            .And.Contain(claim.OperationId.ToString())
            .And.Contain(claim.AttemptId.ToString())
            .And.Contain(original.GetType().Name);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Steps.Single(step => step.StepKey == FailingStepKey).Status
            .Should().Be(NhBackgroundOperationStepStatus.Running, "the write that would record the step failed");
        if (level == StepLevel.Nested)
        {
            operation.Steps.Single(step => step.StepKey == OuterStepKey).Status.Should().Be(
                outcome == StepOutcome.Error
                    ? NhBackgroundOperationStepStatus.Failed
                    : NhBackgroundOperationStepStatus.Cancelled);
        }
    }

    /// <summary>
    /// Another holder keeps the operation's lock while the failed step is recorded. The contention
    /// signal still wins, so the runner reschedules the attempt, and the step's error is logged.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Contention_while_recording_a_failed_step_still_stops_the_attempt(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StepRecordingRequest(), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        var (_, context) = await host.StartAttemptAsync(operationId, generation, logger);
        var original = new StepFailedException();
        IAsyncDisposable? holder = null;
        try
        {
            var run = () => RunFailingStepAsync(context, StepLevel.Root, async () =>
            {
                holder = await host.HoldOperationLockAsync(operationId);
                throw original;
            });

            await run.Should().ThrowExactlyAsync<NhBackgroundOperationContentionSignal>();
        }
        finally
        {
            if (holder is not null)
            {
                await holder.DisposeAsync();
            }
        }

        logger.At(LogLevel.Warning).Should().ContainSingle()
            .Which.Exception.Should().BeSameAs(original);
    }

    /// <summary>
    /// The operation moved to another attempt while the step ran. The lost fence still wins: the
    /// attempt is no longer this one's to record, and the step's error is logged.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_lost_fence_while_recording_a_failed_step_still_stops_the_attempt(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StepRecordingRequest(), owner);
        var generation = await host.DispatchAsync(operationId);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();
        var (_, context) = await host.StartAttemptAsync(operationId, generation, logger);
        var original = new StepFailedException();

        var run = () => RunFailingStepAsync(context, StepLevel.Root, async () =>
        {
            await host.MoveToAnotherAttemptAsync(operationId, NhBackgroundOperationStatus.RetryScheduled);
            throw original;
        });

        (await run.Should().ThrowExactlyAsync<NhBackgroundOperationOwnershipLostException>())
            .Which.Should().BeAssignableTo<InvalidOperationException>()
            .Which.Message.Should().Contain("fencing token");
        logger.At(LogLevel.Warning).Should().ContainSingle()
            .Which.Exception.Should().BeSameAs(original);
    }

    /// <summary>
    /// The handler's step fails and the write that records it fails as well. The runner records the
    /// step's own error as the handler's failure and schedules the retry the registration allows.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task The_runner_records_the_steps_own_error_after_the_write_of_the_step_failed(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<FailingStepState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StepRecordingRequest(), owner);
        var generation = await host.DispatchAsync(operationId);
        state.BeforeFailure = () =>
        {
            host.WriteFailures.FailCompletionOfStep(FailingStepKey);
            return Task.CompletedTask;
        };
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();

        await host.CreateRunner(logger).RunAsync(operationId, generation).WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        operation.FailureCode.Should().Be("handler-failed");
        operation.CurrentAttemptId.Should().BeNull();
        operation.Attempts.Should().ContainSingle().Which.Status.Should().Be(NhBackgroundOperationAttemptStatus.Failed);
        logger.At(LogLevel.Error).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<StepFailedException>();
        logger.At(LogLevel.Warning).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<DbUpdateException>();
    }

    /// <summary>
    /// The owner requests the cancellation while the step runs, and the step then fails with an
    /// error of its own. The write that records the step finds the cancellation request, which
    /// stops the attempt: the operation ends cancelled instead of retrying a handler error its owner
    /// cancelled, and the step's error is logged.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_cancellation_request_found_while_recording_a_failed_step_ends_the_operation_cancelled(
        RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var state = host.Services.GetRequiredService<FailingStepState>();
        var owner = await host.CreateUserAsync();
        var operationId = await host.EnqueueAsync(new StepRecordingRequest(), owner);
        var generation = await host.DispatchAsync(operationId);
        state.BeforeFailure = () => host.RequestCancellationAsync(operationId, owner);
        var logger = new CapturingLogger<NhBackgroundOperationRunner>();

        await host.CreateRunner(logger).RunAsync(operationId, generation).WaitAsync(BackgroundOperationProviderHost.Patience);

        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        operation.FailureCode.Should().Be("cancelled");
        operation.NextDispatchAt.Should().BeNull();
        operation.Attempts.Should().ContainSingle().Which.Status.Should().Be(NhBackgroundOperationAttemptStatus.Cancelled);
        logger.At(LogLevel.Warning).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<StepFailedException>();
        logger.At(LogLevel.Error).Should().BeEmpty();
    }

    private static async Task RunFailingStepAsync(
        NhBackgroundOperationContext context,
        StepLevel level,
        Func<Task> fail)
    {
        if (level == StepLevel.Root)
        {
            await context.Progress.RunStepAsync(FailingStepKey, async (_, _) =>
            {
                await fail();
                return TaskResult.Succeeded();
            });
            return;
        }

        await context.Progress.RunStepAsync(OuterStepKey, (outer, cancellationToken) =>
            outer.RunStepAsync(FailingStepKey, 1, async (_, _) =>
            {
                await fail();
                return TaskResult.Succeeded();
            }, cancellationToken));
    }

    private static Task<BackgroundOperationProviderHost> CreateHostAsync(RelationalTestProvider provider)
    {
        return BackgroundOperationProviderHost.CreateAsync(
            provider,
            operations => operations.Add<StepRecordingRequest, FailingStepHandler>(
                OperationType,
                operation => operation.WithRetry(2)),
            services =>
            {
                services.AddSingleton<FailingStepState>();
                services.AddTransient<FailingStepHandler>();
            },
            SlowHeartbeat);
    }

    private sealed record StepRecordingRequest;

    private sealed class StepFailedException() : Exception("The step failed.");

    private sealed class FailingStepState
    {
        public Func<Task> BeforeFailure { get; set; } = () => Task.CompletedTask;
    }

    /// <summary>
    /// Runs one step that fails with an error of its own after the test's preparation.
    /// </summary>
    private sealed class FailingStepHandler(FailingStepState state) : INhBackgroundOperationHandler<StepRecordingRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            StepRecordingRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return context.Progress.RunStepAsync(FailingStepKey, async (_, _) =>
            {
                await state.BeforeFailure();
                throw new StepFailedException();
            }, cancellationToken);
        }
    }
}
