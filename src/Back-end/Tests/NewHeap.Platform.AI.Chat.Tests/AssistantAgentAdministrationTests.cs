using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantAgentAdministrationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Code_agents_are_upserted_idempotently_and_refreshed_when_their_code_changes(
        AssistantTestProvider provider)
    {
        var state = CreateState(AssistantTestData.Agent());
        await using var services = await CreateServicesAsync(provider, state);

        await new NhAssistantSeeder(Factory(services), state).EnsureSeededAsync(CancellationToken.None);
        await new NhAssistantSeeder(Factory(services), state).EnsureSeededAsync(CancellationToken.None);
        var first = await SingleAgentAsync(services);

        var changed = CreateState(AssistantTestData.Agent() with { ToolSelectors = ["projects.search"] });
        await new NhAssistantSeeder(Factory(services), changed).EnsureSeededAsync(CancellationToken.None);
        var refreshed = await SingleAgentAsync(services);

        Assert.Equal(NhAssistantAgentSources.Code, first.Source);
        Assert.Equal(1, first.Version);
        Assert.False(first.IsOverridden);
        Assert.Equal(2, refreshed.Version);
        Assert.Equal("[\"projects.search\"]", refreshed.ToolSelectorsJson);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_overridden_code_agent_keeps_source_code_survives_code_changes_and_can_be_reset(
        AssistantTestProvider provider)
    {
        var state = CreateState(AssistantTestData.Agent());
        await using var services = await CreateServicesAsync(provider, state);
        await using var scope = services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var original = (await catalog.FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;

        var overridden = await administration.UpdateAsync(
            Input(original, instructions: "Answer only about budgets."),
            original.Definition.Version,
            "admin-1",
            CancellationToken.None);
        var stale = await administration.UpdateAsync(
            Input(original, instructions: "Something else."),
            original.Definition.Version,
            "admin-2",
            CancellationToken.None);
        var codeChanged = CreateState(AssistantTestData.Agent() with { ToolSelectors = ["projects.search"] });
        await new NhAssistantSeeder(Factory(services), codeChanged).EnsureSeededAsync(CancellationToken.None);
        var afterCodeChange = (await catalog.FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;
        var reset = await administration.ResetAsync("project-assistant", "admin-1", CancellationToken.None);
        var delete = await administration.DeleteAsync("project-assistant", "admin-1", CancellationToken.None);

        Assert.True(overridden.Success);
        Assert.Equal(NhAssistantAgentSources.Code, overridden.Data!.Source);
        Assert.True(overridden.Data.IsOverridden);
        Assert.Equal("Answer only about budgets.", overridden.Data.Definition.Instructions.Content);
        Assert.Equal("project-assistant-instructions", overridden.Data.Definition.Instructions.Manifest.Id);
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(stale));
        Assert.True(afterCodeChange.IsOverridden);
        Assert.Equal("Answer only about budgets.", afterCodeChange.Definition.Instructions.Content);
        Assert.True(reset.Success);
        Assert.Equal(NhAssistantAgentSources.Code, reset.Data!.Source);
        Assert.False(reset.Data.IsOverridden);
        Assert.Equal(AssistantTestData.Instructions.Content, reset.Data.Definition.Instructions.Content);
        Assert.Equal(AssistantTestData.Instructions.Manifest.ContentHash, reset.Data.Definition.Instructions.Manifest.ContentHash);
        Assert.Equal(NhAssistantAdminErrorCodes.CodeAgentNotDeletable, Code(delete));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Administrators_create_disable_and_delete_their_own_agents(AssistantTestProvider provider)
    {
        var state = CreateState(AssistantTestData.Agent());
        state.ChatProfileName = "project-chat";
        await using var services = await CreateServicesAsync(provider, state);
        await using var scope = services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var input = new NhAssistantAgentInput(
            "budget-helper",
            "Budget helper",
            "Answers budget questions.",
            "Explain budgets briefly.",
            ["projects.search"],
            [],
            null,
            NhAiAutonomyLevel.Observe);

        var created = await administration.CreateAsync(input, "admin-1", CancellationToken.None);
        var duplicate = await administration.CreateAsync(input, "admin-1", CancellationToken.None);
        var invalid = await administration.CreateAsync(input with { Id = "other", ToolSelectors = ["*"] }, "admin-1", CancellationToken.None);
        var unknownServer = await administration.CreateAsync(input with { Id = "other", McpServerIds = ["missing"] }, "admin-1", CancellationToken.None);
        var edited = await administration.UpdateAsync(
            input with { Instructions = "Explain budgets in one sentence." },
            1,
            "admin-1",
            CancellationToken.None);
        var store = scope.ServiceProvider.GetRequiredService<NhAssistantAdminStore>();
        await store.SetAgentStateAsync("budget-helper", NhAssistantAgentRunStates.Stopped, "admin-1", "Wrong answers", CancellationToken.None);
        var visible = await catalog.ListAsync(includeDisabled: false, CancellationToken.None);
        var stopped = await catalog.FindAsync("budget-helper", includeDisabled: true, CancellationToken.None);
        var deleted = await administration.DeleteAsync("budget-helper", "admin-1", CancellationToken.None);

        Assert.True(created.Success);
        Assert.Equal(NhAssistantAgentSources.Admin, created.Data!.Source);
        Assert.Equal("project-chat", created.Data.Definition.ProfileName);
        Assert.Equal("Budget helper", created.Data.Definition.DisplayNameKey);
        Assert.Equal(NhAssistantAdminErrorCodes.AgentExists, Code(duplicate));
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(invalid));
        Assert.Equal(NhAssistantAdminErrorCodes.McpServerNotFound, Code(unknownServer));
        Assert.True(edited.Success);
        Assert.Equal(2, edited.Data!.Definition.Version);
        Assert.Equal(NhAssistantAgentRunStates.Active, edited.Data.RunState);
        // A state change hides a stopped agent without a new version of its definition.
        Assert.DoesNotContain(visible, agent => agent.Id == "budget-helper");
        Assert.Equal(NhAssistantAgentRunStates.Stopped, stopped!.RunState);
        Assert.Equal("Wrong answers", stopped.StateReason);
        Assert.Equal(2, stopped.Definition.Version);
        Assert.True(deleted.Success);
        Assert.Null(await catalog.FindAsync("budget-helper", includeDisabled: true, CancellationToken.None));
        var events = services.GetRequiredService<CapturedBusinessAuditSink>().Events;
        Assert.Contains(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated && evt.ObjectId == "budget-helper" && evt.ActorId == "admin-1");
        Assert.Contains(events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentDeleted);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Administrators_may_store_as_many_tool_selectors_as_the_configured_limit_allows(
        AssistantTestProvider provider)
    {
        var state = CreateState(AssistantTestData.Agent());
        state.ChatProfileName = "project-chat";
        state.Limits = new NhAssistantLimits { MaxToolSelectorsPerAgent = 150 };
        await using var services = await CreateServicesAsync(provider, state);
        await using var scope = services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        string[] selectors = [.. Enumerable.Range(0, 148).Select(index => $"projects.settings-action-{index:000}")];
        var input = new NhAssistantAgentInput(
            "settings-helper",
            "Settings helper",
            "Changes application settings.",
            "Change settings on request.",
            selectors,
            [],
            null,
            NhAiAutonomyLevel.Observe);

        var created = await administration.CreateAsync(input, "admin-1", CancellationToken.None);
        var aboveLimit = await administration.CreateAsync(
            input with { Id = "above-limit", ToolSelectors = [.. selectors, "projects.extra-1", "projects.extra-2", "projects.extra-3"] },
            "admin-1",
            CancellationToken.None);
        string[] unstorable = [.. Enumerable.Range(0, 150).Select(index => $"projects.a-very-long-settings-action-name-that-exceeds-storage-{index:000}")];
        var aboveStorage = await administration.CreateAsync(
            input with { Id = "above-storage", ToolSelectors = unstorable },
            "admin-1",
            CancellationToken.None);

        Assert.True(created.Success);
        Assert.Equal(148, created.Data!.Definition.ToolSelectors.Count);
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(aboveLimit));
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(aboveStorage));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administration_agent_uses_a_profile_the_administration_offers_and_keeps_it_on_update(AssistantTestProvider provider)
    {
        var state = CreateState(AssistantTestData.Agent());
        state.ChatProfileName = "project-chat";
        await using var services = await CreateServicesAsync(provider, state, collection => collection.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("planning-chat", profile => profile
                .UseKeyedClient("planning-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local")
                .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
            // Without function calling an agent cannot use tools: the administration does not offer it.
            ai.AddChatProfile("summary-chat", profile => profile
                .UseKeyedClient("summary-model")
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local")
                .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
        }));
        await using var scope = services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var input = new NhAssistantAgentInput(
            "planning-helper",
            "Planning helper",
            "Plans projects.",
            "Plan the project.",
            ["projects.search"],
            [],
            null,
            NhAiAutonomyLevel.Propose,
            ProfileName: "planning-chat");
        var original = (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>()
            .FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;

        var created = await administration.CreateAsync(input, "admin-1", CancellationToken.None);
        var notOffered = await administration.CreateAsync(input with { Id = "summary-helper", ProfileName = "summary-chat" }, "admin-1", CancellationToken.None);
        var unknown = await administration.CreateAsync(input with { Id = "unknown-helper", ProfileName = "missing-chat" }, "admin-1", CancellationToken.None);
        var standard = await administration.CreateAsync(input with { Id = "standard-helper", ProfileName = null }, "admin-1", CancellationToken.None);
        var kept = await administration.UpdateAsync(input with { Instructions = "Plan briefly.", ProfileName = null }, 1, "admin-1", CancellationToken.None);
        // An editor that sends the agent back with the profile it has is not refused, also when the
        // administration does not offer that profile.
        var echoed = await administration.UpdateAsync(
            input with { Id = "standard-helper", Instructions = "Help as before.", ProfileName = "project-chat" },
            1,
            "admin-1",
            CancellationToken.None);
        // An agent in code takes its profile from its code.
        var code = await administration.UpdateAsync(
            Input(original, "Answer only about budgets.") with { ProfileName = "planning-chat" },
            original.Definition.Version,
            "admin-1",
            CancellationToken.None);

        Assert.Equal("planning-chat", created.Data!.Definition.ProfileName);
        Assert.Equal(["not-found"], Field(notOffered, "profile"));
        Assert.Equal(["not-found"], Field(unknown, "profile"));
        Assert.Equal("project-chat", standard.Data!.Definition.ProfileName);
        Assert.Equal("planning-chat", kept.Data!.Definition.ProfileName);
        Assert.True(echoed.Success);
        Assert.Equal("project-chat", echoed.Data!.Definition.ProfileName);
        Assert.Equal("project-chat", code.Data!.Definition.ProfileName);
    }

    /// <summary>
    /// The errors of one field of a validation failure.
    /// </summary>
    private static string[] Field(NewHeap.Platform.Common.Models.TaskResult result, string field)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        return result.GetResultItems()
            .Where(item => item.Name == NhAssistantValidationErrors.FieldPrefix + field)
            .SelectMany(item => item.ErrorMessages.Select(message => message.ToString()))
            .ToArray();
    }

    private static NhAssistantAgentInput Input(NhAssistantAgent agent, string instructions)
    {
        return new NhAssistantAgentInput(
            agent.Id,
            agent.Definition.DisplayNameKey,
            agent.Definition.DescriptionKey,
            instructions,
            agent.Definition.ToolSelectors,
            [],
            agent.Definition.RequiredPolicy,
            agent.Definition.Autonomy);
    }

    private static string? Code(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static async Task<Entities.AssistantAgent> SingleAgentAsync(ServiceProvider services)
    {
        await using var context = Factory(services).CreateDbContext();
        return await context.Agents.SingleAsync();
    }

    private static NhAssistantDbContextFactory Factory(IServiceProvider services)
    {
        return services.GetRequiredService<NhAssistantDbContextFactory>();
    }

    private static NhAssistantRegistrationState CreateState(NhAssistantAgentDefinition agent)
    {
        var state = new NhAssistantRegistrationState { StorageProvider = "test" };
        state.AddAgent(agent);
        return state;
    }

    private Task<ServiceProvider> CreateServicesAsync(
        AssistantTestProvider provider,
        NhAssistantRegistrationState state,
        Action<IServiceCollection>? configure = null)
    {
        return database.CreateStorageAsync(provider, configure: services =>
        {
            configure?.Invoke(services);
            services.AddSingleton(state);
            services.AddSingleton<NhAssistantAgentRegistry>();
            services.AddSingleton<NhAssistantSeeder>();
            services.AddScoped<NhAssistantAdminStore>();
            services.AddScoped<NhAssistantAgentCatalog>();
            services.AddScoped<NhAssistantAgentAdministration>();
            // What an agent works on is checked against the registered subject types.
            services.AddSingleton<NhAssistantWorksOnRule>();
            services.AddScoped<NhAssistantRunStore>();
            services.AddScoped<NewHeap.Platform.AI.Chat.Runs.NhAssistantSubjectAccessService>();
            // Changes of administration agents reach the run configuration defined as data.
            services.AddLogging();
            services.AddSingleton<NhAssistantRunConfigurationStore>();
            services.AddSingleton<NhAssistantRunConfigurationValidator>();
            services.AddSingleton<NhAssistantRunConfigurations>();
            services.AddSingleton<CapturedBusinessAuditSink>();
            services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
        });
    }
}
