using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Instructions from the application's text assets (S7-1 of the agent runs adoption plan): a data skill
/// version and an agent created in the administration store a reference instead of text, a save asks
/// the application's source and refuses a missing asset or another hash, every load checks the hash
/// again, and an asset that changed, disappeared or cannot be read takes the skill out of effect and
/// keeps the agent from running; inline instructions keep working; on both relational providers.
/// </summary>
public sealed class AssistantInstructionAssetTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string FollowUp = "follow-up";
    private const string Follower = "project-follower";
    private const string Admin = "admin-1";
    private const string SkillAsset = "follow-up-instructions";
    private const string AgentAsset = "follower-instructions";
    private const string FirstText = "Name the task that comes next and why.";
    private const string SecondText = "Name the two tasks that come next and why.";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_skill_takes_its_instructions_from_an_asset_that_a_save_checks(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var assets = host.Services.GetRequiredService<TestTextAssets>();
        var first = assets.Put(SkillAsset, 1, FirstText);
        assets.Put("follow-up-untrusted", 1, FirstText, NhAiContextTrust.UntrustedRetrieved);

        // The asset must exist with the hash the administrator saw, and is the version's only text.
        Assert.Equal(["instructionsAsset.hash:invalid"], Fields(await SaveSkillAsync(host, Request(Reference(first) with { Hash = new string('a', 64) }))));
        Assert.Equal(["instructionsAsset:not-found"], Fields(await SaveSkillAsync(host, Request(new NhAssistantInstructionAssetReference(SkillAsset, 2, first.Manifest.ContentHash)))));
        Assert.Equal(["instructionsAsset:invalid"], Fields(await SaveSkillAsync(host, Request(new NhAssistantInstructionAssetReference("Follow Up", 1, first.Manifest.ContentHash)))));
        Assert.Equal(["instructionsAsset:invalid"], Fields(await SaveSkillAsync(host, Request(new NhAssistantInstructionAssetReference("follow-up-untrusted", 1, first.Manifest.ContentHash)))));
        Assert.Equal(["instructions:not-allowed"], Fields(await SaveSkillAsync(host, Request(Reference(first)) with { Instructions = "Also this." })));
        // A source that fails refuses the save without storing anything.
        assets.Fail = true;
        Assert.Equal(NhAssistantAdminErrorCodes.InstructionsAssetUnavailable, Code(await SaveSkillAsync(host, Request(Reference(first)))));
        assets.Fail = false;
        Assert.Empty(await SkillRowsAsync(host));

        var saved = await SaveSkillAsync(host, Request(Reference(first)));
        Assert.True(saved.Success);
        Assert.Equal(Reference(first), saved.Data!.InstructionsAsset);
        Assert.Equal(first.Manifest.ContentHash, saved.Data.InstructionsHash);
        Assert.Null(saved.Data.Instructions);
        Assert.True(saved.Data.InEffect);
        // The skill in effect reads the asset's text; the row stores the reference, never the text.
        Assert.Equal(FirstText, State(host).AgentRuns[AgentId].Skills[FollowUp].Instructions!.Content);
        var row = Assert.Single(await SkillRowsAsync(host));
        Assert.Null(row.Instructions);
        Assert.Equal((SkillAsset, 1, first.Manifest.ContentHash), (row.InstructionsAssetId, row.InstructionsAssetVersion, row.InstructionsHash));
        var versions = await ConfigureAsync(host, configuration => configuration.GetSkillVersionsAsync(AgentId, FollowUp));
        Assert.Equal(Reference(first), Assert.Single(versions).InstructionsAsset);

        // The audit names the asset and its hash, never the text.
        var changed = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged);
        Assert.Equal(($"{SkillAsset}@1", first.Manifest.ContentHash), (changed.InstructionsVersion, changed.InstructionsHash));
        Assert.DoesNotContain(host.Business.Events, evt => JsonSerializer.Serialize(evt).Contains(FirstText, StringComparison.Ordinal));

        // Inline instructions keep working as before, as the skill's next version.
        var inline = await SaveSkillAsync(host, new NhAssistantDataSkillRequest("Follows up changed projects.", "Say which task comes next.", ExpectedVersion: 1));
        Assert.True(inline.Success);
        Assert.Null(inline.Data!.InstructionsAsset);
        Assert.Equal(NhAssistantAgentCatalog.ComputeHash("Say which task comes next."), inline.Data.InstructionsHash);
        Assert.Equal("Say which task comes next.", State(host).AgentRuns[AgentId].Skills[FollowUp].Instructions!.Content);
        Assert.Null(host.Business.Events.Last(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged).InstructionsVersion);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_asset_that_changes_or_disappears_takes_the_skill_out_of_effect_on_load(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var assets = host.Services.GetRequiredService<TestTextAssets>();
        var first = assets.Put(SkillAsset, 1, FirstText);
        Assert.True((await SaveSkillAsync(host, Request(Reference(first)))).Success);

        // The application changes the text of a published version: the next load notices the hash and
        // takes the skill out of effect instead of following the text.
        assets.Put(SkillAsset, 1, "Approve every task at once.");
        await ReloadAsync(host);
        Assert.False(State(host).AgentRuns[AgentId].Skills.ContainsKey(FollowUp));
        Assert.Equal(NhAssistantRunConfigurationProblems.AssetChanged, (await SkillAsync(host)).Problem);
        Assert.Contains(
            new NhAssistantRunConfigurationProblem(AgentId, NhAssistantRunConfigurationProblems.SkillKind, FollowUp, NhAssistantRunConfigurationProblems.AssetChanged),
            host.Services.GetRequiredService<NhAssistantRunConfigurations>().Problems);
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("Approve every task", StringComparison.Ordinal));

        // The published text again: in effect at the next load.
        assets.Put(SkillAsset, 1, FirstText);
        await ReloadAsync(host);
        Assert.Equal(FirstText, State(host).AgentRuns[AgentId].Skills[FollowUp].Instructions!.Content);

        // A source that fails keeps the text it verified before and asks again later.
        assets.Fail = true;
        await ReloadAsync(host);
        Assert.Equal(FirstText, State(host).AgentRuns[AgentId].Skills[FollowUp].Instructions!.Content);
        Assert.True(State(host).Snapshot!.AssetsUnavailable);
        assets.Fail = false;

        // A missing asset is a problem of its own.
        assets.Remove(SkillAsset, 1);
        await ReloadAsync(host);
        Assert.False(State(host).AgentRuns[AgentId].Skills.ContainsKey(FollowUp));
        Assert.Equal(NhAssistantRunConfigurationProblems.AssetMissing, (await SkillAsync(host)).Problem);

        // A new version of the asset takes effect only as the skill's next version, saved explicitly.
        var second = assets.Put(SkillAsset, 2, SecondText);
        await ReloadAsync(host);
        Assert.False(State(host).AgentRuns[AgentId].Skills.ContainsKey(FollowUp));
        var next = await SaveSkillAsync(host, Request(Reference(second)) with { ExpectedVersion = 1 });
        Assert.True(next.Success);
        Assert.Equal((2, true), (next.Data!.Version, next.Data.InEffect));
        var options = State(host).AgentRuns[AgentId].Skills[FollowUp];
        Assert.Equal((SecondText, 2), (options.Instructions!.Content, options.DataVersion));

        // The candidate of a skill is checked as its live version is.
        var candidate = await SaveSkillAsync(host, Request(Reference(first) with { Version = 3 }) with { ExpectedVersion = 2, Apply = NhAssistantSkillApplyModes.Shadow });
        Assert.Equal(["instructionsAsset:not-found"], Fields(candidate));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_created_in_the_administration_takes_its_instructions_from_an_asset_and_does_not_run_without_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The next task is the review.");
        await using var host = await CreateHostAsync(provider, model);
        var assets = host.Services.GetRequiredService<TestTextAssets>();
        var instructions = assets.Put(AgentAsset, 1, "You follow up projects of the team.");

        Assert.Equal(["instructionsAsset.hash:invalid"], Fields(await CreateAgentAsync(host, Reference(instructions) with { Hash = new string('b', 64) })));
        Assert.Equal(["instructions:not-allowed"], Fields(await CreateAgentAsync(host, Reference(instructions), "Follow up the project.")));
        var created = await CreateAgentAsync(host, Reference(instructions));
        Assert.True(created.Success);
        Assert.Equal(Reference(instructions), created.Data!.InstructionsAsset);
        Assert.Null(created.Data.InstructionsProblem);
        Assert.Equal("You follow up projects of the team.", created.Data.Definition.Instructions.Content);
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated);
        Assert.Equal(($"{AgentAsset}@1", instructions.Manifest.ContentHash), (audit.InstructionsVersion, audit.InstructionsHash));
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var row = await context.Agents.AsNoTracking().SingleAsync(agent => agent.Id == Follower);
            Assert.Equal((true, string.Empty, AgentAsset, 1), (row.InstructionsFromAsset, row.Instructions, row.InstructionsAssetId, row.InstructionsAssetVersion));
        }

        // An agent in code keeps its instructions in code.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var codeAgent = (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
            var refused = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().UpdateAsync(
                Input(AgentId, Reference(instructions)) with { ToolSelectors = codeAgent.Definition.ToolSelectors },
                codeAgent.Definition.Version,
                Admin,
                CancellationToken.None);
            Assert.Equal(["instructionsAsset:not-allowed"], Fields(refused));
        }

        // The model reads the asset's text as the agent's instructions.
        var conversation = await host.CreateConversationAsync(agentId: Follower);
        var answered = await host.SendAsync(conversation.Id, "What comes next?");
        Assert.Null(answered.StartCode);
        Assert.Contains("You follow up projects of the team.", Assert.Single(model.Requests).Options!.Instructions, StringComparison.Ordinal);

        // A changed text makes the agent not runnable at the next load; the administration shows why.
        assets.Put(AgentAsset, 1, "You approve everything.");
        await ReloadAsync(host);
        var broken = await FindAgentAsync(host, Follower);
        Assert.Equal(NhAssistantRunConfigurationProblems.AssetChanged, broken.InstructionsProblem);
        Assert.Contains(
            new NhAssistantRunConfigurationProblem(Follower, NhAssistantRunConfigurationProblems.AgentKind, Follower, NhAssistantRunConfigurationProblems.AssetChanged),
            host.Services.GetRequiredService<NhAssistantRunConfigurations>().Problems);
        var refusedTurn = await host.SendAsync(conversation.Id, "And now?");
        Assert.Equal(NhAssistantErrorCodes.AgentInstructionsUnavailable, refusedTurn.StartCode);
        Assert.Single(model.Requests);

        // Saving the agent with inline instructions makes it run again.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var inline = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().UpdateAsync(
                Input(Follower, null, "Follow up the project."),
                broken.Definition.Version,
                Admin,
                CancellationToken.None);
            Assert.True(inline.Success);
            Assert.Null(inline.Data!.InstructionsAsset);
            Assert.Null(inline.Data.InstructionsProblem);
            Assert.Equal("Follow up the project.", inline.Data.Definition.Instructions.Content);
        }
        Assert.Null((await FindAgentAsync(host, Follower)).InstructionsProblem);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_a_source_a_reference_is_refused_and_a_stored_one_is_a_problem(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, withSource: false);
        var reference = new NhAssistantInstructionAssetReference(SkillAsset, 1, NhAssistantAgentCatalog.ComputeHash(FirstText));

        Assert.Equal(["instructionsAsset:not-allowed"], Fields(await SaveSkillAsync(host, Request(reference))));
        Assert.Equal(["instructionsAsset:not-allowed"], Fields(await CreateAgentAsync(host, reference)));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.False(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetCatalog().InstructionAssets);
        }

        // A reference stored while the application still had a source, as after a deploy that removed
        // it, is out of effect with a problem instead of failing the host.
        Assert.True((await SaveSkillAsync(host, new NhAssistantDataSkillRequest("Follows up changed projects.", FirstText))).Success);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var row = await context.AgentSkillDefinitions.SingleAsync();
            row.Instructions = null;
            row.InstructionsAssetId = reference.AssetId;
            row.InstructionsAssetVersion = reference.Version;
            row.InstructionsHash = reference.Hash;
            await context.SaveChangesAsync();
        }
        await ReloadAsync(host);
        Assert.False(State(host).AgentRuns[AgentId].Skills.ContainsKey(FollowUp));
        Assert.Equal(NhAssistantRunConfigurationProblems.NoAssetSource, (await SkillAsync(host)).Problem);
    }

    [Fact]
    public async Task Only_administrators_list_and_read_the_applications_assets_and_save_references()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestTextAssets>();
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .UseTextAssetSource<TestTextAssets>()
                .ConfigureAgent(AgentId, run => run.Grant("projects.*")));
        var assets = app.Services.GetRequiredService<TestTextAssets>();
        var first = assets.Put(AgentAsset, 1, "You follow up projects of the team.");
        assets.Put("retrieved-notes", 1, "Notes from a search.", NhAiContextTrust.UntrustedRetrieved);
        using var admin = app.CreateClient(Admin, admin: true);

        // The run catalog says assets may be chosen; the list offers trusted system instructions only.
        Assert.True((await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"))).GetProperty("instructionAssets").GetBoolean());
        var list = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/instruction-assets"));
        Assert.True(list.GetProperty("supported").GetBoolean());
        var listed = Assert.Single(list.GetProperty("assets").EnumerateArray());
        Assert.Equal((AgentAsset, 1, first.Manifest.ContentHash), (listed.GetProperty("assetId").GetString(), listed.GetProperty("version").GetInt32(), listed.GetProperty("hash").GetString()));
        Assert.False(listed.TryGetProperty("content", out _));
        var text = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/instruction-assets/{AgentAsset}/versions/1"));
        Assert.Equal(("You follow up projects of the team.", first.Manifest.ContentHash), (text.GetProperty("content").GetString(), text.GetProperty("hash").GetString()));
        await AssertErrorAsync(await admin.GetAsync($"/api/assistant/admin/instruction-assets/{AgentAsset}/versions/2"), HttpStatusCode.NotFound, NhAssistantAdminErrorCodes.InstructionsAssetNotFound);
        assets.Fail = true;
        await AssertErrorAsync(await admin.GetAsync("/api/assistant/admin/instruction-assets"), HttpStatusCode.ServiceUnavailable, NhAssistantAdminErrorCodes.InstructionsAssetUnavailable);
        assets.Fail = false;

        // An agent created with a reference reports it with the asset's text, read-only.
        var created = await ReadJsonAsync(
            await admin.PostAsJsonAsync("/api/assistant/admin/agents", NewAgent(Follower, Reference(first))),
            HttpStatusCode.Created);
        Assert.Equal(AgentAsset, created.GetProperty("instructionsAsset").GetProperty("assetId").GetString());
        Assert.Equal(("You follow up projects of the team.", first.Manifest.ContentHash), (created.GetProperty("instructions").GetString(), created.GetProperty("instructionsHash").GetString()));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("instructionsProblem").ValueKind);

        // People who do not administer neither read the assets nor save references.
        using var reader = app.CreateClient("reader-1");
        await AssertErrorAsync(await reader.GetAsync("/api/assistant/admin/instruction-assets"), HttpStatusCode.Forbidden, NhAssistantAdminErrorCodes.Forbidden);
        await AssertErrorAsync(await reader.GetAsync($"/api/assistant/admin/instruction-assets/{AgentAsset}/versions/1"), HttpStatusCode.Forbidden, NhAssistantAdminErrorCodes.Forbidden);
        await AssertErrorAsync(
            await reader.PutAsJsonAsync($"/api/assistant/admin/agents/{AgentId}/skills/{FollowUp}", new { description = "Follows up.", instructionsAsset = Reference(first) }),
            HttpStatusCode.Forbidden,
            NhAssistantAdminErrorCodes.Forbidden);
        await AssertErrorAsync(
            await reader.PostAsJsonAsync("/api/assistant/admin/agents", NewAgent("reader-agent", Reference(first))),
            HttpStatusCode.Forbidden,
            NhAssistantAdminErrorCodes.Forbidden);
        using var anonymous = app.CreateClient(user: null, access: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/assistant/admin/instruction-assets")).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Empty(await context.AgentSkillDefinitions.ToListAsync());
        Assert.False(await context.Agents.AnyAsync(agent => agent.Id == "reader-agent"));
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient? model = null, bool withSource = true)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model ?? new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestTextAssets>();
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
            },
            assistantBuilder: assistant =>
            {
                if (withSource)
                {
                    assistant.UseTextAssetSource<TestTextAssets>();
                }

                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .ConfigureAgent(AgentId, agent => agent.Grant("projects.*"));
            });
    }

    private static NhAssistantInstructionAssetReference Reference(NhAiTextAsset asset)
    {
        return new NhAssistantInstructionAssetReference(asset.Manifest.Id, asset.Manifest.Version, asset.Manifest.ContentHash);
    }

    private static NhAssistantDataSkillRequest Request(NhAssistantInstructionAssetReference reference)
    {
        return new NhAssistantDataSkillRequest("Follows up changed projects.") { InstructionsAsset = reference };
    }

    private static NhAssistantAgentInput Input(string id, NhAssistantInstructionAssetReference? reference, string instructions = "")
    {
        return new NhAssistantAgentInput(
            id,
            "Project follower",
            "Follows up changed projects.",
            instructions,
            ["projects.*"],
            [],
            null,
            NhAiAutonomyLevel.Propose,
            InstructionsAsset: reference);
    }

    private static object NewAgent(string id, NhAssistantInstructionAssetReference reference)
    {
        return new
        {
            id,
            displayName = "Project follower",
            description = "Follows up changed projects.",
            toolSelectors = new[] { "projects.*" },
            mcpServerIds = Array.Empty<string>(),
            autonomy = "propose",
            instructionsAsset = reference
        };
    }

    private static async Task<TaskResult<NhAssistantAgent>> CreateAgentAsync(AssistantTestHost host, NhAssistantInstructionAssetReference reference, string instructions = "")
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
            Input(Follower, reference, instructions),
            Admin,
            CancellationToken.None);
    }

    private static async Task<NhAssistantAgent> FindAgentAsync(AssistantTestHost host, string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, CancellationToken.None))!;
    }

    private static Task<TaskResult<NhAssistantDataSkillDto>> SaveSkillAsync(AssistantTestHost host, NhAssistantDataSkillRequest request)
    {
        return ConfigureAsync(host, configuration => configuration.SaveSkillAsync(AgentId, FollowUp, request, Admin));
    }

    private static async Task<NhAssistantDataSkillDto> SkillAsync(AssistantTestHost host)
    {
        var configuration = await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId));
        return Assert.Single(configuration!.DataSkills);
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static Task ReloadAsync(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunConfigurations>().ReloadAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<AssistantAgentSkillDefinition>> SkillRowsAsync(AssistantTestHost host)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.AgentSkillDefinitions.AsNoTracking().ToListAsync();
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The field errors of a validation failure as <c>field:code</c>, ordered.
    /// </summary>
    private static IReadOnlyList<string> Fields(TaskResult result)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        return NhAssistantResults.FieldErrorsOf(result)
            .SelectMany(field => field.Value.Select(error => field.Key + ":" + error))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, status);
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// The text assets of a test application, kept in memory: a test publishes, changes and removes
/// versions, and lets the source fail.
/// </summary>
public sealed class TestTextAssets : INhAiTextAssetSource
{
    private readonly ConcurrentDictionary<(string Id, int Version), NhAiTextAsset> _assets = new();

