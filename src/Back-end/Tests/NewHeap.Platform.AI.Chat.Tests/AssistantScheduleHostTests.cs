using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Hosts that know the schedules but do not run them (LS11), on both relational providers. Two hosts
/// register the same agents and schedules and share the database; only the one whose Hangfire server
/// runs the schedules keeps <see cref="NhAssistantOptions.RunSchedules"/> true. The other starts without
/// Hangfire, adds and removes no recurring job, neither for schedules in code nor for schedules defined as
/// data, and still knows and fires its schedules and triggers.
/// </summary>
public sealed class AssistantScheduleHostTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string CodeSchedule = "weekly-review";
    private const string DataSchedule = "monthly-review";
    private const string CodeJob = "nh-assistant-trigger-" + CodeSchedule;
    private const string DataJob = "nh-assistant-data-trigger-" + DataSchedule;

    /// <summary>
    /// The job of a data schedule that another host registered and that this host's snapshot does not
    /// know, such as one created there a moment ago.
    /// </summary>
    private const string ForeignDataJob = "nh-assistant-data-trigger-created-elsewhere";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_host_that_does_not_run_schedules_starts_without_Hangfire_and_touches_no_recurring_job(AssistantTestProvider provider)
    {
        // Without any Hangfire service the host starts and still knows and fires its schedules.
        await using (var withoutHangfire = await CreateHostAsync(provider, runSchedules: false, hangfire: null))
        {
            await StartScheduleRegistrationAsync(withoutHangfire);
            Assert.True((await SaveDataScheduleAsync(withoutHangfire)).Success);
            var registration = withoutHangfire.Services.GetRequiredService<NhAssistantRegistrationState>();
            Assert.Equal(NhAssistantTriggerSource.Schedule, registration.Triggers[DataSchedule].Source);
            var receipts = await TickAsync(withoutHangfire, CodeSchedule);
            Assert.Equal(2, receipts.Count);
            Assert.All(receipts, receipt => Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome));
        }

        // Next to the Hangfire storage of the host that runs the schedules, it adds and removes nothing:
        // the other host's job that its own snapshot does not know stays.
        var hangfire = new RecordingRecurringJobs(CodeJob, ForeignDataJob);
        await using var sharingStorage = await CreateHostAsync(provider, runSchedules: false, hangfire);
        await StartScheduleRegistrationAsync(sharingStorage);
        Assert.True((await SaveDataScheduleAsync(sharingStorage)).Success);
        await RefreshAsync(sharingStorage);

        Assert.Empty(hangfire.Added);
        Assert.Empty(hangfire.Removed);
        Assert.Contains(sharingStorage.Logs.Messages, message => message.Contains("RunSchedules is false", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_host_that_runs_schedules_registers_their_jobs_and_follows_its_snapshot(AssistantTestProvider provider)
    {
        // The default runs the schedules, so a host without Hangfire cannot start its schedule in code.
        await using (var withoutHangfire = await CreateHostAsync(provider, runSchedules: null, hangfire: null))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => StartScheduleRegistrationAsync(withoutHangfire));
            Assert.Contains("need Hangfire", refused.Message, StringComparison.Ordinal);
        }

        var hangfire = new RecordingRecurringJobs(CodeJob, ForeignDataJob);
        await using var host = await CreateHostAsync(provider, runSchedules: true, hangfire);
        await StartScheduleRegistrationAsync(host);
        Assert.Equal([CodeJob], hangfire.Added);

        // A data schedule gets its job, and a job its snapshot does not know is removed.
        Assert.True((await SaveDataScheduleAsync(host)).Success);
        await RefreshAsync(host);
        Assert.Contains(DataJob, hangfire.Added);
        Assert.Equal([ForeignDataJob], hangfire.Removed.Distinct());
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, bool? runSchedules, RecordingRecurringJobs? hangfire)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                if (runSchedules is { } run)
                {
                    services.Configure<NhAssistantOptions>(options => options.RunSchedules = run);
                }

                if (hangfire is not null)
                {
                    services.AddSingleton<IRecurringJobManager>(hangfire);
                    services.AddSingleton(hangfire.Storage);
                }
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddSubjectSet("test-projects", "Two test projects", TestProjectsAsync)
                .ConfigureAgent(AgentId, agent => agent.Grant("projects.*"))
                .AddScheduledTrigger(CodeSchedule, schedule => schedule
                    .StartAgent(AgentId)
                    .Cron("0 7 * * 1")
                    .Subjects(TestProjectsAsync)
                    .Message(tick => "Weekly review of " + tick.Subject.Id + ".")));
    }

    private static async Task StartScheduleRegistrationAsync(AssistantTestHost host)
    {
        await host.Services.GetServices<IHostedService>().OfType<NhAssistantScheduleRegistration>().Single()
            .StartAsync(CancellationToken.None);
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantDataTriggerDto>> SaveDataScheduleAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SaveTriggerAsync(
            AgentId,
            DataSchedule,
            new NhAssistantDataTriggerRequest(
                NhAssistantDataTriggerSources.Schedule,
                Cron: "0 7 1 * *",
                SubjectSet: "test-projects",
                Message: "Monthly review"),
            "admin-1");
    }

    /// <summary>
    /// Loads the snapshot again, as the background refresh does when another host changed it.
    /// </summary>
    private static async Task RefreshAsync(AssistantTestHost host)
    {
        await host.Services.GetRequiredService<NhAssistantRunConfigurations>().EnsureCurrentAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> TickAsync(AssistantTestHost host, string triggerId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>()
            .RunAsync(triggerId, new DateTimeOffset(2026, 10, 19, 5, 0, 10, TimeSpan.Zero), CancellationToken.None);
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> TestProjectsAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new NhAiSubjectRef("project", "11111111111111111111111111111111");
        yield return new NhAiSubjectRef("project", "22222222222222222222222222222222");
    }

    /// <summary>
    /// The recurring job manager and storage of the host that runs the schedules, recording every job
    /// that is added or removed; it lists the jobs it was created with as existing.
    /// </summary>
    private sealed class RecordingRecurringJobs : IRecurringJobManager
    {
        public RecordingRecurringJobs(params string[] existing)
        {
            var connection = Substitute.For<IStorageConnection>();
            connection.GetAllItemsFromSet("recurring-jobs").Returns(_ => new HashSet<string>(existing));
            Storage = Substitute.For<JobStorage>();
            Storage.GetConnection().Returns(connection);
        }

        public JobStorage Storage { get; }

        public ConcurrentQueue<string> Added { get; } = new();

        public ConcurrentQueue<string> Removed { get; } = new();

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
        {
            Added.Enqueue(recurringJobId);
        }

        public void Trigger(string recurringJobId)
        {
        }

        public void RemoveIfExists(string recurringJobId)
        {
            Removed.Enqueue(recurringJobId);
        }
    }
}

