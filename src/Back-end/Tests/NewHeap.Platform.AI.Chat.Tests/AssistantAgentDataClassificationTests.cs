using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The data classification of an agent and of its skills, in code and as data: the most specific
/// one reaches the model calls of a run, and the profile resolution refuses a profile that does not
/// permit it, as for every model call.
/// </summary>
public sealed class AssistantAgentDataClassificationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Secure = "secure";
    private const string Notes = "notes";
    private const string ConfidentialProfile = "confidential-chat";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_skills_classification_or_else_the_agents_reaches_the_model_call_and_a_profile_that_does_not_permit_it_refuses(AssistantTestProvider provider)
    {
        var models = new ClassifiedModels();
        models.Confidential.RespondWithText("Reviewed the confidential figures.");
        models.Default.RespondWithText("Wrote the note.");
        await using var host = await CreateHostAsync(provider, models);

        // The skill names no classification: the agent's Confidential reaches its model call, which
        // only the confidential profile permits.
        var secure = await RunAsync(host, "secure-changed");
        // The skill's own Internal wins over the agent's.
        var notes = await RunAsync(host, "notes-changed");
        // Without a skill the agent's Confidential applies; its profile permits Internal only.
        var refused = await RunAsync(host, "plain-changed");

        var options = host.Services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        Assert.Equal(NhAssistantRunStatuses.Completed, secure.Status);
        Assert.Single(models.Confidential.Requests);
        Assert.Equal(NhAssistantRunStatuses.Completed, notes.Status);
        Assert.Single(models.Default.Requests);
        // The refusal is no transient failure: the run fails at its first attempt, without a retry,
        // although the agent retries transient failures.
        Assert.Equal(NhAssistantRunStatuses.Failed, refused.Status);
        Assert.Equal(NhAssistantErrorCodes.ClassificationNotPermitted, refused.FailureCode);
        Assert.Equal(NhAssistantRunFailureClasses.Policy, refused.FailureClass);
        Assert.Equal(3, options.RetryCount);
        Assert.Single(models.Default.Requests);
        Assert.Equal(NhAiDataClassification.Confidential, options.DataClassification);
        Assert.Equal(NhAiDataClassification.Internal, options.Skills[Notes].DataClassification);
        Assert.Null(options.Skills[Secure].DataClassification);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_skill_stores_its_classification_and_its_model_calls_carry_it(AssistantTestProvider provider)
    {
        var models = new ClassifiedModels();
        models.Confidential.RespondWithText("Audited the project.");
        await using var host = await CreateHostAsync(provider, models);

        var invalid = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            AgentId,
            "audit",
            new NhAssistantDataSkillRequest("Audits the project.", DataClassification: "secret"),
            "admin-1"));
        var saved = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            AgentId,
            "audit",
            new NhAssistantDataSkillRequest("Audits the project.", Profile: ConfidentialProfile, DataClassification: "restricted"),
            "admin-1"));
        var trigger = await ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(
            AgentId,
            "audit-changed",
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Event, "audit", Event: "project.changed", Conflict: NhAssistantTriggerConflictNames.Queue),
            "admin-1"));
        // Restricted reaches the model call, which the confidential profile does not permit.
        var restricted = await RunAsync(host, "audit first", "audit-changed");
        var changed = await ConfigureAsync(host, configuration => configuration.SaveSkillAsync(
            AgentId,
            "audit",
            new NhAssistantDataSkillRequest("Audits the project.", Profile: ConfidentialProfile, DataClassification: "confidential", ExpectedVersion: 1),
            "admin-1"));
        var confidential = await RunAsync(host, "audit second", "audit-changed");

        Assert.Contains(invalid.GetResultItems(), item => item.Name == "field:dataClassification");
        Assert.True(saved.Success);
        Assert.Equal("restricted", saved.Data!.DataClassification);
        Assert.True(trigger.Success);
        Assert.Equal((NhAssistantRunStatuses.Failed, NhAssistantErrorCodes.ClassificationNotPermitted), (restricted.Status, restricted.FailureCode));
        Assert.True(changed.Success);
        Assert.Equal(NhAssistantRunStatuses.Completed, confidential.Status);
        Assert.Single(models.Confidential.Requests);
        var configuration = await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId));
        Assert.Equal("confidential", configuration!.DataSkills.Single(skill => skill.SkillId == "audit").DataClassification);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, ClassifiedModels models)
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
                services.AddKeyedSingleton<IChatClient>("confidential-model", models.Confidential);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(ConfidentialProfile, profile => profile
                    .UseKeyedClient("confidential-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Confidential)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddTriggerEvent<ProjectChanged>("project.changed", evt => evt
                    .Description("A project changed.")
                    .When(change => change.Change.StartsWith("audit", StringComparison.Ordinal))
                    .Subject(change => new NhAiSubjectRef("project", change.ProjectId.ToString("N")))
                    .Message(change => change.Change))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .DataClassification(NhAiDataClassification.Confidential)
                    .Skill(Secure, skill => skill
                        .Description("Reviews the confidential figures of the project.")
                        .UseProfile(ConfidentialProfile))
                    .Skill(Notes, skill => skill
                        .Description("Writes notes about the project.")
                        .DataClassification(NhAiDataClassification.Internal)))
                .AddTrigger<ProjectChanged>("secure-changed", trigger => Changed(trigger, "secure").Skill(Secure))
                .AddTrigger<ProjectChanged>("notes-changed", trigger => Changed(trigger, "notes").Skill(Notes))
                .AddTrigger<ProjectChanged>("plain-changed", trigger => Changed(trigger, "plain")));
    }

    private static NhAssistantTriggerBuilder<ProjectChanged> Changed(NhAssistantTriggerBuilder<ProjectChanged> trigger, string prefix)
    {
        return trigger
            .StartAgent(AgentId)
            .When(evt => evt.Change.StartsWith(prefix, StringComparison.Ordinal))
            .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
            .Message(evt => evt.Change)
            .OnBusy(NhAssistantTriggerConflict.Queue);
    }

    /// <summary>
    /// Publishes a change of a new project, executes the run of <paramref name="triggerId"/> and
    /// returns it as it ended.
    /// </summary>
    private static async Task<AssistantRun> RunAsync(AssistantTestHost host, string change, string? triggerId = null)
    {
        NhAssistantTriggerReceipt receipt;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
                .PublishAsync(new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), change));
            receipt = Assert.Single(receipts, item => item.TriggerId == (triggerId ?? change));
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

    private sealed class ClassifiedModels
    {
        public NhAiScriptedChatClient Default { get; } = new();

        public NhAiScriptedChatClient Confidential { get; } = new();
    }
}
