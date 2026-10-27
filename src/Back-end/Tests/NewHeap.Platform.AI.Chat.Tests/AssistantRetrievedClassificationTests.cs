using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Retrieved context is admitted up to the highest classification of the agent's profile and then
/// raises the classification of the turn's model calls, as files do. A skill or fallback profile that
/// does not permit it never receives the retrieved items: <c>use_skill</c> refuses the skill and the
/// profile chain fails the turn instead of falling back. Proved on both relational providers.
/// </summary>
public sealed class AssistantRetrievedClassificationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string VaultAgentId = "vault-assistant";
    private const string FallbackAgentId = "fallback-assistant";
    private const string Summary = "summary";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Use_skill_refuses_a_skill_whose_profile_may_not_receive_the_retrieved_context(AssistantTestProvider provider)
    {
        var models = new ClassifiedModels();
        models.Vault
            .RespondWithFunctionCall("use_skill", new { skill = Summary, reason = "A summary is asked for." }, "call-confidential")
            .RespondWithText("Answered without the summary skill.")
            .RespondWithFunctionCall("use_skill", new { skill = Summary, reason = "A summary is asked for." }, "call-internal");
        models.Summary.RespondWithText("A summary of the internal note.");
        await using var host = await CreateHostAsync(provider, models);

        var confidential = await host.SendAsync((await host.CreateConversationAsync(agentId: VaultAgentId)).Id, "Summarize the plan.");
        var refusal = Assert.Single(FunctionResults(models.Vault.Requests[1]));
        var summaryRequestsAfterRefusal = models.Summary.Requests.Count;
        // Only an internal item is retrieved for this question, which the summary profile may receive.
        var internalOnly = await host.SendAsync((await host.CreateConversationAsync(agentId: VaultAgentId)).Id, "Summarize the internal note.");

        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), Outcome(confidential));
        Assert.Contains(ClassifiedDocumentSource.ConfidentialText, models.Vault.Requests[0].Options!.Instructions);
        var result = JsonSerializer.SerializeToElement(refusal.Result);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(NhAssistantErrorCodes.ClassificationNotPermitted, result.GetProperty("code").GetString());
        Assert.Equal(0, summaryRequestsAfterRefusal);

        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), Outcome(internalOnly));
        var summary = Assert.Single(models.Summary.Requests);
        Assert.Contains(ClassifiedDocumentSource.InternalText, summary.Options!.Instructions);
        Assert.DoesNotContain(models.Summary.Requests, request =>
            request.Options!.Instructions!.Contains(ClassifiedDocumentSource.ConfidentialText, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_fallback_profile_that_may_not_receive_the_retrieved_context_never_serves_the_turn(AssistantTestProvider provider)
    {
        var models = new ClassifiedModels();
        models.Backup.RespondWithText("The internal note says the roadmap moved.");
        await using var host = await CreateHostAsync(provider, models);

        // The primary profile permits confidential data but not this host's region; its fallback runs
        // here and permits internal data only.
        var confidential = await host.SendAsync((await host.CreateConversationAsync(agentId: FallbackAgentId)).Id, "What does the plan say?");
        var internalOnly = await host.SendAsync((await host.CreateConversationAsync(agentId: FallbackAgentId)).Id, "What does the internal note say?");

        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAssistantErrorCodes.ModelRegionDenied), Outcome(confidential));
        Assert.Empty(models.Primary.Requests);
        var served = Assert.Single(models.Backup.Requests);
        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), Outcome(internalOnly));
        Assert.Contains(ClassifiedDocumentSource.InternalText, served.Options!.Instructions);
        Assert.DoesNotContain(ClassifiedDocumentSource.ConfidentialText, served.Options!.Instructions);
    }

    private static IEnumerable<FunctionResultContent> FunctionResults(NhAiRecordedChatRequest request)
    {
        return request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>();
    }

    private static (string Status, string? ErrorCode) Outcome(TurnResult turn)
    {
        var completed = turn.Single<NhAssistantTurnCompletedEvent>();
        return (completed.Status, completed.ErrorCode);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, ClassifiedModels models)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Default,
            configure: services =>
            {
                services.AddKeyedSingleton<IChatClient>("vault-model", models.Vault);
                services.AddKeyedSingleton<IChatClient>("summary-model", models.Summary);
                services.AddKeyedSingleton<IChatClient>("primary-model", models.Primary);
                services.AddKeyedSingleton<IChatClient>("backup-model", models.Backup);
                services.AddNewHeapPlatformAI(ai => ai
                    .AddChatProfile("vault-chat", profile => Chat(profile, "vault-model")
                        .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                        .PermitExecutionRegions("local"))
                    .AddChatProfile("summary-chat", profile => Chat(profile, "summary-model")
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local"))
                    .AddChatProfile("primary-chat", profile => Chat(profile, "primary-model")
                        .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                        .PermitExecutionRegions("remote")
                        .WithFallbackProfiles("backup-chat"))
                    .AddChatProfile("backup-chat", profile => Chat(profile, "backup-model")
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local"))
                    .AddContextSource<ClassifiedDocumentSource>()
                    .AddContextContributor<TestDivisionContributor>()
                    .UseContextAuthorizationPolicy<TestDocumentPolicy>());
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(VaultAgentId) with { ProfileName = "vault-chat" })
                .AddAgent(AssistantTestData.Agent(FallbackAgentId) with { ProfileName = "primary-chat" })
                .ConfigureAgent(VaultAgentId, agent => agent
                    .Skill(Summary, skill => skill
                        .Description("Summarizes what the person asks about.")
                        .UseProfile("summary-chat")
                        .Tools("projects.search"))
                    .SkillsInChat())
                .ConfigureAgentContext(VaultAgentId, context => context.UseRetrievedContext(retrieval => retrieval.Sources(ClassifiedDocumentSource.SourceId)))
                .ConfigureAgentContext(FallbackAgentId, context => context.UseRetrievedContext(retrieval => retrieval.Sources(ClassifiedDocumentSource.SourceId))));
    }

    private static NhAiModelProfileBuilder Chat(NhAiModelProfileBuilder profile, string clientKey)
    {
        return profile
            .UseKeyedClient(clientKey)
            .RequireCapabilities(NhAiModelCapability.FunctionCalling)
            .WithStreaming(NhAiStreamingPolicy.Allowed)
            .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16);
    }

    private sealed class ClassifiedModels
    {
        public NhAiScriptedChatClient Default { get; } = new();

        public NhAiScriptedChatClient Vault { get; } = new();

        public NhAiScriptedChatClient Summary { get; } = new();

        public NhAiScriptedChatClient Primary { get; } = new();

        public NhAiScriptedChatClient Backup { get; } = new();
    }
}

