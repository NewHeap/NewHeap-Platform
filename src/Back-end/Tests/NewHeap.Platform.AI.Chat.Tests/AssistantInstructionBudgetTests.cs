using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// What an agent's turns need against the limit of a model call, measured with the application context
/// stored in this environment: the public reading, the warning when the text is saved, the warning
/// on the agent in the administration, and a limit an application raises. Proved on both relational
/// providers where the text is stored.
/// </summary>
public sealed class AssistantInstructionBudgetTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "budget-assistant";
    private const string SkillId = "reporting";
    private const int RetrievalBudget = 4_000;
    private const string Marker = "secret-application-text";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_reading_counts_the_context_stored_now_and_the_longest_style_wishes(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var before = await AgentBudgetAsync(host);
        var composedWithoutWishes = NhAssistantPromptComposer
            .Compose(AssistantTestData.Agent(), null, NhAssistantPreferences.Default, "en")
            .Instructions.Length;

        Assert.Equal((AgentId, (string?)null), (before.AgentId, before.SkillId));
        Assert.Equal(RetrievalBudget, before.RetrievalCharacters);
        Assert.Equal(NhAssistantLimits.DefaultMaxInstructionCharacters, before.MaxCharacters);
        Assert.True(before.InstructionCharacters >= composedWithoutWishes + NhAssistantPreferences.MaxCustomInstructionsLength);
        Assert.Equal(before.MaxCharacters - before.InstructionCharacters - before.RetrievalCharacters, before.RemainingCharacters);
        Assert.True(before.Fits);

        await SaveApplicationContextAsync(host, new string('c', 5_000));
        var after = await AgentBudgetAsync(host);

        Assert.True(after.InstructionCharacters >= before.InstructionCharacters + 5_000);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Stored_text_that_takes_the_room_of_the_retrieval_fails_the_budget_and_the_save_warns(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, agentInstructions: 15_000);

        var saved = await SaveApplicationContextAsync(host, Marker.PadRight(NhAssistantApplicationContexts.MaxTextLength, '.'));

        Assert.False((await AgentBudgetAsync(host)).Fits);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NhAssistantInstructionBudgets>()
                .LogOverBudgetAsync(saved, CancellationToken.None);
        }

        var warnings = host.Logs.Messages.Where(message => message.Contains("With the saved application context", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains(warnings, warning => warning.Contains($"skill {SkillId}", StringComparison.Ordinal));
        Assert.All(warnings, warning => Assert.DoesNotContain(Marker, warning));
    }

    [Fact]
    public async Task A_skill_with_instructions_has_a_budget_of_its_own()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);

        var budgets = await BudgetsAsync(host);

        Assert.Equal([null, SkillId], budgets.Select(budget => budget.SkillId));
        Assert.True(budgets[1].InstructionCharacters > budgets[0].InstructionCharacters + 500);
    }

    [Fact]
    public async Task An_unknown_agent_is_a_failed_result()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        await using var scope = host.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<INhAssistantInstructionBudgets>()
            .GetAsync("no-such-agent", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.GetResultItems(), item => item.Name == NhAssistantErrorCodes.AgentNotFound);
    }

    [Fact]
    public async Task The_administration_warns_about_an_agent_the_stored_text_leaves_no_room()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(AgentId) with { Instructions = Asset(15_000) }));
        using var admin = app.CreateClient(admin: true);
        Assert.Equal(0, (await AgentAsync(admin)).GetProperty("warnings").GetArrayLength());

        using var saved = await admin.PutAsJsonAsync("/api/assistant/admin/context", new
        {
            text = new string('c', NhAssistantApplicationContexts.MaxTextLength),
            expectedVersion = 0
        });
        saved.EnsureSuccessStatusCode();

        var warnings = (await AgentAsync(admin)).GetProperty("warnings").EnumerateArray().Select(item => item.GetString());
        Assert.Equal([NhAssistantConfigurationWarnings.InstructionsOverBudget], warnings);
    }

    [Fact]
    public async Task A_raised_limit_lets_a_turn_send_more_than_the_default()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Fine.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            limits => limits.MaxInstructionCharacters = 48_000,
            configure: services =>
            {
                // A higher limit needs a profile whose input budget takes the longer text.
                services.AddKeyedSingleton<IChatClient>("long-chat-model", model);
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile("long-chat", profile => profile
                    .UseKeyedClient("long-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 32_000, maxOutputTokens: 1_024, maxCalls: 4)));
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(AgentId) with { ProfileName = "long-chat", Instructions = Asset(30_000) })
                .UseDefaultApplicationContext(Asset(15_000)));
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(conversation.Id, "Hello.");

        Assert.True(Assert.Single(model.Requests).Options!.Instructions!.Length > NhAssistantLimits.DefaultMaxInstructionCharacters);
    }

    [Theory]
    [InlineData(4_095)]
    [InlineData(NhAssistantLimits.HardMaxInstructionCharacters + 1)]
    public void A_limit_out_of_bounds_is_refused(int characters)
    {
        Assert.Throws<InvalidOperationException>(new NhAssistantLimits { MaxInstructionCharacters = characters }.Validate);
    }

    [Fact]
    public void The_highest_limit_is_one_a_model_call_accepts()
    {
        new NhAssistantLimits { MaxInstructionCharacters = NhAssistantLimits.HardMaxInstructionCharacters }.Validate();
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, int? agentInstructions = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            assistantBuilder: assistant => assistant
                .AddAgent(agentInstructions is { } characters
                    ? AssistantTestData.Agent(AgentId) with { Instructions = Asset(characters) }
                    : AssistantTestData.Agent(AgentId))
                .ConfigureAgent(AgentId, agent => agent
                    .Skill(SkillId, skill => skill
                        .Description("Writes reports.")
                        .Instructions(new string('s', 1_000))))
                .ConfigureAgentContext(AgentId, context => context.UseRetrievedContext(retrieval => retrieval
                    .Sources("documents")
                    .MaxCharacters(RetrievalBudget))));
    }

    private static async Task<IReadOnlyList<NhAssistantInstructionBudget>> BudgetsAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<INhAssistantInstructionBudgets>()
            .GetAsync(AgentId, CancellationToken.None);
        Assert.True(result.Success);
        return result.Data!;
    }

    private static async Task<NhAssistantInstructionBudget> AgentBudgetAsync(AssistantTestHost host)
    {
        return (await BudgetsAsync(host))[0];
    }

    private static async Task<AssistantApplicationContext> SaveApplicationContextAsync(AssistantTestHost host, string text)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var personalization = scope.ServiceProvider.GetRequiredService<NhAssistantPersonalization>();
        var current = await personalization.GetApplicationContextAsync(CancellationToken.None);
        var saved = await personalization.UpdateApplicationContextAsync(text, current?.Version ?? 0, "admin-1", CancellationToken.None);
        Assert.True(saved.Success);
        return saved.Data!;
    }

    private static async Task<JsonElement> AgentAsync(HttpClient admin)
    {
        var agents = await admin.GetFromJsonAsync<JsonElement>("/api/assistant/admin/agents");
        return agents.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == AgentId);
    }

    private static NhAiTextAsset Asset(int characters)
    {
        return NhAiTextAssetFactory.Create(
            "budget-instructions",
            1,
            "Instructions".PadRight(characters, '.'),
            "embedded:NewHeap.Platform.AI.Chat.Tests/budget-instructions",
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.FunctionCalling,
            [],
            "project-context-v1",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Evaluation,
            "project-assistant-v1");
    }
}
