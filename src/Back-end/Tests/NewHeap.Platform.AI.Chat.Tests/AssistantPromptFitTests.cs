using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The fit step of a turn's text, without a host: the lowest-ranked passages go first, then the
/// application map, then the turn data; what remains is formatted again and hashed as sent. And the
/// startup warning for instructions that leave no room for their retrieval budget.
/// </summary>
public sealed class AssistantPromptFitTests
{
    private const int Generous = 1_000_000;
    private const string TurnData = "# Situation\nA moment.";
    private const string Map = "# Application map\nThe resources.";

    [Fact]
    public async Task A_turn_that_fits_stays_byte_for_byte_what_it_was()
    {
        var resolution = await ResolveAsync("first", "second");
        var retrieved = Retrieved(resolution);
        var prompt = Prompt(map: Map);

        var fit = NhAssistantPromptFit.Of(prompt, retrieved, Generous);

        Assert.Equal(
            string.Join("\n\n", prompt.Instructions, Map, TurnData, retrieved.Block),
            fit.Prompt.ModelInstructions);
        Assert.Equal(resolution.ContextHash, fit.Prompt.RetrievedContextHash);
        Assert.Equal("map-hash", fit.Prompt.ApplicationMapHash);
        Assert.False(fit.LeftDataOut);
        Assert.DoesNotContain("left out", retrieved.Block);
    }

    [Fact]
    public async Task The_lowest_ranked_passages_go_one_at_a_time_and_the_turn_data_stays()
    {
        var retrieved = Retrieved(await ResolveAsync("first", "second", "third"));
        var prompt = Prompt();
        var twoPassages = retrieved.FitTo(retrieved.Block.Length - 1);
        var limit = prompt.ModelInstructions.Length + 2 + twoPassages.Block.Length;

        var fit = NhAssistantPromptFit.Of(prompt, retrieved, limit);

        Assert.Equal(2, fit.KeptPassages);
        Assert.Equal(1, fit.DroppedPassages);
        Assert.False(fit.ApplicationMapDropped);
        Assert.False(fit.TurnDataDropped);
        Assert.True(fit.LeftDataOut);
        Assert.Contains(TurnData, fit.Prompt.ModelInstructions);
        Assert.Contains(Passage("first"), fit.Prompt.RetrievedContext);
        Assert.Contains(Passage("second"), fit.Prompt.RetrievedContext);
        Assert.DoesNotContain(Passage("third"), fit.Prompt.RetrievedContext);
        Assert.Equal(limit, fit.Prompt.ModelInstructions.Length);
    }

    [Fact]
    public async Task The_block_that_remains_carries_the_hash_of_the_passages_it_holds()
    {
        var all = await ResolveAsync("first", "second", "third");
        var firstTwo = await ResolveAsync("first", "second");

        var fitted = Retrieved(all).FitTo(Retrieved(all).Block.Length - 1);

        // The same hash as a resolution of just those passages, and not that of the full resolution.
        Assert.Equal(firstTwo.ContextHash, fitted.Hash);
        Assert.NotEqual(all.ContextHash, fitted.Hash);
        Assert.Contains(firstTwo.ContextHash, fitted.Block);
        Assert.DoesNotContain(all.ContextHash, fitted.Block);
    }

    [Fact]
    public async Task The_application_map_stays_while_the_text_fits_without_passages()
    {
        var retrieved = Retrieved(await ResolveAsync("first", "second"));
        var prompt = Prompt(map: Map);
        var noPassages = retrieved.FitTo(0);
        var limit = prompt.ModelInstructions.Length + 2 + noPassages.Block.Length;

        var fit = NhAssistantPromptFit.Of(prompt, retrieved, limit);

        Assert.Equal(0, fit.KeptPassages);
        Assert.Equal(2, fit.DroppedPassages);
        Assert.False(fit.ApplicationMapDropped);
        Assert.Equal(Map, fit.Prompt.ApplicationMap);
        Assert.Equal("map-hash", fit.Prompt.ApplicationMapHash);
    }

