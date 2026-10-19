using System.Runtime.CompilerServices;
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
/// Run configuration as data: skills and triggers defined at runtime on an agent configured in code,
/// checked against the building blocks in code, starting in shadow and followed by every host.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAgentRunConfigurationTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ChatOnlyAgent = "project-chat-only";
    private const string Review = "review";
    private const string FollowUp = "follow-up";
    private const string DataTrigger = "data-changed";
    private const string WeeklyTrigger = "weekly-check";
    private const string Admin = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_skill_and_its_trigger_start_in_shadow_and_work_live_once_both_are_put_live(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var skillRules = host.Services.GetRequiredService<TestSkillRuleScript>();

        var skill = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            AgentId,
            FollowUp,
            new NhAssistantDataSkillRequest("Follows up changed projects by rule.", "Say which task comes next.", ["projects.search"], RuleHandler: "project-follow-up"),
            Admin));
        Assert.True(skill.Success);
        Assert.Equal(1, skill.Data!.Version);
        Assert.True(skill.Data.InEffect);
        Assert.Equal(NhAssistantAgentCatalog.ComputeHash("Say which task comes next."), skill.Data.InstructionsHash);
        var options = State(host).AgentRuns[AgentId].Skills[FollowUp];
        Assert.Equal(1, options.DataVersion);
        Assert.True(options.Shadow);
        Assert.Equal(typeof(TestSkillRules), options.RuleHandlerType);
        Assert.Equal(new[] { "projects.search" }, options.ToolSelectors);
        Assert.Equal("Say which task comes next.", options.Instructions!.Content);
        // The options in code stay as they are.
        Assert.False(State(host).CodeAgentRuns[AgentId].Skills.ContainsKey(FollowUp));

        var trigger = await ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(
            AgentId,
            DataTrigger,
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, FollowUp, Event: "project.changed", Conflict: NhAssistantTriggerConflictNames.Queue),
            Admin));
        Assert.True(trigger.Success);
        Assert.True(trigger.Data!.Shadow);
        Assert.Equal(120, trigger.Data.DebounceSeconds);

        // In shadow: the run works in the agent's shadow thread, in the data skill, by its rules.
        var first = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "data first"));
        await ExecuteAsync(host, first.RunId!.Value);
        var firstRun = (await Runs(host).FindRunAsync(first.RunId.Value, CancellationToken.None))!;
        Assert.True(firstRun.Shadow);
        Assert.Equal(FollowUp, firstRun.SkillId);
        Assert.Equal(NhAssistantRunStatuses.Completed, firstRun.Status);
        var context = Assert.Single(skillRules.Contexts);
        Assert.Equal((DataTrigger, true), (context.TriggerId, context.Shadow));

        // A live trigger of a skill in shadow still works in shadow.
        var live = await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(AgentId, DataTrigger, false, Admin, "Looks right"));
        Assert.False(live.Data!.Shadow);
        var second = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "data second"));
        Assert.True((await Runs(host).FindRunAsync(second.RunId!.Value, CancellationToken.None))!.Shadow);

        // With the skill live too, the run works in the live thread.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>()
                .SetSkillShadowAsync(AgentId, FollowUp, false, Admin, "Evaluated");
            Assert.False(state.Data!.Shadow.Shadow);
            Assert.True(state.Data.Shadow.Configured);
        }
        var third = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "data third"));
        var thirdRun = (await Runs(host).FindRunAsync(third.RunId!.Value, CancellationToken.None))!;
        Assert.False(thirdRun.Shadow);
        Assert.Equal(FollowUp, thirdRun.SkillId);

        var skillEvent = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged);
        Assert.Equal(("created", FollowUp, 1, Admin), (skillEvent.ResultCode, skillEvent.SkillId, skillEvent.AgentVersion, skillEvent.ActorId));
        Assert.Equal(skill.Data.InstructionsHash, skillEvent.InstructionsHash);
        var triggerEvents = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentTriggerDefinitionChanged).ToArray();
        Assert.Equal(["created", "live"], triggerEvents.Select(evt => evt.ResultCode));
        Assert.All(triggerEvents, evt => Assert.Equal((DataTrigger, FollowUp), (evt.TriggerId, evt.SkillId)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Saving_checks_the_data_against_the_code_and_keeps_every_version(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);

        Assert.Equal("tools", Field(await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up.", Tools: ["orders.*"]))));
        Assert.Equal("profile", Field(await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up.", Profile: "missing-chat"))));
        Assert.Equal("ruleHandler", Field(await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up.", RuleHandler: "missing"))));
        Assert.Equal("approvalPolicy", Field(await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up.", ApprovalPolicy: "missing"))));
        Assert.Equal("description", Field(await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest(new string('x', 301)))));
        Assert.Equal(NhAssistantErrorCodes.SkillInCode, Code(await SaveSkillAsync(host, Review, new NhAssistantDataSkillRequest("Reviews."))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotConfigured, Code(await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(ChatOnlyAgent, FollowUp, new NhAssistantDataSkillRequest("Follows up."), Admin))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await ConfigureAsync(host, configuration => configuration.SaveSkillAsync("missing-agent", FollowUp, new NhAssistantDataSkillRequest("Follows up."), Admin))));

        var first = await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up.", RuleHandler: "project-follow-up", ApprovalPolicy: "project-review", Profile: "project-chat"));
        Assert.True(first.Success);
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up again."))));
        var second = await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up completed tasks.", RuleHandler: "project-follow-up", ExpectedVersion: 1, Reason: "Sharper"));
        Assert.Equal(2, second.Data!.Version);
        Assert.Equal("Follows up completed tasks.", State(host).AgentRuns[AgentId].Skills[FollowUp].Description);
        Assert.Equal(2, State(host).AgentRuns[AgentId].Skills[FollowUp].DataVersion);

        Assert.Equal("event", Field(await SaveTriggerAsync(host, DataTrigger, new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, FollowUp, Event: "project.removed"))));
        Assert.Equal("skill", Field(await SaveTriggerAsync(host, DataTrigger, new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, "missing", Event: "project.changed"))));
        Assert.Equal("cron", Field(await SaveTriggerAsync(host, WeeklyTrigger, new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Schedule, FollowUp, Cron: "every monday", SubjectSet: "test-projects", Message: "Weekly check"))));
        Assert.Equal("subjectSet", Field(await SaveTriggerAsync(host, WeeklyTrigger, new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Schedule, FollowUp, Cron: "0 7 * * 1", SubjectSet: "missing", Message: "Weekly check"))));
        Assert.Equal(NhAssistantErrorCodes.TriggerInCode, Code(await SaveTriggerAsync(host, "task-changed", new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, Event: "project.changed"))));

        var trigger = await SaveTriggerAsync(host, DataTrigger, new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, FollowUp, Event: "project.changed"));
        Assert.True(trigger.Success);
        var changed = await SaveTriggerAsync(host, DataTrigger, new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, FollowUp, Event: "project.changed", DebounceSeconds: 30, ExpectedVersion: 1));
        Assert.Equal((2, 30, true), (changed.Data!.Version, changed.Data.DebounceSeconds, changed.Data.Shadow));
        Assert.Equal(TimeSpan.FromSeconds(30), State(host).Triggers[DataTrigger].Debounce);

        Assert.Equal(NhAssistantErrorCodes.SkillInUse, Code(await ConfigureAsync(host, configuration => configuration.RemoveSkillAsync(AgentId, FollowUp, 2, Admin))));
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveTriggerAsync(AgentId, DataTrigger, 2, Admin))).Success);
        Assert.False(State(host).Triggers.ContainsKey(DataTrigger));
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveSkillAsync(AgentId, FollowUp, 2, Admin, "No longer needed"))).Success);
        Assert.False(State(host).AgentRuns[AgentId].Skills.ContainsKey(FollowUp));

        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!;
        Assert.Empty(view.DataSkills);
        Assert.Empty(view.DataTriggers);
        Assert.Equal([Review], view.CodeSkills.Select(item => item.SkillId));
        Assert.Contains(view.CodeTriggers, item => item.TriggerId == "task-changed" && item.Source == NhAssistantDataTriggerSources.Event);
        var versions = await ConfigureAsync(host, configuration => configuration.GetSkillVersionsAsync(AgentId, FollowUp));
        Assert.Equal([(2, NhAssistantSkillDefinitionStatuses.Superseded), (1, NhAssistantSkillDefinitionStatuses.Superseded)], versions.Select(item => (item.Version, item.Status)));
        Assert.Equal("Sharper", versions[0].Reason);

        // A skill added again under the same id starts over in shadow.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
            var again = await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up.", RuleHandler: "project-follow-up"));
            Assert.Equal(3, again.Data!.Version);
            var state = await control.GetAsync(AgentId);
            Assert.True(Assert.Single(state!.Skills, item => item.SkillId == FollowUp).Shadow.Shadow);
        }

        var catalog = host.Services.CreateScope().ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetCatalog();
        Assert.Equal(["project.changed"], catalog.TriggerEvents.Select(item => item.Name));
        Assert.Equal(["test-projects"], catalog.SubjectSets.Select(item => item.Name));
        Assert.Equal(["project-follow-up"], catalog.RuleHandlers.Select(item => item.Name));
        Assert.Contains("project-review", catalog.ApprovalPolicies);
        Assert.Contains("project-chat", catalog.Profiles);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Another_host_follows_a_change_through_the_stamp_and_data_that_no_longer_fits_is_reported(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var configurations = host.Services.GetRequiredService<NhAssistantRunConfigurations>();
        await configurations.EnsureCurrentAsync(CancellationToken.None);
        Assert.NotNull(State(host).Snapshot);

        // Another host, which seeded its agents, stores a skill and a trigger whose event this
        // host's code no longer has.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().GetAsync(AgentId));
        }
        var store = host.Services.GetRequiredService<NhAssistantRunConfigurationStore>();
        var now = DateTimeOffset.UtcNow;
        var skill = await store.AddSkillVersionAsync(
            new AssistantAgentSkillDefinition { AgentId = AgentId, SkillId = FollowUp, Description = "Follows up.", RuleHandlerName = "project-follow-up", CreatedAt = now, CreatedBy = Admin },
            null,
            CancellationToken.None);
        Assert.True(skill.Succeeded);
        var stored = await store.SaveTriggerAsync(
            new AssistantAgentTriggerDefinition { Id = DataTrigger, AgentId = AgentId, SkillId = FollowUp, Source = NhAssistantDataTriggerSources.Event, EventName = "project.removed", Conflict = NhAssistantTriggerConflictNames.Queue, UpdatedAt = now, UpdatedBy = Admin },
            null,
            CancellationToken.None);
        Assert.True(stored.Succeeded);
        Assert.False(State(host).AgentRuns[AgentId].Skills.ContainsKey(FollowUp));

        await Task.Delay(TimeSpan.FromMilliseconds(1_100));
        await configurations.EnsureCurrentAsync(CancellationToken.None);

        Assert.True(State(host).AgentRuns[AgentId].Skills.ContainsKey(FollowUp));
        Assert.False(State(host).Triggers.ContainsKey(DataTrigger));
        var problem = Assert.Single(configurations.Problems);
        Assert.Equal(new NhAssistantRunConfigurationProblem(AgentId, NhAssistantRunConfigurationProblems.TriggerKind, DataTrigger, NhAssistantRunConfigurationProblems.UnknownEvent), problem);
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!;
        var trigger = Assert.Single(view.DataTriggers);
        Assert.False(trigger.InEffect);
        Assert.Equal(NhAssistantRunConfigurationProblems.UnknownEvent, trigger.Problem);
        Assert.True(Assert.Single(view.DataSkills).InEffect);
        Assert.Contains(host.Logs.Messages, message => message.Contains("out of effect (unknown-event)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_schedule_starts_its_skill_for_every_subject_of_its_set(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        Assert.True((await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up.", RuleHandler: "project-follow-up"))).Success);
        var saved = await SaveTriggerAsync(
            host,
            WeeklyTrigger,
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Schedule, FollowUp, Cron: "0 7 * * 1", TimeZone: "Europe/Amsterdam", SubjectSet: "test-projects", Message: "Weekly check"));
        Assert.True(saved.Success);
        Assert.Equal(NhAssistantTriggerConflictNames.Ignore, saved.Data!.Conflict);
        var definition = State(host).Triggers[WeeklyTrigger];
        Assert.Equal((NhAssistantTriggerSource.Schedule, "0 7 * * 1", 1), (definition.Source, definition.Cron, definition.DataVersion));

        await using var scope = host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>();
        var receipts = await job.RunAsync(WeeklyTrigger, new DateTimeOffset(2026, 10, 19, 5, 0, 10, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal(2, receipts.Count);
        foreach (var receipt in receipts)
        {
            Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
            var run = (await Runs(host).FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
            Assert.True(run.Shadow);
            Assert.Equal(FollowUp, run.SkillId);
            Assert.Equal(WeeklyTrigger, run.TriggerId);
        }
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
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(ChatOnlyAgent))
                .AddRuleHandler<TestSkillRules>("project-follow-up", "Follows up a project by rule.")
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .When(change => change.Change.StartsWith("data", StringComparison.Ordinal))
                    .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                    .Message(change => change.Change)
                    .Dedupe(change => change.EventId.ToString("N")))
                .AddSubjectSet("test-projects", "Two test projects", TestProjectsAsync)
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>()
                    .Skill(Review, skill => skill
                        .Description("Reviews the project by rule.")
                        .UseRuleHandler<TestSkillRules>()))
                .AddTrigger<ProjectChanged>("task-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .When(evt => evt.Change.StartsWith("task", StringComparison.Ordinal))
                    .Skill(Review)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)));
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> TestProjectsAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new NhAiSubjectRef("project", "11111111111111111111111111111111");
        yield return new NhAiSubjectRef("project", "22222222222222222222222222222222");
    }

    private static Task<TaskResult<NhAssistantDataSkillDto>> SaveSkillAsync(AssistantTestHost host, string skillId, NhAssistantDataSkillRequest request)
    {
        return ConfigureAsync(host, configuration => configuration.SaveSkillAsync(AgentId, skillId, request, Admin));
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

    /// <summary>
    /// Publishes a change of the project and returns the receipt of the data trigger.
    /// </summary>
    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, Guid projectId, string change)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, change));
        return receipts.Where(receipt => receipt.TriggerId == DataTrigger).ToArray();
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
