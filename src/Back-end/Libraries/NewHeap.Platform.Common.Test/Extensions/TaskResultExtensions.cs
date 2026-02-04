using AwesomeAssertions;
using AwesomeAssertions.Execution;
using AwesomeAssertions.Primitives;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.Common.Test.Extensions;

public static class TaskResultExtensions
{
    public static AndConstraint<BooleanAssertions> ShouldBeSuccess(
        this TaskResult taskResult, string? because = null)
    {
        because ??= "Expected success result, but got error result.";
        return taskResult.Success.Should().BeTrue(because);
    }

    public static AndConstraint<BooleanAssertions> ShouldBeError(
        this TaskResult taskResult, string? because = null)
    {
        because ??= "Expected error result, but got success result.";
        return taskResult.Success.Should().BeFalse(because);
    }

    public static AndConstraint<BooleanAssertions> ShouldBeSuccess<T>(
        this TaskResult<T> taskResult, string? because = null)
    {
        because ??= "Expected success result, but got error result.";
        return taskResult.Success.Should().BeTrue(because);
    }

    public static AndConstraint<BooleanAssertions> ShouldBeError<T>(
        this TaskResult<T> taskResult, string? because = null)
    {
        because ??= "Expected error result, but got success result.";
        return taskResult.Success.Should().BeFalse(because);
    }

    public static AndConstraint<BooleanAssertions> ShouldBeSuccess<T>(
        this DisposableTaskResult<T> taskResult, string? because = null)
        where T : IDisposable
    {
        because ??= "Expected success result, but got error result.";
        return taskResult.Success.Should().BeTrue(because);
    }

    public static AndConstraint<BooleanAssertions> ShouldBeError<T>(
        this DisposableTaskResult<T> taskResult, string? because = null)
        where T : IDisposable
    {
        because ??= "Expected error result, but got success result.";
        return taskResult.Success.Should().BeFalse(because);
    }

    public static TaskResult AsSuccess(
        this TaskResult taskResult, string? because = null)
    {
        using var _ = new AssertionScope();
        taskResult.ShouldBeSuccess(because);
        return taskResult;
    }

    public static TaskResult AsError(
        this TaskResult taskResult, string? because = null)
    {
        using var _ = new AssertionScope();
        taskResult.ShouldBeError(because);
        return taskResult;
    }

    public static TaskResult<T> AsSuccess<T>(
        this TaskResult<T> taskResult, string? because = null)
    {
        using var _ = new AssertionScope();
        taskResult.ShouldBeSuccess(because);
        return taskResult;
    }

    public static TaskResult<T> AsError<T>(
        this TaskResult<T> taskResult, string? because = null)
    {
        using var _ = new AssertionScope();
        taskResult.ShouldBeError(because);
        return taskResult;
    }

    public static DisposableTaskResult<T> AsSuccess<T>(
        this DisposableTaskResult<T> taskResult, string? because = null)
        where T : IDisposable
    {
        using var _ = new AssertionScope();
        taskResult.ShouldBeSuccess(because);
        return taskResult;
    }

    public static DisposableTaskResult<T> AsError<T>(
        this DisposableTaskResult<T> taskResult, string? because = null)
        where T : IDisposable
    {
        using var _ = new AssertionScope();
        taskResult.ShouldBeError(because);
        return taskResult;
    }
}
