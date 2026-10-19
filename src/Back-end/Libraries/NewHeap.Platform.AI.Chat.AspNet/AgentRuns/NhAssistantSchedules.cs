using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The Hangfire job of a scheduled trigger: it reads the subjects of the tick and fires the
/// trigger for each of them, at most <c>MaxSubjectsPerTick</c>. A tick fires every subject once:
/// a repeated job in the same minute finds the runs it already started. While the assistant is
/// disabled or the agent is paused, stopped or disabled in code, the tick is skipped before the
/// subjects are read. The shadow variants of the agent get every subject of the tick too, unless
/// they are not active themselves.
/// </summary>
internal sealed class NhAssistantScheduledTriggerJob(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantAgentTriggers triggers,
    ILogger<NhAssistantScheduledTriggerJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(string triggerId)
    {
        await RunAsync(triggerId, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    internal async Task<IReadOnlyList<NhAssistantTriggerReceipt>> RunAsync(
        string triggerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await configurations.EnsureCurrentAsync(cancellationToken);
        if (!registration.Triggers.TryGetValue(triggerId, out var trigger)
            || trigger.Source != NhAssistantTriggerSource.Schedule
            || trigger.ScheduledSubjects is null)
        {
            logger.LogWarning("Scheduled trigger {TriggerId} is not registered; the recurring job does nothing.", triggerId);
            return [];
        }

        // Read before the subjects query: a tick of an agent or skill that is not active comes back
        // by itself.
        var refusal = await triggers.RefusalOfAsync(trigger.AgentId, refusePaused: true, cancellationToken);
        if (refusal is null
            && trigger.SkillId is { } skillId
            && await triggers.SkillStateAsync(trigger.AgentId, skillId, cancellationToken) is var skillState
            && skillState != NhAssistantAgentRunStates.Active)
        {
            refusal = skillState == NhAssistantAgentRunStates.Paused ? NhAssistantErrorCodes.SkillPaused : NhAssistantErrorCodes.SkillStopped;
        }
        var variants = await triggers.AcceptingVariantsAsync(trigger, null, refusePaused: true, cancellationToken);
        var variantsTakingTick = new List<string>();
        foreach (var variant in variants)
        {
            // A variant's paused skill skips the tick for that variant only.
            if (triggers.SkillFor(trigger, variant) is not { } variantSkill
                || await triggers.SkillStateAsync(variant, variantSkill, cancellationToken) == NhAssistantAgentRunStates.Active)
            {
                variantsTakingTick.Add(variant);
            }
        }
        if (refusal is not null)
        {
            logger.LogInformation(
                "Scheduled trigger {TriggerId} skipped its tick ({Reason}).",
                triggerId,
                refusal);
            if (variantsTakingTick.Count == 0)
            {
                return [];
            }
        }

        var tick = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).ToUniversalTime();
        var receipts = new List<NhAssistantTriggerReceipt>();
        var subjects = 0;
        await foreach (var subject in trigger.ScheduledSubjects(services, cancellationToken))
        {
            if (subjects >= trigger.MaxSubjectsPerTick)
            {
                logger.LogInformation(
                    "Scheduled trigger {TriggerId} reached its limit of {Limit} subjects; the rest wait for the next tick.",
                    triggerId,
                    trigger.MaxSubjectsPerTick);
                break;
            }

            subjects++;
            var scheduled = new NhAssistantScheduledTick(subject, tick);
            if (refusal is null)
            {
                receipts.Add(await triggers.FireScheduledAsync(trigger, scheduled, cancellationToken));
            }
            foreach (var variant in variantsTakingTick)
            {
                await triggers.FireScheduledAsync(trigger, scheduled, cancellationToken, variant);
            }
        }

        return receipts;
    }
}

/// <summary>
/// Registers a Hangfire recurring job for every scheduled trigger in code at startup. Data schedules
/// have jobs of their own; see <see cref="NhAssistantDataScheduleSync"/>.
/// </summary>
internal sealed class NhAssistantScheduleRegistration(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantScheduleRegistration> logger) : IHostedService
{
    internal const string RecurringJobPrefix = "nh-assistant-trigger-";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var schedules = registration.CodeTriggers.Values
            .Where(trigger => trigger.Source == NhAssistantTriggerSource.Schedule)
            .ToArray();
        if (schedules.Length == 0)
        {
            return Task.CompletedTask;
        }

        var manager = services.GetService<IRecurringJobManager>()
            ?? throw new InvalidOperationException(
                "Scheduled assistant triggers need Hangfire. Configure WithHangfire in the host.");
        var queue = services.GetService<INhHangfireQueueNameResolver>()?.GetQueueName() ?? "default";
        foreach (var trigger in schedules)
        {
            var triggerId = trigger.Id;
            manager.AddOrUpdate<NhAssistantScheduledTriggerJob>(
                RecurringJobPrefix + triggerId,
                queue,
                job => job.RunAsync(triggerId),
                trigger.Cron!,
                new RecurringJobOptions { TimeZone = trigger.TimeZone });
            logger.LogInformation("Scheduled assistant trigger {TriggerId} runs on '{Cron}'.", triggerId, trigger.Cron);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