    /// <summary>
    /// The source throws, as an unreachable store would.
    /// </summary>
    public bool Fail { get; set; }

    /// <summary>
    /// Runs before the source answers a read, for a test that holds a read until other work arrives.
    /// </summary>
    public Func<string, int, Task>? BeforeGet { get; set; }

    public NhAiTextAsset Put(string id, int version, string content, NhAiContextTrust trust = NhAiContextTrust.TrustedApplication)
    {
        var asset = NhAiTextAssetFactory.Create(
            id,
            version,
            content,
            "test:" + id,
            NhAiAssetRole.SystemInstructions,
            trust,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            id + "-baseline");
        _assets[(id, version)] = asset;
        return asset;
    }

    public void Remove(string id, int version)
    {
        _assets.TryRemove((id, version), out _);
    }

    public async ValueTask<NhAiTextAsset?> GetAsync(string id, int version, CancellationToken cancellationToken = default)
    {
        if (BeforeGet is { } before)
        {
            await before(id, version);
        }
        if (Fail)
        {
            throw new InvalidOperationException("The asset store is offline.");
        }

        return _assets.GetValueOrDefault((id, version));
    }

    public ValueTask<IReadOnlyList<NhAiAssetManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("The asset store is offline.");
        }

        IReadOnlyList<NhAiAssetManifest> manifests = _assets.Values
            .Select(asset => asset.Manifest)
            .OrderBy(manifest => manifest.Id, StringComparer.Ordinal)
            .ThenBy(manifest => manifest.Version)
            .ToArray();
        return ValueTask.FromResult(manifests);
    }
}
