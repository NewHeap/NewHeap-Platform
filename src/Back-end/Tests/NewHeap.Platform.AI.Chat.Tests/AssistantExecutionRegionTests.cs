using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The execution region (<c>UseExecutionRegion</c>) against the model profiles in use: startup refuses a
/// profile in code whose chain does not permit the region, a save refuses one for a data skill or an
/// agent created in the administration, and a turn whose chain permits it for no profile that takes its
/// data fails with <c>assistant-model-region-denied</c>, which a run does not retry.
/// </summary>
public sealed class AssistantExecutionRegionTests
{
    private const string LocalProfile = "local-chat";
    private const string RegionalProfile = "regional-chat";
    private const string EuProfile = "eu-chat";

    [Fact]
    public void Startup_names_the_profile_in_code_and_the_region_that_its_chain_does_not_permit()
    {
        // The chat profile, the agents' profiles, the skills' and the triage's: each is checked with its fallbacks.
        AssertRefused(assistant => assistant.UseChatProfile(LocalProfile), "The assistant chat profile", LocalProfile);
        AssertRefused(
            assistant => assistant.AddAgent(AssistantTestData.Agent() with { ProfileName = LocalProfile }),
            "Agent 'project-assistant'",
            LocalProfile);
        AssertRefused(
            assistant => assistant
                .AddAgent(AssistantTestData.Agent() with { ProfileName = RegionalProfile })
                .ConfigureAgent("project-assistant", agent => agent
                    .Grant("projects.*")
                    .Skill("figures", skill => skill.Description("Reviews the figures.").UseProfile(LocalProfile))),
            "Skill 'figures' of agent 'project-assistant'",
            LocalProfile);
        AssertRefused(
            assistant => assistant
                .AddAgent(AssistantTestData.Agent() with { ProfileName = RegionalProfile })
                .ConfigureAgent("project-assistant", agent => agent
                    .Grant("projects.*")
                    .Skill("figures", skill => skill.Description("Reviews the figures."))
                    .Triage(triage => triage.UseProfile(LocalProfile))),
            "The triage of agent 'project-assistant'",
            LocalProfile);

        // A profile that permits the region through a fallback starts.
        var (state, profiles) = Register(assistant => assistant
            .UseExecutionRegion("eu")
            .UseChatProfile(RegionalProfile)
            .AddAgent(AssistantTestData.Agent() with { ProfileName = RegionalProfile })
            .ConfigureAgent("project-assistant", agent => agent
                .Grant("projects.*")
                .Skill("figures", skill => skill.Description("Reviews the figures.").UseProfile(EuProfile))
                .Triage(triage => triage.UseProfile(RegionalProfile))));
        NhAssistantRegionChecks.Validate(state, profiles);

        // The default region is local, which every profile here but the regional one's fallback permits.
        var (local, localProfiles) = Register(assistant => assistant
            .UseChatProfile(LocalProfile)
            .AddAgent(AssistantTestData.Agent() with { ProfileName = LocalProfile }));
        Assert.Equal("local", local.ExecutionRegion);
        NhAssistantRegionChecks.Validate(local, localProfiles);
    }

    [Fact]
    public void A_profile_permits_the_region_itself_or_through_a_fallback_and_with_the_data_classification_of_the_call()
    {
        var (_, profiles) = Register(assistant => assistant.AddAgent(AssistantTestData.Agent() with { ProfileName = RegionalProfile }));
        Assert.True(profiles.TryGet(RegionalProfile, out var regional));
        Assert.True(profiles.TryGet(LocalProfile, out var local));

        Assert.True(NhAssistantRegionChecks.Permits(profiles, regional, "eu"));
        Assert.True(NhAssistantRegionChecks.Permits(profiles, regional, "local"));
        Assert.False(NhAssistantRegionChecks.Permits(profiles, local, "eu"));
        // The fallback runs in the region but takes internal data only.
        Assert.True(NhAssistantRegionChecks.PermitsTogether(profiles, regional, NhAiDataClassification.Internal, "eu"));
        Assert.False(NhAssistantRegionChecks.PermitsTogether(profiles, regional, NhAiDataClassification.Confidential, "eu"));
        Assert.True(NhAssistantRegionChecks.PermitsTogether(profiles, regional, NhAiDataClassification.Confidential, "local"));
        // A profile that is not registered is left to the checks that report it.
        Assert.True(NhAssistantRegionChecks.PermitsByName(profiles, "missing-chat", "eu"));
        Assert.True(NhAssistantRegionChecks.PermitsByName(profiles, null, "eu"));
        Assert.False(NhAssistantRegionChecks.PermitsByName(profiles, LocalProfile, "eu"));
    }