/// <summary>
/// An internal and a confidential document of the person's division; a question about the internal
/// note retrieves only the internal one.
/// </summary>
public sealed class ClassifiedDocumentSource : INhAiContextSource
{
    public const string SourceId = "test-documents";
    public const string InternalText = "INTERNAL-NOTE: the roadmap moved to May.";
    public const string ConfidentialText = "CONFIDENTIAL-PLAN: the acquisition closes in May.";

    public NhAiContextSourceDescriptor Descriptor { get; } = new(
        SourceId,
        "Classified documents of the test application.",
        NhAiDataClassification.Confidential,
        10,
        2_000);

    public ValueTask<IReadOnlyList<NhAiContextItem>> RetrieveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default)
    {
        var division = new NhAiExecutionScopeEntry("division", "division-a");
        var created = DateTimeOffset.UtcNow.AddDays(-1);
        var items = new List<NhAiContextItem>
        {
            new("note-1", "note-1", SourceId, "text/plain", InternalText, NhAiDataClassification.Internal, NhAiContextTrust.UntrustedRetrieved, [division], created)
        };
        if (!request.Query.Contains("internal", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new("plan-1", "plan-1", SourceId, "text/plain", ConfidentialText, NhAiDataClassification.Confidential, NhAiContextTrust.UntrustedRetrieved, [division], created));
        }

        return ValueTask.FromResult<IReadOnlyList<NhAiContextItem>>(items);
    }
}
