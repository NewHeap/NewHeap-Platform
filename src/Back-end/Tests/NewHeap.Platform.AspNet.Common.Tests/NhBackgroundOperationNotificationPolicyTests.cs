using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhBackgroundOperationNotificationPolicyTests
{
    [Theory]
    [InlineData("background-operation.succeeded", true)]
    [InlineData("background-operation.failed", true)]
    [InlineData("background-operation.timedout", true)]
    [InlineData("background-operation.operator-recovery-required", true)]
    [InlineData("background-operation.signal-wait-started", true)]
    [InlineData("sample.analysis.needs-review", true)]
    [InlineData("background-operation.started", false)]
    [InlineData("background-operation.retry-scheduled", false)]
    [InlineData("background-operation.retry-requested", false)]
    [InlineData("background-operation.cancellation-requested", false)]
    [InlineData("background-operation.result-available", false)]
    [InlineData("background-operation.stale-attempt-recovered", false)]
    [InlineData("background-operation.children-created", false)]
    public async Task DefaultPolicyNotifiesOnlyOutcomesAndRequestsForAttention(string messageKey, bool expected)
    {
        var policy = CreatePolicy();

        var decision = await policy.DecideAsync(CreateOperation(), CreateMilestone(messageKey));

        decision.ShouldNotify.Should().Be(expected);
        if (expected)
        {
            decision.Content.Should().NotBeNull();
            decision.GroupKey.Should().BeNull();
            decision.Url.Should().BeNull();
        }
        else
        {
            decision.Content.Should().BeNull();
        }
    }

    [Fact]
    public async Task DefaultPolicySkipsCancellationRequestedByTheOwner()
    {
        var policy = CreatePolicy();
        var requested = CreateOperation();
        requested.CancelRequestedAt = DateTimeOffset.UtcNow;
        var unrequested = CreateOperation();

        var requestedDecision = await policy.DecideAsync(requested, CreateMilestone("background-operation.cancelled"));
        var unrequestedDecision = await policy.DecideAsync(unrequested, CreateMilestone("background-operation.cancelled"));

        requestedDecision.ShouldNotify.Should().BeFalse();
        unrequestedDecision.ShouldNotify.Should().BeTrue();
    }

    [Fact]
    public async Task DefaultFormatterDescribesTheTimedOutLifecycleEvent()
    {
        var policy = CreatePolicy();

        var decision = await policy.DecideAsync(CreateOperation(), CreateMilestone("background-operation.timedout"));

        decision.Content!.Message.Should().Be("The operation timed out.");
        decision.Content.Title.Should().Be("Background operation: policy-test");
    }

    [Fact]
    public void DecisionCanBeRefinedWithThreadingAndPresentation()
    {
        var decision = NhBackgroundOperationNotificationDecision
            .Notify(new NhBackgroundOperationNotificationContent("Report ready", "Open the report."))
            with
            {
                GroupKey = "report:42",
                Url = "/reports/42",
                Category = "report",
                Severity = NhUserNotificationSeverity.Success
            };

        decision.ShouldNotify.Should().BeTrue();
        decision.GroupKey.Should().Be("report:42");
        decision.Url.Should().Be("/reports/42");
        decision.Category.Should().Be("report");
        decision.Severity.Should().Be(NhUserNotificationSeverity.Success);
        NhBackgroundOperationNotificationDecision.Skip().ShouldNotify.Should().BeFalse();
    }

    [Fact]
    public void UseNotificationPolicyReplacesThePolicyRegistration()
    {
        var services = new ServiceCollection();
        services.AddScoped<INhBackgroundOperationNotificationPolicy, NhDefaultBackgroundOperationNotificationPolicy>();
        var builder = new NhBackgroundOperationBuilder(services, new NhBackgroundOperationsOptions());

        builder.UseNotificationPolicy<SkipEverythingPolicy>();

        services.Where(x => x.ServiceType == typeof(INhBackgroundOperationNotificationPolicy))
            .Should().ContainSingle()
            .Which.ImplementationType.Should().Be<SkipEverythingPolicy>();
    }

    private static NhDefaultBackgroundOperationNotificationPolicy CreatePolicy()
    {
        return new NhDefaultBackgroundOperationNotificationPolicy(
            new NhDefaultBackgroundOperationNotificationFormatter());
    }

    private static NhBackgroundOperation CreateOperation()
    {
        return new NhBackgroundOperation
        {
            Id = Guid.NewGuid(),
            OperationType = "policy-test",
            PayloadJson = "{}",
            OwnerUserId = Guid.NewGuid(),
            Status = NhBackgroundOperationStatus.Running,
            Version = 1
        };
    }

    private static NhBackgroundOperationEvent CreateMilestone(string messageKey)
    {
        return new NhBackgroundOperationEvent
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            EventType = NhBackgroundOperationEventType.StateChanged,
            Severity = NhBackgroundOperationMessageSeverity.Information,
            MessageKey = messageKey,
            IsMilestone = true
        };
    }

    private sealed class SkipEverythingPolicy : INhBackgroundOperationNotificationPolicy
    {
        public Task<NhBackgroundOperationNotificationDecision> DecideAsync(
            NhBackgroundOperation operation,
            NhBackgroundOperationEvent milestone,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(NhBackgroundOperationNotificationDecision.Skip());
        }
    }
}