    [Fact]
    public async Task The_application_map_goes_after_the_passages_and_its_room_goes_back_to_them()
    {
        var retrieved = Retrieved(await ResolveAsync("first", "second"));
        var prompt = Prompt(map: new string('m', 4_000));
        var limit = Prompt().ModelInstructions.Length + 2 + retrieved.Block.Length + 100;

        var fit = NhAssistantPromptFit.Of(prompt, retrieved, limit);

        Assert.True(fit.ApplicationMapDropped);
        Assert.Empty(fit.Prompt.ApplicationMap);
        Assert.Null(fit.Prompt.ApplicationMapHash);
        Assert.Equal(2, fit.KeptPassages);
        Assert.Equal(0, fit.DroppedPassages);
        Assert.Contains(TurnData, fit.Prompt.ModelInstructions);
    }

    [Theory]
    [InlineData("en", "# Retrieved context", "that did not fit and were left out: 2.", "silent")]
    [InlineData("nl-NL", "# Opgehaalde context", "die niet pasten en zijn weggelaten: 2.", "zwijgen")]
    public async Task The_block_says_how_many_passages_were_left_out_in_the_language_of_the_turn(
        string language,
        string heading,
        string count,
        string silent)
    {
        var retrieved = Retrieved(await ResolveAsync("first", "second", "third"), language);

        var fitted = retrieved.FitTo(retrieved.FitTo(retrieved.Block.Length - 1).Block.Length - 1);

        Assert.Equal(1, fitted.Passages);
        Assert.Equal(2, fitted.LeftOut);
        Assert.StartsWith(heading, fitted.Block, StringComparison.Ordinal);
        Assert.Contains(count, fitted.Block);
        Assert.Contains(silent, fitted.Block);
        // The note belongs to the trusted introduction, not to the untrusted data.
        Assert.True(fitted.Block.IndexOf(count, StringComparison.Ordinal) < fitted.Block.IndexOf("<retrieved-context>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en", "that did not fit and were left out: 2.")]
    [InlineData("nl", "die niet pasten en zijn weggelaten: 2.")]
    public async Task A_block_without_passages_keeps_the_note_and_records_no_hash(string language, string count)
    {
        var retrieved = Retrieved(await ResolveAsync("first", "second"), language);
        var prompt = Prompt();
        var limit = prompt.ModelInstructions.Length + 2 + retrieved.FitTo(0).Block.Length;

        var fit = NhAssistantPromptFit.Of(prompt, retrieved, limit);

        Assert.Equal(0, fit.KeptPassages);
        Assert.Equal(2, fit.DroppedPassages);
        Assert.Contains(count, fit.Prompt.RetrievedContext);
        Assert.DoesNotContain("<retrieved-context>", fit.Prompt.RetrievedContext);
        Assert.Null(fit.Prompt.RetrievedContextHash);
    }

    [Fact]
    public async Task A_block_without_passages_counts_as_public()
    {
        var retrieved = Retrieved(await ResolveAsync("first"));

        Assert.Equal(NhAiDataClassification.Internal, retrieved.Classification);
        Assert.Equal(NhAiDataClassification.Public, retrieved.FitTo(0).Classification);
    }

    [Fact]
    public async Task The_turn_data_stays_when_not_even_the_note_fits_next_to_it()
    {
        var retrieved = Retrieved(await ResolveAsync("first", "second"));
        var prompt = Prompt(map: "m");
        var limit = Prompt().ModelInstructions.Length + 10;

        var fit = NhAssistantPromptFit.Of(prompt, retrieved, limit);

        Assert.Equal(Prompt().ModelInstructions, fit.Prompt.ModelInstructions);
        Assert.Null(fit.Prompt.RetrievedContextHash);
        Assert.Equal(2, fit.DroppedPassages);
        Assert.True(fit.ApplicationMapDropped);
        Assert.False(fit.TurnDataDropped);
    }

    [Fact]
    public async Task A_turn_whose_turn_data_does_not_fit_runs_on_its_instructions_alone_and_records_nothing_as_sent()
    {
        var retrieved = Retrieved(await ResolveAsync("first"));
        var prompt = Prompt(map: Map);

        var fit = NhAssistantPromptFit.Of(prompt, retrieved, prompt.Instructions.Length + 1);

        Assert.Equal(prompt.Instructions, fit.Prompt.ModelInstructions);
        Assert.True(fit.TurnDataDropped);
        Assert.True(fit.ApplicationMapDropped);
        Assert.Equal(1, fit.DroppedPassages);
        Assert.Null(fit.Prompt.RetrievedContextHash);
        Assert.Null(fit.Prompt.ApplicationMapHash);
        Assert.Equal(0, fit.Prompt.FactCount);
        Assert.Equal(0, fit.Prompt.PageEntityCount);
        Assert.False(fit.Prompt.HadPageContext);
    }

    [Fact]
    public void A_turn_without_retrieved_context_fits_the_same_way()
    {
        var prompt = Prompt(map: Map);

        Assert.False(NhAssistantPromptFit.Of(prompt, null, Generous).LeftDataOut);

        var withoutMap = NhAssistantPromptFit.Of(prompt, null, Prompt().ModelInstructions.Length);
        Assert.True(withoutMap.ApplicationMapDropped);
        Assert.Equal(Prompt().ModelInstructions, withoutMap.Prompt.ModelInstructions);
        Assert.Equal(0, withoutMap.DroppedPassages);
    }

    [Fact]
    public void An_agent_whose_instructions_leave_no_room_for_its_retrieval_budget_is_logged_at_startup()
    {
        const string marker = "instructions-of-the-application";
        var state = Register(assistant => assistant
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat", Instructions = Instructions(marker, 29_600) })
            .ConfigureAgentContext("project-assistant", context => context.UseRetrievedContext(retrieval => retrieval.Sources("documents"))));

        var warning = Assert.Single(Warnings(state));

        Assert.StartsWith("Agent project-assistant retrieves up to 6000 characters", warning, StringComparison.Ordinal);
        Assert.Contains("of at most 32768", warning);
        Assert.DoesNotContain(marker, warning);
    }

    [Fact]
    public void The_startup_check_counts_the_seed_of_the_application_context_and_the_lowest_budget()
    {
        var withSeed = Register(assistant => assistant
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat", Instructions = Instructions("agent", 15_000) })
            .UseDefaultApplicationContext(Instructions("context", 15_000))
            .ConfigureAgentContext("project-assistant", context => context.UseRetrievedContext(retrieval => retrieval.Sources("documents"))));
        var withinBudget = Register(assistant => assistant
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat", Instructions = Instructions("agent", 30_000) })
            .WithLimits(limits => limits.MaxRetrievedCharacters = 1_000)
            .ConfigureAgentContext("project-assistant", context => context.UseRetrievedContext(retrieval => retrieval.Sources("documents"))));

        Assert.Single(Warnings(withSeed));
        Assert.Empty(Warnings(withinBudget));
    }

    [Fact]
    public void The_startup_check_stays_silent_when_the_budget_fits_or_the_agent_does_not_retrieve()
    {
        var fits = Register(assistant => assistant
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat", Instructions = Instructions("agent", 20_000) })
            .ConfigureAgentContext("project-assistant", context => context.UseRetrievedContext(retrieval => retrieval.Sources("documents"))));
        var withoutRetrieval = Register(assistant => assistant
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat", Instructions = Instructions("agent", 32_000) })
            .ConfigureAgentContext("project-assistant", context => context.UseApplicationMap()));

        Assert.Empty(Warnings(fits));
        Assert.Empty(Warnings(withoutRetrieval));
    }

    [Fact]
    public void A_skill_whose_instructions_leave_no_room_for_the_retrieval_budget_is_logged_at_startup()
    {
        var state = Register(assistant => assistant
            .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat", Instructions = Instructions("agent", 20_000) })
            .ConfigureAgent("project-assistant", agent => agent
                .Grant("projects.*")
                .Skill("long", skill => skill
                    .Description("Works long.")
                    .Instructions(Instructions("long skill", 10_000)))
                .Skill("short", skill => skill
                    .Description("Works short.")
                    .Instructions(Instructions("short skill", 100))))
            .ConfigureAgentContext("project-assistant", context => context.UseRetrievedContext(retrieval => retrieval.Sources("documents"))));

        var warning = Assert.Single(Warnings(state));

        Assert.StartsWith("Skill long of agent project-assistant retrieves up to", warning, StringComparison.Ordinal);
    }

    private static string Passage(string name)
    {
        return "The roadmap passage " + name + ".";
    }

    private static NhAssistantComposedPrompt Prompt(string map = "")
    {
        return new NhAssistantComposedPrompt(
            new string('i', 1_000),
            "prompt@1",
            "prompt-hash",
            null,
            null,
            "instructions@1",
            "instructions-hash",
            "preferences-hash")
        {
            ApplicationMap = map,
            ApplicationMapHash = map.Length > 0 ? "map-hash" : null,
            TurnData = TurnData,
            FactCount = 2,
            PageEntityCount = 1,
            HadPageContext = true
        };
    }

    private static NhAssistantRetrievedContext Retrieved(NhAiContextResolution resolution, string language = "en")
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI();
        using var provider = services.BuildServiceProvider();
        return new NhAssistantRetrievedContext(resolution, provider.GetRequiredService<INhAiContextFormatter>(), language);
    }

