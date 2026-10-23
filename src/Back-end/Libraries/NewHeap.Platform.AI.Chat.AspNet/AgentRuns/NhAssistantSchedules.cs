using System.Runtime.CompilerServices;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The Hangfire job of a scheduled trigger: it reads the subjects of the tick and fires the
/// trigger for each of them, at most <c>MaxSubjectsPerTick</c>. A tick fires every subject once:
/// a repeated job in the same minute finds the runs it already started. While the assistant is
/// disabled or the agent is paused, stopped, off or disabled in code, the tick is skipped before the
/// subjects are read; an agent or skill that is off reports it as one ignored receipt
/// (<see cref="NhAssistantTriggerReasons.AgentOff"/> or <see cref="NhAssistantTriggerReasons.SkillOff"/>).
/// The shadow variants of the agent get every subject of the tick too, unless
/// they are not active themselves. A schedule without subjects runs for the whole application:
/// once on <c>nh-system/global</c> for an agent that works app-wide, once per listed division for
/// one that works per division.
/// </summary>
internal sealed class NhAssistantScheduledTriggerJob(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantAgentTriggers triggers,
    NhAssistantWorksOnRule worksOn,
    ILogger<NhAssistantScheduledTriggerJob> logger)
{
    /// <summary>
    /// The entry point of the recurring job. Hangfire fires the cron on real time; the minute the
    /// tick belongs to comes from the application's clock (a registered <see cref="TimeProvider"/>,
    /// otherwise the system clock), so a test that moves its clock to the next minute ticks again,
    /// and a clock that stands still counts every later tick as the same minute.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(string triggerId)
    {
        var applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        await RunAsync(triggerId, applicationClock.GetUtcNow(), CancellationToken.None);
    }

    /// <summary>
    /// The entry point of the recurring job of a data schedule's candidate version, which ticks on its
    /// own cron next to the live version's job.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunCandidateAsync(string triggerId)
    {
        var applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        await RunCandidateAsync(triggerId, applicationClock.GetUtcNow(), CancellationToken.None);
    }

    /// <summary>
    /// Fires the candidate version of a data schedule for every subject of its tick, for the trigger's
    /// agent only, into its shadow threads; the agent's variants follow the live version only. A tick
    /// that the agent or the skill would not take is skipped, as for the live version, and nothing
    /// waits for a resume.
    /// </summary>
    internal async Task<IReadOnlyList<NhAssistantTriggerReceipt>> RunCandidateAsync(
        string triggerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await configurations.EnsureCurrentAsync(cancellationToken);
        if (!registration.TriggerCandidates.TryGetValue(triggerId, out var candidate)
            || candidate.Source != NhAssistantTriggerSource.Schedule)
        {
            logger.LogInformation("Scheduled trigger {TriggerId} has no candidate schedule; the recurring job does nothing.", triggerId);
            return [];
        }

        if (await triggers.RefusalOfAsync(candidate.AgentId, refusePaused: true, cancellationToken, refuseOff: true) is not null
            || (candidate.SkillId is { } skillId
                && await triggers.SkillStateAsync(candidate.AgentId, skillId, cancellationToken) != NhAssistantAgentRunStates.Active))
        {
            return [];
        }

        // The minute of the application's clock, as for the live version's tick.
        var tick = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).ToUniversalTime();
        var receipts = new List<NhAssistantTriggerReceipt>();
        var tickSubjects = candidate.ScheduledSubjects is { } subjectsOfTick
            ? subjectsOfTick(services, cancellationToken)
            : SystemSubjectsAsync(candidate, cancellationToken);
        await foreach (var subject in tickSubjects)
        {
            if (receipts.Count >= candidate.MaxSubjectsPerTick)
            {
                break;
            }

            receipts.Add(await triggers.FireScheduledCandidateAsync(candidate, new NhAssistantScheduledTick(subject, tick), cancellationToken));
        }

        return receipts;
    }

    internal async Task<IReadOnlyList<NhAssistantTriggerReceipt>> RunAsync(
        string triggerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await configurations.EnsureCurrentAsync(cancellationToken);
        if (!registration.Triggers.TryGetValue(triggerId, out var trigger)
            || trigger.Source != NhAssistantTriggerSource.Schedule)
        {
            logger.LogWarning("Scheduled trigger {TriggerId} is not registered; the recurring job does nothing.", triggerId);
            return [];
        }

        // Read before the subjects query: a tick of an agent or skill that is not active comes back
        // by itself.
        var refusal = await triggers.RefusalOfAsync(trigger.AgentId, refusePaused: true, cancellationToken, refuseOff: true);
        if (refusal is null
            && trigger.SkillId is { } skillId
            && await triggers.SkillStateAsync(trigger.AgentId, skillId, cancellationToken) is var skillState
            && skillState != NhAssistantAgentRunStates.Active)
        {
            refusal = skillState switch
            {
                NhAssistantAgentRunStates.Paused => NhAssistantErrorCodes.SkillPaused,
                NhAssistantAgentRunStates.Off => NhAssistantErrorCodes.SkillOff,
                _ => NhAssistantErrorCodes.SkillStopped
            };
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
        var receipts = new List<NhAssistantTriggerReceipt>();
        if (refusal is NhAssistantErrorCodes.AgentOff or NhAssistantErrorCodes.SkillOff)
        {
            // An agent or skill that is off ignores the tick silently, without reading its subjects.
            receipts.Add(new NhAssistantTriggerReceipt(
                triggerId,
                NhAssistantTriggerOutcomes.Ignored,
                null,
                null,
                refusal == NhAssistantErrorCodes.AgentOff ? NhAssistantTriggerReasons.AgentOff : NhAssistantTriggerReasons.SkillOff));
        }
        else if (refusal is not null)
        {
            logger.LogInformation(
                "Scheduled trigger {TriggerId} skipped its tick ({Reason}).",
                triggerId,
                refusal);
        }
        if (refusal is not null && variantsTakingTick.Count == 0)
        {
            return receipts;
        }

        // The tick is the minute of the application's clock: a repeated job in that minute
        // deduplicates against the runs it already started.
        var tick = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).ToUniversalTime();
        var subjects = 0;
        var tickSubjects = trigger.ScheduledSubjects is { } subjectsOfTick
            ? subjectsOfTick(services, cancellationToken)
            : SystemSubjectsAsync(trigger, cancellationToken);
        await foreach (var subject in tickSubjects)
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

    /// <summary>
    /// The subjects of a schedule without subjects: the whole application app-wide, or the whole
    /// application in each listed division, ordered by name, for an agent that works per division.
    /// One more division than a tick takes is read, so the job reports the limit.
    /// </summary>
    private async IAsyncEnumerable<NhAiSubjectRef> SystemSubjectsAsync(
        NhAssistantTriggerDefinition trigger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!worksOn.Of(trigger.AgentId).PerDivision)
        {
            yield return NhAiSystemSubjects.Global;
            yield break;
        }

        if (services.GetService<INhDivisionDirectory>() is not { } directory)
        {
            logger.LogWarning(
                "Scheduled trigger {TriggerId} starts agent {AgentId}, which works per division, but the application lists no divisions; the tick starts nothing.",
                trigger.Id,
                trigger.AgentId);
            yield break;
        }

        var limit = Math.Min(trigger.MaxSubjectsPerTick + 1, NhDivisionInfo.MaxListLimit);
        foreach (var division in await directory.ListAsync(limit, cancellationToken))
        {
            yield return NhAiSystemSubjects.ForDivision(division.Id);
        }
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
