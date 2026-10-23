using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Versions and candidates of triggers defined as data (S8-1 of the agent runs adoption plan): a change
/// is the next append-only version, which takes effect at once or fires as a candidate in the agent's
/// shadow thread next to the live version until it is promoted or discarded; the runs say which version
/// started them, the comparison counts both, and expected versions guard every change. Agents created at
/// runtime may start in shadow (S8-2). On both relational providers.
/// </summary>
public sealed class AssistantTriggerVersionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string FollowUp = "follow-up";
    private const string DataTrigger = "data-changed";
    private const string WeeklyTrigger = "weekly-check";
    private const string NotMinor = "not-minor";
    private const string Admin = "admin-1";
    private const string Follower = "project-follower";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_changed_trigger_fires_as_a_candidate_in_shadow_next_to_the_live_version_and_is_promoted(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await AddLiveTriggerAsync(host);

        // A new trigger has no live version to evaluate a candidate next to.
        Assert.Equal("apply", Field(await SaveTriggerAsync(host, "other-trigger", Trigger() with { Apply = NhAssistantSkillApplyModes.Shadow })));

        // The change skips minor changes; saved for shadow, it becomes the candidate.
        var candidate = await SaveTriggerAsync(host, DataTrigger, Trigger([NotMinor]) with { ExpectedVersion = 1, Apply = NhAssistantSkillApplyModes.Shadow, Reason = "Skip minor changes" });
        Assert.True(candidate.Success);
        Assert.Equal((2, true, true), (candidate.Data!.Version, candidate.Data.Shadow, candidate.Data.InEffect));
        Assert.Equal((1, false), (State(host).Triggers[DataTrigger].DataVersion, State(host).Triggers[DataTrigger].Shadow));
        var candidateDefinition = State(host).TriggerCandidates[DataTrigger];
        Assert.Equal((2, true, true), (candidateDefinition.DataVersion, candidateDefinition.Candidate, candidateDefinition.Shadow));
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!;
        var live = Assert.Single(view.DataTriggers);
        Assert.Equal((1, false, 2), (live.Version, live.Shadow, live.Candidate!.Version));
        Assert.Equal([NotMinor], live.Candidate.Filters);

        // One event: the live version works live, the candidate in the shadow thread, in the live
        // version of the skill.
        var project = Guid.NewGuid();
        var evt = new ProjectChanged(Guid.NewGuid(), project, "data first");
        var receipt = Assert.Single(await PublishAsync(host, evt));
        var liveRun = (await Runs(host).FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
        Assert.Equal((false, (int?)1, false), (liveRun.Shadow, liveRun.TriggerVersion, liveRun.TriggerCandidate));
        var shadowThread = (await ShadowThreadAsync(host, project))!;
        var candidateRun = Assert.Single(await RunsOfAsync(host, shadowThread.ConversationId));
        Assert.Equal((true, (int?)2, true, FollowUp, (int?)1, false), (
            candidateRun.Shadow,
            candidateRun.TriggerVersion,
            candidateRun.TriggerCandidate,
            candidateRun.SkillId,
            candidateRun.SkillVersion,
            candidateRun.SkillCandidate));
        Assert.Equal(NhAssistantTriggerKinds.Event, candidateRun.TriggerKind);
        await ExecuteAsync(host, liveRun.Id);
        await ExecuteAsync(host, candidateRun.Id);
        Assert.Equal(NhAssistantRunStatuses.Completed, (await Runs(host).FindRunAsync(candidateRun.Id, CancellationToken.None))!.Status);

        // A repeated delivery starts neither again; a minor change only the live version takes.
        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, Assert.Single(await PublishAsync(host, evt)).Outcome);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data minor"))).Outcome);
        Assert.Single(await RunsOfAsync(host, shadowThread.ConversationId));

        var comparison = (await ConfigureAsync(host, configuration => configuration.CompareTriggerCandidateAsync(AgentId, DataTrigger))).Data!;
        Assert.Equal((2, 1, 1, 1, 1, 0), (
            comparison.Live.Runs,
            comparison.Candidate.Runs,
            comparison.Candidate.Completed,
            comparison.PairedEvents,
            comparison.LiveOnlyEvents,
            comparison.CandidateOnlyEvents));
        Assert.Equal((1, 2), (comparison.Live.Version, comparison.Candidate.Version));

        // The run facts tell the versions apart: the candidate's run is a shadow run in its version.
        var ended = host.Business.Events.Where(item => item.Kind == NhAssistantAuditEventKind.AgentRunEnded).ToArray();
        Assert.Contains(ended, item => item.RunId == liveRun.Id && item.TriggerVersion == 1 && item.Shadow == false);
        Assert.Contains(ended, item => item.RunId == candidateRun.Id && item.TriggerVersion == 2 && item.Shadow == true);

        // Promoted, the candidate is the live version in the trigger's mode and fires no candidate run.
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await ConfigureAsync(host, configuration => configuration.PromoteTriggerCandidateAsync(AgentId, DataTrigger, 3, Admin))));
        var promoted = await ConfigureAsync(host, configuration => configuration.PromoteTriggerCandidateAsync(AgentId, DataTrigger, 2, Admin, "Fewer runs"));
        Assert.Equal((2, false), (promoted.Data!.Version, promoted.Data.Shadow));
        Assert.Equal([NotMinor], promoted.Data.Filters);
        Assert.Equal(2, State(host).Triggers[DataTrigger].DataVersion);
        Assert.Empty(State(host).TriggerCandidates);
        var next = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data second")));
        Assert.Equal((int?)2, (await Runs(host).FindRunAsync(next.RunId!.Value, CancellationToken.None))!.TriggerVersion);
        Assert.Equal(NhAssistantTriggerReasons.Filtered, Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data minor again"))).ReasonCode);
        Assert.Single(await RunsOfAsync(host, shadowThread.ConversationId));
        Assert.Equal(NhAssistantErrorCodes.TriggerCandidateNotFound, Code(await ConfigureAsync(host, configuration => configuration.CompareTriggerCandidateAsync(AgentId, DataTrigger))));
        Assert.Equal(NhAssistantErrorCodes.TriggerCandidateNotFound, Code(await ConfigureAsync(host, configuration => configuration.DiscardTriggerCandidateAsync(AgentId, DataTrigger, 2, Admin))));

        var versions = await ConfigureAsync(host, configuration => configuration.GetTriggerVersionsAsync(AgentId, DataTrigger));
        Assert.Equal([(2, NhAssistantSkillDefinitionStatuses.Live), (1, NhAssistantSkillDefinitionStatuses.Superseded)], versions.Select(item => (item.Version, item.Status)));
        Assert.Equal(("Skip minor changes", Admin), (versions[0].Reason, versions[0].CreatedBy));
        Assert.Equal([NotMinor], versions[0].Filters);
        var changes = host.Business.Events.Where(item => item.Kind == NhAssistantAuditEventKind.AdminAgentTriggerDefinitionChanged).ToArray();
        Assert.Equal(["created", "live", "candidate", "promoted"], changes.Select(item => item.ResultCode));
        Assert.Equal([1, 1, 2, 2], changes.Select(item => item.AgentVersion));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_discarded_candidate_and_a_removed_trigger_stay_as_history_and_stale_versions_are_refused(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await AddLiveTriggerAsync(host);
        Assert.True((await SaveTriggerAsync(host, DataTrigger, Trigger([NotMinor]) with { ExpectedVersion = 1, Apply = NhAssistantSkillApplyModes.Shadow })).Success);

        // A change is made on the live version, never on the candidate's.
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await SaveTriggerAsync(host, DataTrigger, Trigger() with { ExpectedVersion = 2 })));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await ConfigureAsync(host, configuration => configuration.DiscardTriggerCandidateAsync(AgentId, DataTrigger, 1, Admin))));

        // Discarded, the candidate fires no more; the live version keeps working.
        Assert.True((await ConfigureAsync(host, configuration => configuration.DiscardTriggerCandidateAsync(AgentId, DataTrigger, 2, Admin, "Not needed"))).Success);
        Assert.Empty(State(host).TriggerCandidates);
        var project = Guid.NewGuid();
        Assert.Equal(NhAssistantTriggerOutcomes.Started, Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data first"))).Outcome);
        Assert.Null(await ShadowThreadAsync(host, project));

        // A live change replaces a candidate and keeps the trigger's mode.
        Assert.True((await SaveTriggerAsync(host, DataTrigger, Trigger([NotMinor]) with { ExpectedVersion = 1, Apply = NhAssistantSkillApplyModes.Shadow })).Success);
        var changed = await SaveTriggerAsync(host, DataTrigger, Trigger() with { DebounceSeconds = 30, ExpectedVersion = 1 });
        Assert.Equal((4, false, (NhAssistantDataTriggerDto?)null), (changed.Data!.Version, changed.Data.Shadow, changed.Data.Candidate));
        Assert.Equal((4, TimeSpan.FromSeconds(30)), (State(host).Triggers[DataTrigger].DataVersion, State(host).Triggers[DataTrigger].Debounce));
        Assert.Empty(State(host).TriggerCandidates);

        // Removed, its versions stay as history and no longer hold the skill.
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await ConfigureAsync(host, configuration => configuration.RemoveTriggerAsync(AgentId, DataTrigger, 3, Admin))));
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveTriggerAsync(AgentId, DataTrigger, 4, Admin))).Success);
        Assert.False(State(host).Triggers.ContainsKey(DataTrigger));
        Assert.Empty((await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!.DataTriggers);
        Assert.Equal(
            [(4, NhAssistantSkillDefinitionStatuses.Superseded), (3, NhAssistantSkillDefinitionStatuses.Discarded), (2, NhAssistantSkillDefinitionStatuses.Discarded), (1, NhAssistantSkillDefinitionStatuses.Superseded)],
            (await ConfigureAsync(host, configuration => configuration.GetTriggerVersionsAsync(AgentId, DataTrigger))).Select(item => (item.Version, item.Status)));
        Assert.Equal(NhAssistantErrorCodes.TriggerNotFound, Code(await ConfigureAsync(host, configuration => configuration.RemoveTriggerAsync(AgentId, DataTrigger, null, Admin))));

        // Added again under the same id, it starts over in shadow as the next version.
        var again = await SaveTriggerAsync(host, DataTrigger, Trigger());
        Assert.Equal((5, true), (again.Data!.Version, again.Data.Shadow));
        var changes = host.Business.Events.Where(item => item.Kind == NhAssistantAuditEventKind.AdminAgentTriggerDefinitionChanged).Select(item => item.ResultCode);
        Assert.Equal(["created", "live", "candidate", "discarded", "candidate", "changed", "removed", "created"], changes);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_candidate_schedule_ticks_into_the_shadow_threads_while_the_live_schedule_keeps_working(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await AddLiveSkillAsync(host);
        Assert.True((await SaveTriggerAsync(host, WeeklyTrigger, Weekly("Weekly check"))).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(AgentId, WeeklyTrigger, false, Admin))).Success);
        Assert.True((await SaveTriggerAsync(host, WeeklyTrigger, Weekly("Weekly check, with the open tasks") with { Cron = "0 8 * * 1", ExpectedVersion = 1, Apply = NhAssistantSkillApplyModes.Shadow })).Success);
        Assert.Equal("0 8 * * 1", State(host).TriggerCandidates[WeeklyTrigger].Cron);

        var tick = new DateTimeOffset(2026, 10, 19, 6, 0, 10, TimeSpan.Zero);
        await using var scope = host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>();
        var liveReceipts = await job.RunAsync(WeeklyTrigger, tick, CancellationToken.None);
        var candidateReceipts = await job.RunCandidateAsync(WeeklyTrigger, tick, CancellationToken.None);

        Assert.Equal(2, liveReceipts.Count);
        Assert.Equal(2, candidateReceipts.Count);
        foreach (var receipt in liveReceipts)
        {
            var run = (await Runs(host).FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
            Assert.Equal((false, (int?)1, false, NhAssistantTriggerKinds.Schedule), (run.Shadow, run.TriggerVersion, run.TriggerCandidate, run.TriggerKind));
        }
        foreach (var receipt in candidateReceipts)
        {
            Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
            var run = (await Runs(host).FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
            Assert.Equal((true, (int?)2, true, NhAssistantTriggerKinds.Schedule), (run.Shadow, run.TriggerVersion, run.TriggerCandidate, run.TriggerKind));
            Assert.EndsWith(":trigger-candidate-2", run.TriggerEventKey, StringComparison.Ordinal);
        }

        // The same tick again starts nothing new; without a candidate the candidate's job does nothing.
        Assert.All(await job.RunCandidateAsync(WeeklyTrigger, tick, CancellationToken.None), receipt => Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, receipt.Outcome));
        Assert.True((await ConfigureAsync(host, configuration => configuration.DiscardTriggerCandidateAsync(AgentId, WeeklyTrigger, 2, Admin))).Success);
        Assert.Empty(await job.RunCandidateAsync(WeeklyTrigger, tick.AddDays(7), CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_created_to_start_in_shadow_works_in_shadow_once_its_run_settings_are_saved_in_the_same_request(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var project = Guid.NewGuid();

        // One request creates the agent, saves its run settings and a live trigger, as an import does.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            var created = await configuration.CreateAgentAsync(
                new NhAssistantAgentRequest(Follower, "Project follower", "Follows up changed projects.", "Follow up the project.", ["projects.*"], NhAiAutonomyLevel.Execute)
                {
                    StartsInShadow = true
                },
                Admin);
            Assert.True(created.Success);
            Assert.True((await configuration.SaveRunSettingsAsync(
                Follower,
                new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First, RuleHandler: "project-follow-up"),
                Admin)).Success);
            Assert.True((await configuration.SaveTriggerAsync(Follower, "follower-changed", Trigger() with { Skill = null }, Admin)).Success);
            Assert.True((await configuration.SetTriggerShadowAsync(Follower, "follower-changed", false, Admin)).Success);

            var receipt = Assert.Single(
                await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
                    .PublishAsync(new ProjectChanged(Guid.NewGuid(), project, "data first")),
                item => item.TriggerId == "follower-changed");
            var run = (await Runs(host).FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
            Assert.True(run.Shadow);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
            var state = (await control.GetAsync(Follower))!;
            Assert.Equal((true, false, (bool?)true, Admin), (state.Shadow!.Shadow, state.Shadow.Configured, state.Shadow.Override, state.Shadow.ChangedBy));

            // An administrator puts it live like any agent.
            Assert.False((await control.SetShadowAsync(Follower, false, Admin, "Evaluated")).Data!.Shadow!.Shadow);
        }

        var live = Assert.Single(await PublishAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data second"), "follower-changed"));
        Assert.False((await Runs(host).FindRunAsync(live.RunId!.Value, CancellationToken.None))!.Shadow);
        var shadowChanges = host.Business.Events.Where(item => item.Kind == NhAssistantAuditEventKind.AdminAgentShadowChanged && item.AgentId == Follower).ToArray();
        Assert.Equal(["shadow", "live"], shadowChanges.Select(item => item.ResultCode));

        // Without the choice an agent created at runtime starts live, as before.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            Assert.True((await configuration.CreateAgentAsync(
                new NhAssistantAgentRequest("project-helper", "Project helper", "Helps.", "Help.", ["projects.*"], NhAiAutonomyLevel.Execute),
                Admin)).Success);
            Assert.True((await configuration.SaveRunSettingsAsync(
                "project-helper",
                new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First, RuleHandler: "project-follow-up"),
                Admin)).Success);
            var state = (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().GetAsync("project-helper"))!;
            Assert.Equal((false, (bool?)null), (state.Shadow!.Shadow, state.Shadow.Override));
        }
    }

    private static async Task AddLiveSkillAsync(AssistantTestHost host)
    {
        Assert.True((await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            AgentId,
            FollowUp,
            new NhAssistantDataSkillRequest("Follows up changed projects by rule.", RuleHandler: "project-follow-up"),
            Admin))).Success);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().SetSkillShadowAsync(AgentId, FollowUp, false, Admin)).Success);
    }

    /// <summary>
    /// The follow-up skill, live in version 1, and its live trigger in version 1.
    /// </summary>
    private static async Task AddLiveTriggerAsync(AssistantTestHost host)
    {
        await AddLiveSkillAsync(host);
        Assert.True((await SaveTriggerAsync(host, DataTrigger, Trigger())).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(AgentId, DataTrigger, false, Admin))).Success);
    }

    private static NhAssistantDataTriggerRequest Trigger(IReadOnlyList<string>? filters = null)
    {
        return new NhAssistantDataTriggerRequest(
            NhAssistantDataTriggerSources.Event,
            FollowUp,
            Event: "project.changed",
            Conflict: NhAssistantTriggerConflictNames.Queue,
            Filters: filters);
    }

    private static NhAssistantDataTriggerRequest Weekly(string message)
    {
        return new NhAssistantDataTriggerRequest(
            NhAssistantDataTriggerSources.Schedule,
            FollowUp,
            Cron: "0 7 * * 1",
            SubjectSet: "test-projects",
            Message: message);
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, ProjectChanged evt, string triggerId = DataTrigger)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
        return receipts.Where(receipt => receipt.TriggerId == triggerId).ToArray();
    }

    private static Task<AssistantThread?> ShadowThreadAsync(AssistantTestHost host, Guid projectId)
    {
        return Runs(host).FindThreadAsync(
            NhAiSubjectInfo.CreateScopeKey(null, null),
            AgentId,
            new NhAiSubjectRef("project", projectId.ToString("N")),
            CancellationToken.None,
            shadow: true);
    }

    private static async Task<IReadOnlyList<AssistantRun>> RunsOfAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.AsNoTracking().Where(run => run.ConversationId == conversationId).ToListAsync();
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddRuleHandler<TestSkillRules>("project-follow-up", "Follows up a project by rule.")
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .When(change => change.Change.StartsWith("data", StringComparison.Ordinal))
                    .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                    .Message(change => change.Change)
                    .Dedupe(change => change.EventId.ToString("N"))
                    .Filter(NotMinor, "Skips minor changes.", (change, _) => !change.Change.Contains("minor", StringComparison.Ordinal)))
                .AddSubjectSet("test-projects", "Two test projects", TestProjectsAsync)
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseRuleHandler<TestRules>()));
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> TestProjectsAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new NhAiSubjectRef("project", "11111111111111111111111111111111");
        yield return new NhAiSubjectRef("project", "22222222222222222222222222222222");
    }

    private static Task<TaskResult<NhAssistantDataTriggerDto>> SaveTriggerAsync(AssistantTestHost host, string triggerId, NhAssistantDataTriggerRequest request)
    {
        return ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(AgentId, triggerId, request, Admin));
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The one field a validation failure names.
    /// </summary>
    private static string? Field(TaskResult result)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        return result.GetResultItems()
            .Select(item => item.Name)
            .Where(name => name.StartsWith("field:", StringComparison.Ordinal))
            .Select(name => name["field:".Length..])
            .Single();
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }
}