    /// <summary>
    /// Resolves the named passages through the real resolver, which ranks them by id here.
    /// </summary>
    private static async Task<NhAiContextResolution> ResolveAsync(params string[] names)
    {
        var scope = new NhAiExecutionScopeEntry("division", "division-a");
        var created = DateTimeOffset.UtcNow.AddDays(-1);
        var items = names
            .Select((name, index) => new NhAiContextItem(
                "passage-" + index,
                "passage-" + index,
                "documents",
                "text/plain",
                Passage(name),
                NhAiDataClassification.Internal,
                NhAiContextTrust.UntrustedRetrieved,
                [scope],
                created))
            .ToArray();
        var services = new ServiceCollection();
        services.AddSingleton<INhAiContextSource>(NhAiTestContextSource.FromItems("documents", items));
        services.AddSingleton<INhAiContextAuthorizationPolicy>(NhAiTestContextAuthorizationPolicy.Allowed());
        services.AddNewHeapPlatformAI();
        await using var provider = services.BuildServiceProvider();
        await using var serviceScope = provider.CreateAsyncScope();
        return await serviceScope.ServiceProvider.GetRequiredService<INhAiContextResolver>().ResolveAsync(new NhAiContextRequest(
            new NhAiInvocationContext("actor-1", "project-assistance", new Dictionary<string, string>())
            {
                ExecutionScopes = [scope]
            },
            "roadmap",
            NhAiDataClassification.Internal,
            10,
            10_000,
            2_500));
    }

    private static NhAiTextAsset Instructions(string text, int characters)
    {
        return NhAiTextAssetFactory.Create(
            "fit-instructions",
            1,
            text.PadRight(characters, '.'),
            "embedded:NewHeap.Platform.AI.Chat.Tests/fit-instructions",
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.FunctionCalling,
            [],
            "project-context-v1",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Evaluation,
            "project-assistant-v1");
    }

    private static string[] Warnings(NhAssistantRegistrationState state)
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));

        NhAssistantRetrievalChecks.LogCodeWarnings(state, factory.CreateLogger("startup"));

        return logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToArray();
    }

    private static NhAssistantRegistrationState Register(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI();
        services.AddNewHeapAssistant(configure);
        return (NhAssistantRegistrationState)services
            .Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState))
            .ImplementationInstance!;
    }
}
