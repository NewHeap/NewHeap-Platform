using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Keeps one Hangfire recurring job per schedule defined as data, under its own prefix, in line with
/// every snapshot that goes into effect: it adds and changes the jobs of the snapshot's data schedules
/// and removes those of schedules that were removed or are out of effect. The candidate version of a
/// data schedule has a job of its own (<see cref="CandidateJobSuffix"/>) on its own cron. Every host
/// does this; the recurring job manager makes repeated registrations harmless.
/// </summary>
internal sealed class NhAssistantDataScheduleSync(
    IServiceProvider services,
    ILogger<NhAssistantDataScheduleSync> logger) : INhAssistantRunSnapshotListener
{
    internal const string RecurringJobPrefix = "nh-assistant-data-trigger-";

    /// <summary>
    /// The suffix of a candidate's job id; a colon never occurs in a trigger id.
    /// </summary>
    internal const string CandidateJobSuffix = ":candidate";

    private string? _registered;

    public Task AppliedAsync(NhAssistantRunSnapshot snapshot, CancellationToken cancellationToken)
    {
        var schedules = snapshot.Triggers.Values
            .Where(trigger => trigger.Source == NhAssistantTriggerSource.Schedule && trigger.DataVersion is not null)
            .Concat(snapshot.TriggerCandidates.Values.Where(trigger => trigger.Source == NhAssistantTriggerSource.Schedule))
            .OrderBy(trigger => trigger.Id, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Candidate)
            .ToArray();
        var fingerprint = string.Join('\n', schedules.Select(trigger => $"{JobIdOf(trigger)} {trigger.Cron} {trigger.TimeZone.Id}"));
        if (string.Equals(fingerprint, Volatile.Read(ref _registered), StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var manager = services.GetService<IRecurringJobManager>();
        if (manager is null)
        {
            if (schedules.Length > 0)
            {
                logger.LogWarning("Schedules defined as data need Hangfire; configure WithHangfire in the host. They do not run.");
            }

            Volatile.Write(ref _registered, fingerprint);
            return Task.CompletedTask;
        }

        var queue = services.GetService<INhHangfireQueueNameResolver>()?.GetQueueName() ?? "default";
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in schedules)
        {
            var triggerId = trigger.Id;
            var jobId = JobIdOf(trigger);
            try
            {
                if (trigger.Candidate)
                {
                    manager.AddOrUpdate<NhAssistantScheduledTriggerJob>(
                        jobId,
                        queue,
                        job => job.RunCandidateAsync(triggerId),
                        trigger.Cron!,
                        new RecurringJobOptions { TimeZone = trigger.TimeZone });
                }
                else
                {
                    manager.AddOrUpdate<NhAssistantScheduledTriggerJob>(
                        jobId,
                        queue,
                        job => job.RunAsync(triggerId),
                        trigger.Cron!,
                        new RecurringJobOptions { TimeZone = trigger.TimeZone });
                }

                wanted.Add(jobId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(
                    "The schedule {TriggerId} defined as data was not registered ({ExceptionType}).",
                    triggerId,
                    exception.GetType().Name);
            }
        }

        if (services.GetService<JobStorage>() is { } storage)
        {
            using var connection = storage.GetConnection();
            foreach (var job in connection.GetRecurringJobs()
                         .Where(job => job.Id.StartsWith(RecurringJobPrefix, StringComparison.Ordinal) && !wanted.Contains(job.Id)))
            {
                manager.RemoveIfExists(job.Id);
            }
        }

        Volatile.Write(ref _registered, fingerprint);
        return Task.CompletedTask;
    }

    internal static string JobIdOf(NhAssistantTriggerDefinition trigger)
    {
        return RecurringJobPrefix + trigger.Id + (trigger.Candidate ? CandidateJobSuffix : string.Empty);
    }
}

/// <summary>
/// Loads the run configuration defined as data when the host starts and checks the stamp every
/// half minute, so a host that fires and starts nothing still follows changes made elsewhere, and
/// the recurring jobs of data schedules follow them too.
/// </summary>
internal sealed class NhAssistantRunConfigurationRefresh(
    NhAssistantRunConfigurations configurations,
    NhAssistantRegistrationState registration,
    IServiceProvider services) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A host that runs no agents on their own, in code or as data, follows nothing, unless
        // consultations are data: then every assistant host follows them, also one that only chats
        // (AC23), so its tool lists, MCP export and consult tools see a change another host made.
        var runsAgents = registration.CodeAgentRuns.Count > 0
            || services.GetService<NewHeap.Platform.AspNet.Common.Services.BackgroundOperations.NhBackgroundOperationRegistry>()
                ?.TryGetForOperationType(NhAssistantAgentRunOperationTypes.Run, out _) == true;
        if (!configurations.Enabled || (!runsAgents && registration.ConsultationsAsData is null))
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await configurations.EnsureCurrentAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