    [Fact]
    public void A_run_refused_for_its_region_is_a_policy_failure_and_is_not_retried()
    {
        Assert.Equal(
            NhAssistantRunFailureClasses.Policy,
            NhAssistantAgentRunOperation.Classify(NhAssistantErrorCodes.ModelRegionDenied));
        Assert.Equal(
            NhAssistantRunFailureClasses.Transient,
            NhAssistantAgentRunOperation.Classify(NhAssistantErrorCodes.ModelUnavailable));
    }

    private static void AssertRefused(Action<NhAssistantBuilder> configure, string owner, string profileName)
    {
        var (state, profiles) = Register(assistant =>
        {
            assistant.UseExecutionRegion("eu");
            configure(assistant);
        });

        var exception = Assert.Throws<InvalidOperationException>(() => NhAssistantRegionChecks.Validate(state, profiles));

        Assert.StartsWith(owner, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{profileName}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("execution region 'eu'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registration and three profiles: one that runs locally only, one in the region <c>eu</c> for
    /// internal data, and a regional one that runs locally with the <c>eu</c> profile as its fallback.
    /// </summary>
    internal static (NhAssistantRegistrationState State, INhAiModelProfileRegistry Profiles) Register(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(ai => AddProfiles(ai));
        services.AddNewHeapAssistant(configure);
        var state = (NhAssistantRegistrationState)services
            .Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState))
            .ImplementationInstance!;
        return (state, services.BuildServiceProvider().GetRequiredService<INhAiModelProfileRegistry>());
    }

    internal static NhAiBuilder AddProfiles(NhAiBuilder ai)
    {
        return ai
            .AddChatProfile(LocalProfile, profile => Configure(profile, "local-model")
                .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                .PermitExecutionRegions("local"))
            .AddChatProfile(EuProfile, profile => Configure(profile, "eu-model")
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("eu"))
            .AddChatProfile(RegionalProfile, profile => Configure(profile, "local-model")
                .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                .PermitExecutionRegions("local")
                .WithFallbackProfiles(EuProfile));
    }

    private static NhAiModelProfileBuilder Configure(NhAiModelProfileBuilder profile, string modelKey)
    {
        return profile
            .UseKeyedClient(modelKey)
            .RequireCapabilities(NhAiModelCapability.FunctionCalling)
            .WithStreaming(NhAiStreamingPolicy.Allowed)
            .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16);
    }
}

