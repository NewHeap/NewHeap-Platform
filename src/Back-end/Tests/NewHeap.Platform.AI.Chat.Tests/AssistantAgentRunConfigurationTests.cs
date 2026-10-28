using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Scripting;
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
public sealed class AssistantAgentRunConfigurationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ChatOnlyAgent = "project-chat-only";
    private const string Review = "review";
    private const string FollowUp = "follow-up";
    private const string DataTrigger = "data-changed";
    private const string WeeklyTrigger = "weekly-check";
    private const string Admin = "admin-1";
    private const string Follower = "project-follower";
    private const string FollowerTrigger = "follower-changed";

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
        // Its subject is chosen by a function, so its type is known only when it fires.
        Assert.Contains(view.CodeTriggers, item => item.TriggerId == "task-changed" && item.Source == NhAssistantDataTriggerSources.Event && item.SubjectType is null);
        // How the agent runs, as its code says, for the administration's flow view.
        // Settings it leaves to their defaults show those defaults.
        Assert.Equal(
            new NhAssistantCodeRunSettingsDto(NhAssistantAccountabilities.Agent, false, "project-review", true)
            {
                RetryCount = 3,
                MaxConcurrentRuns = 4,
                MaxRunsPerSubjectPerDay = 10
            },
            view.CodeRunSettings);
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
        Assert.Equal(["project-check", "project-follow-up"], catalog.RuleHandlers.Select(item => item.Name));
        Assert.Contains("project-review", catalog.ApprovalPolicies);
        Assert.Contains("project-chat", catalog.Profiles);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Another_host_follows_a_change_through_the_stamp_and_data_that_no_longer_fits_is_reported(AssistantTestProvider provider)
    {
        // The host reads the stamp at every check instead of once a second, so the test need not wait.
        await using var host = await CreateHostAsync(provider, new NhAssistantTiming { RunConfigurationCheckInterval = TimeSpan.Zero });
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

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administration_agent_runs_by_its_run_settings_with_the_rights_of_its_technical_account(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var outcomes = ProposeHoldAndRecord(host);
        await CreateFollowerAsync(host);

        var chatOnly = (await ConfigureAsync(host, configuration => configuration.GetAsync(Follower)))!;
        Assert.Equal(NhAssistantAgentSources.Admin, chatOnly.Source);
        Assert.Null(chatOnly.RunSettings);
        // Its run settings are data; it has none in code.
        Assert.Null(chatOnly.CodeRunSettings);
        Assert.Equal(NhAssistantErrorCodes.AgentNotConfigured, Code(await SaveFollowerTriggerAsync(host)));
        Assert.Equal(NhAssistantErrorCodes.ServiceAccountNotFound, Code(await SaveSettingsAsync(host, Settings("service-9"))));

        // The agent answers to the technical account and works by the named rules; it has no grants.
        var settings = await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First));
        Assert.True(settings.Success);
        Assert.Equal((1, NhAssistantAccountabilities.ServiceAccount, true), (settings.Data!.Version, settings.Data.Accountability, settings.Data.InEffect));
        var options = State(host).AgentRuns[Follower];
        Assert.Equal((true, (int?)1, NhAiAgentAccountability.ServiceAccount, typeof(TestSkillRules)), (options.ActWithAccountableRights, options.DataVersion, options.Accountability, options.RuleHandlerType));
        Assert.Empty(options.Grants);
        Assert.True((await SaveFollowerTriggerAsync(host)).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(Follower, FollowerTrigger, false, Admin))).Success);

        // Its rules propose with the account's rights: the proposal records them as the grant source.
        var project = Guid.NewGuid();
        var first = Assert.Single(await PublishAsync(host, project, "data follow", FollowerTrigger));
        await ExecuteAsync(host, first.RunId!.Value);
        var run = (await Runs(host).FindRunAsync(first.RunId.Value, CancellationToken.None))!;
        Assert.Equal((NhAssistantRunStatuses.Completed, KnownAccountPrincipalFactory.First, false), (run.Status, run.AccountableOwnerId, run.Shadow));
        Assert.Equal(NhAssistantProposeOutcome.Created, Assert.Single(outcomes).Outcome);
        var approval = Assert.Single(await ApprovalsAsync(host, first.ConversationId!.Value));
        Assert.Equal((NhAiGrantSource.Accountable, NhAssistantApprovalStatuses.Pending), (approval.GrantSource, approval.Status));

        // Answering to itself, the agent has no rights to lend: its thread follows and the rules are refused.
        var itself = await SaveSettingsAsync(host, Settings(null) with { Accountability = NhAssistantAccountabilities.Agent, ExpectedVersion = 1 });
        Assert.Equal(2, itself.Data!.Version);
        var second = Assert.Single(await PublishAsync(host, project, "data again", FollowerTrigger));
        Assert.Equal(first.ConversationId, second.ConversationId);
        await ExecuteAsync(host, second.RunId!.Value);
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(NhAssistantProposeOutcome.Refused, outcomes[1].Outcome);
        var thread = (await Runs(host).FindThreadByConversationAsync(second.ConversationId!.Value, CancellationToken.None))!;
        Assert.Equal((NhAssistantAccountabilities.Agent, "assistant-agent:" + Follower), (thread.Accountability, thread.AccountableOwnerId));

        var changes = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentRunSettingsChanged).ToArray();
        Assert.Equal(["created", "changed"], changes.Select(evt => evt.ResultCode));
        Assert.Equal([1, 2], changes.Select(evt => evt.AgentVersion));

        // Run settings stay while data triggers belong to the agent.
        Assert.Equal(NhAssistantErrorCodes.RunSettingsInUse, Code(await ConfigureAsync(host, configuration => configuration.RemoveRunSettingsAsync(Follower, 2, Admin))));
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveTriggerAsync(Follower, FollowerTrigger, null, Admin))).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.RemoveRunSettingsAsync(Follower, 2, Admin, "Only chats again"))).Success);
        Assert.False(State(host).AgentRuns.ContainsKey(Follower));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_rules_of_an_administration_agent_with_propose_autonomy_propose_while_the_turn_withholds_the_script_tool(AssistantTestProvider provider)
    {
        // As the sample's project starter and project follow-up: an agent created in the administration
        // that only proposes and names only the status tool. The host registers the script tool, which
        // needs no selector and which the application does not allow agents created in the
        // administration, so the run withholds it; the rules still propose with the status tool.
        await using var host = await CreateHostAsync(provider, scriptTool: true);
        var outcomes = ProposeHoldAndRecord(host);
        await CreateFollowerAsync(host, NhAiAutonomyLevel.Propose, ["projects.change-status"]);
        Assert.True((await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First))).Success);
        Assert.True((await SaveFollowerTriggerAsync(host)).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(Follower, FollowerTrigger, false, Admin))).Success);
        var scripts = (await CompositionStatusAsync(host, Follower)).Features.Single(feature => feature.Feature == NhAiCompositionFeatures.NameOf(NhAiCompositionFeature.Scripts));
        Assert.All(scripts.Channels, channel => Assert.Equal(NhAssistantCompositionReasons.NotAllowedForDataAgents, channel.Reason));

        var receipt = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "data follow", FollowerTrigger));
        await ExecuteAsync(host, receipt.RunId!.Value);

        var run = (await Runs(host).FindRunAsync(receipt.RunId.Value, CancellationToken.None))!;
        Assert.Equal((NhAssistantRunStatuses.Completed, NhAssistantRunKinds.Rule), (run.Status, run.Kind));
        Assert.Equal(NhAssistantProposeOutcome.Created, Assert.Single(outcomes).Outcome);
        var approval = Assert.Single(await ApprovalsAsync(host, receipt.ConversationId!.Value));
        Assert.Equal((NhAiGrantSource.Accountable, NhAssistantApprovalStatuses.Pending), (approval.GrantSource, approval.Status));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Run_settings_are_checked_against_the_code_and_give_an_administration_agent_skills(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await CreateFollowerAsync(host);

        Assert.Equal(NhAssistantErrorCodes.RunSettingsInCode, Code(await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(AgentId, Settings(null), Admin))));
        Assert.Equal("allowGatewayReads", Field(await SaveSettingsAsync(host, Settings(null) with { Accountability = NhAssistantAccountabilities.Agent, AllowGatewayReads = true })));
        Assert.Equal("serviceAccountActorId", Field(await SaveSettingsAsync(host, Settings(null))));
        Assert.Equal("approvalPolicy", Field(await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First) with { ApprovalPolicy = "missing" })));
        Assert.Equal("ruleHandler", Field(await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First) with { RuleHandler = "missing" })));
        Assert.Equal("triageProfile", Field(await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First) with { TriageProfile = "missing-chat" })));
        Assert.Equal("maxConcurrentRuns", Field(await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First) with { MaxConcurrentRuns = 0 })));

        Assert.True((await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First) with { DailyToolCalls = 5, DailyTokens = 1_000, MaxTokensPerRun = 500 })).Success);
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await SaveSettingsAsync(host, Settings(KnownAccountPrincipalFactory.First))));
        var options = State(host).AgentRuns[Follower];
        Assert.Equal(new NhAssistantAgentBudget(5, 1_000), options.DailyBudget);
        Assert.Equal(500L, options.MaxTokensPerRun);

        // A data skill stays within the administration agent's own tool selectors.
        Assert.Equal("tools", Field(await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            Follower, FollowUp, new NhAssistantDataSkillRequest("Follows up.", Tools: ["orders.*"]), Admin))));
        var skill = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            Follower, FollowUp, new NhAssistantDataSkillRequest("Follows up.", Tools: ["projects.search"], RuleHandler: "project-follow-up"), Admin));
        Assert.True(skill.Success);
        Assert.True(State(host).AgentRuns[Follower].Skills[FollowUp].Shadow);
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(Follower)))!;
        Assert.Equal(1, view.RunSettings!.Version);
        Assert.Equal(FollowUp, Assert.Single(view.DataSkills).SkillId);
        Assert.Equal(new[] { "projects.*" }, view.ToolSelectors);
        Assert.Empty(view.Problems);

        // Deleting the agent takes its run configuration along.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().DeleteAsync(Follower, Admin, CancellationToken.None)).Success);
        }
        Assert.False(State(host).AgentRuns.ContainsKey(Follower));
        Assert.Null(await ConfigureAsync(host, configuration => configuration.GetAsync(Follower)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_changed_skill_runs_as_a_candidate_in_shadow_next_to_the_live_version_and_is_promoted(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var liveRules = host.Services.GetRequiredService<TestSkillRuleScript>();
        var candidateRules = host.Services.GetRequiredService<TestRuleScript>();
        await AddLiveFollowUpAsync(host);

        // A new skill has no live version to evaluate a candidate next to.
        Assert.Equal("apply", Field(await SaveSkillAsync(host, "notes", new NhAssistantDataSkillRequest("Notes.", Apply: NhAssistantSkillApplyModes.Shadow))));

        // The changed skill works by other rules; saved for shadow, it becomes the candidate.
        var candidate = await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest(
            "Follows up by the project check.", RuleHandler: "project-check", ExpectedVersion: 1, Apply: NhAssistantSkillApplyModes.Shadow));
        Assert.Equal(2, candidate.Data!.Version);
        Assert.Equal(1, State(host).AgentRuns[AgentId].Skills[FollowUp].DataVersion);
        Assert.Equal(2, State(host).AgentRuns[AgentId].SkillCandidates[FollowUp].DataVersion);
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!;
        Assert.Equal((1, 2), (Assert.Single(view.DataSkills).Version, view.DataSkills[0].Candidate!.Version));

        // One event: the live version works live, the candidate in the shadow thread.
        var project = Guid.NewGuid();
        var evt = new ProjectChanged(Guid.NewGuid(), project, "data first");
        var receipt = Assert.Single(await PublishEventAsync(host, evt), item => item.TriggerId == DataTrigger);
        var liveRun = (await Runs(host).FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
        Assert.Equal((false, 1, false), (liveRun.Shadow, liveRun.SkillVersion, liveRun.SkillCandidate));
        var shadowThread = (await ShadowThreadAsync(host, project))!;
        var candidateRun = await LatestRunAsync(host, shadowThread.ConversationId);
        Assert.Equal((true, 2, true, FollowUp), (candidateRun.Shadow, candidateRun.SkillVersion, candidateRun.SkillCandidate, candidateRun.SkillId));
        await ExecuteAsync(host, liveRun.Id);
        await ExecuteAsync(host, candidateRun.Id);
        Assert.False(Assert.Single(liveRules.Contexts).Shadow);
        Assert.True(Assert.Single(candidateRules.Contexts).Shadow);

        // A repeated delivery starts neither again.
        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, Assert.Single(await PublishEventAsync(host, evt), item => item.TriggerId == DataTrigger).Outcome);
        Assert.Single(await RunsOfAsync(host, shadowThread.ConversationId));

        var comparison = await ConfigureAsync(host, configuration => configuration.CompareSkillCandidateAsync(AgentId, FollowUp));
        Assert.Equal((1, 1, 1, 1), (comparison.Data!.Live.Runs, comparison.Data.Candidate.Runs, comparison.Data.Candidate.Completed, comparison.Data.PairedEvents));

        // Promoted, the candidate is the live version and no second run follows.
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await ConfigureAsync(host, configuration => configuration.PromoteSkillCandidateAsync(AgentId, FollowUp, 3, Admin))));
        var promoted = await ConfigureAsync(host, configuration => configuration.PromoteSkillCandidateAsync(AgentId, FollowUp, 2, Admin, "The check is better"));
        Assert.Equal(2, promoted.Data!.Version);
        Assert.Equal(2, State(host).AgentRuns[AgentId].Skills[FollowUp].DataVersion);
        Assert.Empty(State(host).AgentRuns[AgentId].SkillCandidates);
        var next = Assert.Single(await PublishEventAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data second")), item => item.TriggerId == DataTrigger);
        await ExecuteAsync(host, next.RunId!.Value);
        Assert.Equal(2, candidateRules.Contexts.Count);
        Assert.Single(await RunsOfAsync(host, shadowThread.ConversationId));
        Assert.Equal(NhAssistantErrorCodes.SkillCandidateNotFound, Code(await ConfigureAsync(host, configuration => configuration.CompareSkillCandidateAsync(AgentId, FollowUp))));

        var versions = await ConfigureAsync(host, configuration => configuration.GetSkillVersionsAsync(AgentId, FollowUp));
        Assert.Equal([(2, NhAssistantSkillDefinitionStatuses.Live), (1, NhAssistantSkillDefinitionStatuses.Superseded)], versions.Select(item => (item.Version, item.Status)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_handler_shared_by_skills_reads_the_skill_version_and_candidate_it_runs_in(AssistantTestProvider provider)
    {
        // TestSkillRules runs the code skill review and, registered as project-follow-up, the data
        // skill follow-up: live in version 1 and as the candidate version 2.
        await using var host = await CreateHostAsync(provider);
        var rules = host.Services.GetRequiredService<TestSkillRuleScript>();
        await AddLiveFollowUpAsync(host);
        Assert.True((await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest(
            "Follows up with a second look.", RuleHandler: "project-follow-up", ExpectedVersion: 1, Apply: NhAssistantSkillApplyModes.Shadow))).Success);

        var project = Guid.NewGuid();
        var live = Assert.Single(await PublishEventAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data first")), item => item.TriggerId == DataTrigger);
        var candidate = await LatestRunAsync(host, (await ShadowThreadAsync(host, project))!.ConversationId);
        var review = Assert.Single(await PublishAsync(host, Guid.NewGuid(), "task done", "task-changed"));

        var seen = new List<(string? SkillId, int? Version, bool? Candidate)>();
        foreach (var runId in new[] { review.RunId!.Value, live.RunId!.Value, candidate.Id })
        {
            await ExecuteAsync(host, runId);
            Assert.True(rules.Contexts.TryDequeue(out var context));
            seen.Add((context.SkillId, context.SkillVersion, context.SkillCandidate));
        }

        Assert.Equal([(Review, null, false), (FollowUp, 1, false), (FollowUp, 2, true)], seen);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_discarded_candidate_stays_as_history_and_its_queued_run_ends(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await AddLiveFollowUpAsync(host);
        Assert.True((await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest(
            "Follows up by the project check.", RuleHandler: "project-check", ExpectedVersion: 1, Apply: NhAssistantSkillApplyModes.Shadow))).Success);

        var project = Guid.NewGuid();
        Assert.Single(await PublishEventAsync(host, new ProjectChanged(Guid.NewGuid(), project, "data first")), item => item.TriggerId == DataTrigger);
        var shadowThread = (await ShadowThreadAsync(host, project))!;
        var queued = await LatestRunAsync(host, shadowThread.ConversationId);

        Assert.True((await ConfigureAsync(host, configuration => configuration.DiscardSkillCandidateAsync(AgentId, FollowUp, 2, Admin, "Not better"))).Success);
        Assert.Empty(State(host).AgentRuns[AgentId].SkillCandidates);
        await ExecuteAsync(host, queued.Id);
        var ended = (await Runs(host).FindRunAsync(queued.Id, CancellationToken.None))!;
        Assert.Equal((NhAssistantRunStatuses.Cancelled, NhAssistantErrorCodes.SkillVersionGone), (ended.Status, ended.FailureCode));
        Assert.Empty(host.Services.GetRequiredService<TestRuleScript>().Contexts);

        // A live change discards a candidate as well; both stay as history.
        Assert.True((await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest(
            "Follows up again.", RuleHandler: "project-check", ExpectedVersion: 1, Apply: NhAssistantSkillApplyModes.Shadow))).Success);
        Assert.True((await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest(
            "Follows up completed tasks.", RuleHandler: "project-follow-up", ExpectedVersion: 1))).Success);
        var versions = await ConfigureAsync(host, configuration => configuration.GetSkillVersionsAsync(AgentId, FollowUp));
        Assert.Equal(
            [(4, NhAssistantSkillDefinitionStatuses.Live), (3, NhAssistantSkillDefinitionStatuses.Discarded), (2, NhAssistantSkillDefinitionStatuses.Discarded), (1, NhAssistantSkillDefinitionStatuses.Superseded)],
            versions.Select(item => (item.Version, item.Status)));
        var changes = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged).Select(evt => evt.ResultCode);
        Assert.Equal(["created", "candidate", "discarded", "candidate", "changed"], changes);
    }

    /// <summary>
    /// The follow-up skill, live in version 1, and its live trigger.
    /// </summary>
    private static async Task AddLiveFollowUpAsync(AssistantTestHost host)
    {
        Assert.True((await SaveSkillAsync(host, FollowUp, new NhAssistantDataSkillRequest("Follows up changed projects by rule.", RuleHandler: "project-follow-up"))).Success);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().SetSkillShadowAsync(AgentId, FollowUp, false, Admin)).Success);
        }
        Assert.True((await SaveTriggerAsync(host, DataTrigger, new NhAssistantDataTriggerRequest(
            NhAssistantDataTriggerSources.Event, FollowUp, Event: "project.changed", Conflict: NhAssistantTriggerConflictNames.Queue))).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(AgentId, DataTrigger, false, Admin))).Success);
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishEventAsync(AssistantTestHost host, ProjectChanged evt)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
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

    private static async Task<AssistantRun> LatestRunAsync(AssistantTestHost host, Guid conversationId)
    {
        return (await RunsOfAsync(host, conversationId)).OrderByDescending(run => run.CreatedAt).First();
    }

    /// <summary>
    /// The skill rules propose to put the project on hold and record what became of it.
    /// </summary>
    private static List<NhAssistantRuleProposal> ProposeHoldAndRecord(AssistantTestHost host)
    {
        var outcomes = new List<NhAssistantRuleProposal>();
        host.Services.GetRequiredService<TestSkillRuleScript>().Run = async (context, cancellationToken) =>
        {
            outcomes.Add(await context.ProposeAsync(
                "projects.change-status",
                new ProjectStatusInput(Guid.Parse(context.Subject.Subject.Id), "OnHold"),
                null,
                cancellationToken));
            return NhAssistantRuleResult.Done("Followed up the project.");
        };
        return outcomes;
    }

    private static async Task CreateFollowerAsync(
        AssistantTestHost host,
        NhAiAutonomyLevel autonomy = NhAiAutonomyLevel.Execute,
        IReadOnlyList<string>? toolSelectors = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
            new NhAssistantAgentInput(
                Follower,
                "Project follower",
                "Follows up changed projects.",
                "Follow up the project.",
                toolSelectors ?? ["projects.*"],
                [],
                null,
                autonomy),
            Admin,
            CancellationToken.None);
        Assert.True(created.Success);
    }

    private static NhAssistantAgentRunSettingsRequest Settings(string? serviceAccount)
    {
        return new NhAssistantAgentRunSettingsRequest(
            NhAssistantAccountabilities.ServiceAccount,
            serviceAccount,
            ApprovalPolicy: "project-review",
            RuleHandler: "project-follow-up",
            Reason: "Follow up projects");
    }

    private static Task<TaskResult<NhAssistantAgentRunSettingsDto>> SaveSettingsAsync(AssistantTestHost host, NhAssistantAgentRunSettingsRequest request)
    {
        return ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(Follower, request, Admin));
    }

    private static Task<TaskResult<NhAssistantDataTriggerDto>> SaveFollowerTriggerAsync(AssistantTestHost host)
    {
        return ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(
            Follower,
            FollowerTrigger,
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, Event: "project.changed", Conflict: NhAssistantTriggerConflictNames.Queue),
            Admin));
    }

    private static async Task<IReadOnlyList<AssistantApproval>> ApprovalsAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Approvals.AsNoTracking().Where(approval => approval.ConversationId == conversationId).ToListAsync();
    }

    private static async Task<NhAssistantCompositionStatusDto> CompositionStatusAsync(AssistantTestHost host, string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantCompositionStatus>()
            .GetAsync(agentId, new NhAssistantCompositionStatusRequest(null, null), CancellationToken.None))!;
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAssistantTiming? timing = null, bool scriptTool = false)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                if (timing is not null)
                {
                    services.AddSingleton(timing);
                }

                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                if (scriptTool)
                {
                    services.AddNewHeapPlatformAIScripting();
                }
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(ChatOnlyAgent))
                .AddRuleHandler<TestSkillRules>("project-follow-up", "Follows up a project by rule.")
                .AddRuleHandler<TestRules>("project-check", "Checks a project by rule.")
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
    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(
        AssistantTestHost host,
        Guid projectId,
        string change,
        string triggerId = DataTrigger)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, change));
        return receipts.Where(receipt => receipt.TriggerId == triggerId).ToArray();
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
