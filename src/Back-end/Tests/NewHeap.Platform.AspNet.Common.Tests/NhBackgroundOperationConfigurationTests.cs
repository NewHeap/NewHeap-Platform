using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhBackgroundOperationConfigurationTests
{
    [Fact]
    public void RegistrationRejectsUnstableOperationTypeNames()
    {
        var builder = CreateBuilder();

        var action = () => builder.Add<TestRequest, TestHandler>("Unstable Operation Name");

        action.Should().Throw<ArgumentException>()
            .WithMessage("*lowercase dash-case*");
    }

    [Fact]
    public void NonIdempotentHandlerCannotAccidentallyEnableAutomaticRetry()
    {
        var builder = CreateBuilder();

        var action = () => builder.Add<TestRequest, TestHandler>(
            "non-idempotent-test",
            operation => operation.RequireIdempotency(NhBackgroundOperationIdempotency.NonIdempotent));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot enable automatic retries*");
    }

    [Fact]
    public void ResourceKeyHelpersProduceStableBoundedKeys()
    {
        var divisionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var key = NhBackgroundOperationResourceKey.ForDivisionAction("Analyze Portfolio", divisionId);

        key.Should().Be("analyze%20portfolio:division:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        NhBackgroundOperationResourceKey.ForAction(
                "process",
                "resource",
                new string('x', 800))
            .Length.Should().BeLessThanOrEqualTo(450);
        NhBackgroundOperationKeys.HashResourceKey(key).Should().MatchRegex("^v1:[a-f0-9]{64}$");
    }

    [Fact]
    public void LiveUpdateGroupsAreIsolatedPerUserAndDivision()
    {
        var userId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var divisionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var globalGroup = NhBackgroundOperationHub.GetUserGroup(userId, null);
        var divisionGroup = NhBackgroundOperationHub.GetUserGroup(userId, divisionId);

        globalGroup.Should().Be("nh-background-operation-user:11111111222233334444555555555555:global");
        divisionGroup.Should().Be("nh-background-operation-user:11111111222233334444555555555555:division:aaaaaaaabbbbccccddddeeeeeeeeeeee");
        divisionGroup.Should().NotBe(globalGroup);
    }

    [Fact]
    public void QueueNamesAreNormalizedAndInvalidNamesAreRejectedAtRegistration()
    {
        var builder = CreateBuilder();
        builder.Add<TestRequest, TestHandler>(
            "queued-test",
            operation => operation.UseQueue("Exports-FAST"));

        builder.Build().GetForRequest(typeof(TestRequest)).Queue.Should().Be("exports-fast");

        var invalidBuilder = CreateBuilder();
        var action = () => invalidBuilder.Add<TestRequest, TestHandler>(
            "invalid-queue-test",
            operation => operation.UseQueue("invalid queue"));
        action.Should().Throw<ArgumentException>().WithMessage("*queue names*");
    }

    [Fact]
    public void NestedProgressTitlesAndIdempotencyPolicyAreRetainedInDescriptor()
    {
        var builder = CreateBuilder();
        builder.WithGlobalConcurrency(12)
            .WithQueueConcurrency("exports", 2);
        builder.Add<TestRequest, TestHandler>(
            "configured-test",
            operation => operation
                .WithPayloadSchemaVersion(2)
                .WithRetry(2)
                .WithTypeConcurrency(3)
                .ExclusivePer(
                    request => NhBackgroundOperationResourceKey.ForUserAction("configured-test", request.UserId),
                    NhBackgroundOperationConflictBehavior.ReturnExisting)
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));

        var descriptor = builder.Build().GetForRequest(typeof(TestRequest));

        descriptor.OperationType.Should().Be("configured-test");
        descriptor.PayloadSchemaVersion.Should().Be(2);
        descriptor.RetryCount.Should().Be(2);
        descriptor.MaxConcurrency.Should().Be(3);
        descriptor.ConflictBehavior.Should().Be(NhBackgroundOperationConflictBehavior.ReturnExisting);
        descriptor.Idempotency.Should().Be(NhBackgroundOperationIdempotency.IdempotentWithKey);
        builder.Options.MaxConcurrentOperations.Should().Be(12);
        builder.Options.QueueConcurrencyLimits["exports"].Should().Be(2);
    }

    [Fact]
    public void RetentionCannotRedactPayloadAfterItsOperationWouldBeRemoved()
    {
        var options = new NhBackgroundOperationsOptions
        {
            PayloadRetentionPeriod = TimeSpan.FromDays(31),
            SucceededRetentionPeriod = TimeSpan.FromDays(30)
        };

        var action = options.Validate;

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*retention periods are invalid*");
    }

    [Theory]
    [MemberData(nameof(InvalidRuntimeOptions))]
    public void InvalidRuntimeOptionsAreRejectedDuringRegistration(
        Action<NhBackgroundOperationsOptions> configure,
        string expectedMessage)
    {
        var options = CreateOptions();
        configure(options);

        var action = options.Validate;

        action.Should().Throw<InvalidOperationException>()
            .WithMessage(expectedMessage);
    }

    public static TheoryData<Action<NhBackgroundOperationsOptions>, string> InvalidRuntimeOptions()
    {
        return new TheoryData<Action<NhBackgroundOperationsOptions>, string>
        {
            {
                options => options.DefaultSoftTimeout = TimeSpan.Zero,
                "*DefaultSoftTimeout must be positive*"
            },
            {
                options => options.ProgressFlushInterval = TimeSpan.Zero,
                "*ProgressFlushInterval must be positive*"
            },
            {
                options => options.TransactionLockTimeoutMilliseconds = 0,
                "*TransactionLockTimeoutMilliseconds must be positive*"
            },
            {
                options =>
                {
                    options.UserNotificationProjectionEnabled = true;
                    options.OperationUrlPrefix = "//malicious.example/operations";
                },
                "*OperationUrlPrefix must be an absolute application path*"
            },
            {
                options =>
                {
                    options.LiveUpdatesEnabled = true;
                    options.HubPath = "/hub-malicious";
                },
                "*HubPath must be under /hub*"
            }
        };
    }

    private static NhBackgroundOperationBuilder CreateBuilder()
    {
        return new NhBackgroundOperationBuilder(new ServiceCollection(), CreateOptions());
    }

    private static NhBackgroundOperationsOptions CreateOptions()
    {
        return new NhBackgroundOperationsOptions
        {
            DispatchWorkersEnabled = false,
            ReconciliationEnabled = false,
            LiveUpdatesEnabled = false,
            UserNotificationProjectionEnabled = false
        };
    }

    private sealed record TestRequest(Guid UserId);

    private sealed class TestHandler : INhBackgroundOperationHandler<TestRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            TestRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }
}