/// <summary>
/// The background operations of the assistant on a queue of their own (LS10): every operation type
/// moves to the queue, and the queue gets the concurrency limit; a limit needs a queue of its own.
/// </summary>
public sealed class AssistantAgentRunQueueTests
{
    [Fact]
    public void The_assistants_operations_run_on_the_chosen_queue_within_its_limit()
    {
        var services = new ServiceCollection();
        services.AddNewHeapBackgroundOperations<OrderDbContext>(operations =>
        {
            operations.Options.DispatchWorkersEnabled = false;
            operations.WithGlobalConcurrency(8)
                .WithReservedQueueConcurrency("agent-runs", 2)
                .AddAssistantAgentRuns(runs => runs.Queue("Agent-Runs").MaxConcurrency(4));
        });
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<NhBackgroundOperationRegistry>();
        var options = provider.GetRequiredService<NhBackgroundOperationsOptions>();
        Assert.Equal(6, registry.Descriptors.Count);
        Assert.All(registry.Descriptors, descriptor => Assert.Equal("agent-runs", descriptor.Queue));
        Assert.Equal(4, options.QueueConcurrencyLimits["agent-runs"]);
        Assert.Equal(2, options.ReservedQueueConcurrency["agent-runs"]);
        Assert.All(registry.Descriptors, descriptor => Assert.Equal(int.MaxValue, descriptor.MaxConcurrency));
    }

    [Fact]
    public void Without_a_choice_they_stay_on_the_default_queue_and_a_limit_needs_a_queue()
    {
        var services = new ServiceCollection();
        services.AddNewHeapBackgroundOperations<OrderDbContext>(operations => operations.AddAssistantAgentRuns());
        using (var provider = services.BuildServiceProvider())
        {
            Assert.All(provider.GetRequiredService<NhBackgroundOperationRegistry>().Descriptors, descriptor => Assert.Null(descriptor.Queue));
            Assert.Empty(provider.GetRequiredService<NhBackgroundOperationsOptions>().QueueConcurrencyLimits);
        }

        var withoutQueue = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddNewHeapBackgroundOperations<OrderDbContext>(operations => operations.AddAssistantAgentRuns(runs => runs.MaxConcurrency(4))));
        Assert.Contains("Queue", withoutQueue.Message, StringComparison.Ordinal);

        var changedLimit = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddNewHeapBackgroundOperations<OrderDbContext>(operations => operations
                .WithQueueConcurrency("agent-runs", 2)
                .AddAssistantAgentRuns(runs => runs.Queue("agent-runs").MaxConcurrency(4))));
        Assert.Contains("already has a concurrency limit of 2", changedLimit.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection()
            .AddNewHeapBackgroundOperations<OrderDbContext>(operations => operations.AddAssistantAgentRuns(runs => runs.Queue("agent-runs").MaxConcurrency(0))));
    }
}