/// <summary>
/// The region checks with a database: a save refuses a profile whose chain does not permit the region,
/// and a turn that no profile may serve in the region fails at once, without a model call or a retry.
/// </summary>
public sealed class AssistantExecutionRegionTurnTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_save_refuses_a_profile_whose_chain_does_not_permit_the_region(AssistantTestProvider provider)
    {
        var models = new RegionModels();
        await using var host = await CreateHostAsync(provider, models);

        var localAgent = await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            AdminAgent("local-desk", "local-chat", NhAssistantDataClassificationNames.Internal),
            "admin-1"));
        var regionalAgent = await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            AdminAgent("regional-desk", "regional-chat", NhAssistantDataClassificationNames.Internal),
            "admin-1"));
        var localSkill = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            AgentId,
            "audit",
            new NhAssistantDataSkillRequest("Audits the project.", Profile: "local-chat"),
            "admin-1"));
        var regionalSkill = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            AgentId,
            "audit",
            new NhAssistantDataSkillRequest("Audits the project.", Profile: "regional-chat"),
            "admin-1"));

        Assert.False(localAgent.Success);
        Assert.Contains(localAgent.GetResultItems(), item => item.Name == "field:profile" && item.ErrorMessages.Any(message => message.ToString() == NhAssistantFieldErrors.RegionNotPermitted));
        Assert.True(regionalAgent.Success);
        Assert.False(localSkill.Success);
        Assert.Contains(localSkill.GetResultItems(), item => item.Name == "field:profile" && item.ErrorMessages.Any(message => message.ToString() == NhAssistantFieldErrors.RegionNotPermitted));
        Assert.True(regionalSkill.Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_turn_that_no_profile_may_serve_in_the_region_fails_at_once_and_its_run_is_not_retried(AssistantTestProvider provider)
    {
        var models = new RegionModels();
        models.Eu.RespondWithText("The figures are on track.");
        await using var host = await CreateHostAsync(provider, models);
        var internalDesk = await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            AdminAgent("figures-desk", "regional-chat", NhAssistantDataClassificationNames.Internal),
            "admin-1"));
        // The regional profile runs in the region only through its fallback, which takes internal data only.
        var confidentialDesk = await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            AdminAgent("vault-desk", "regional-chat", NhAssistantDataClassificationNames.Confidential),
            "admin-1"));

        var moved = await ChatAsync(host, "figures-desk");
        var confidential = await ChatAsync(host, "vault-desk");
        // The agent in code uses the host's chat profile, which runs locally only.
        var code = await ChatAsync(host, AgentId);
        var run = await RunAsync(host);

        Assert.True(internalDesk.Success);
        Assert.True(confidentialDesk.Success);
        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), moved);
        Assert.Single(models.Eu.Requests);
        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAssistantErrorCodes.ModelRegionDenied), confidential);
        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAssistantErrorCodes.ModelRegionDenied), code);
        Assert.Empty(models.Local.Requests);
        Assert.Empty(models.Default.Requests);
        // The run fails at its first attempt although the agent retries transient failures.
        Assert.Equal(NhAssistantRunStatuses.Failed, run.Status);
        Assert.Equal(NhAssistantErrorCodes.ModelRegionDenied, run.FailureCode);
        Assert.Equal(NhAssistantRunFailureClasses.Policy, run.FailureClass);
        Assert.Equal(3, host.Services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId].RetryCount);
        Assert.Contains(host.Logs.Entries, entry => entry.Message.Contains("permits region eu for classification", StringComparison.Ordinal));
    }

    private static NhAssistantAgentRequest AdminAgent(string id, string profile, string classification)
    {
        return new NhAssistantAgentRequest(
            id,
            "Figures desk",
            "Answers questions about the project figures.",
            "Answer questions about the project figures.",
            ["projects.*"],
            NhAiAutonomyLevel.Explain,
            Profile: profile,
            DataClassification: classification);
    }

    private static async Task<(string Status, string? ErrorCode)> ChatAsync(AssistantTestHost host, string agentId)
    {
        var conversation = await host.CreateConversationAsync(agentId: agentId);
        var turn = await host.SendAsync(conversation.Id, "How are the figures of the project?");
        var completed = Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]);
        return (completed.Status, completed.ErrorCode);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, RegionModels models)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Default,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddKeyedSingleton<IChatClient>("local-model", models.Local);
                services.AddKeyedSingleton<IChatClient>("eu-model", models.Eu);
                services.AddNewHeapPlatformAI(ai => AssistantExecutionRegionTests.AddProfiles(ai));
            },
            assistantBuilder: assistant => assistant
                // The host's chat profile runs locally only, so its agent in code is refused in this region.
                .UseExecutionRegion("eu")
                .AddSubjectType<TestSubjectResolver>()
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                    .Message(change => change.Change))
                .ConfigureAgent(AgentId, agent => agent.Grant("projects.*"))
                .AddTrigger<ProjectChanged>("project-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue)));
    }

    /// <summary>
    /// Publishes a change of a new project, executes the run it started and returns it as it ended.
    /// </summary>
    private static async Task<AssistantRun> RunAsync(AssistantTestHost host)
    {
        NhAssistantTriggerReceipt receipt;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
                .PublishAsync(new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "changed"));
            receipt = Assert.Single(receipts);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
            var context = Substitute.For<INhBackgroundOperationContext>();
            context.AttemptNumber.Returns(1);
            await operation.ExecuteAsync(new NhAssistantAgentRunRequest(receipt.RunId!.Value), context, CancellationToken.None);
        }

        return (await host.Services.GetRequiredService<NhAssistantRunStore>().FindRunAsync(receipt.RunId.Value, CancellationToken.None))!;
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private sealed class RegionModels
    {
        public NhAiScriptedChatClient Default { get; } = new();

        public NhAiScriptedChatClient Local { get; } = new();

        public NhAiScriptedChatClient Eu { get; } = new();
    }
}
