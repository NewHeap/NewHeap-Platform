using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Extensible agent metadata (MD1 to MD3 of the agent runs adoption plan): named, typed entries
/// registered in code, values in code checked at startup as a save checks data, values of agents
/// created at runtime versioned with the agent, overrides of editable entries of agents in code with
/// versions of their own, the lookup by a declared searchable value, removal with the agent, the
/// content-free audit and the administration's authorization; on both relational providers.
/// </summary>
public sealed class AssistantAgentMetadataTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string CostCenterEntry = "cost-center";
    private const string TeamEntry = "team";
    private const string CodeAgent = "project-assistant";
    private const string FollowUp = "follow-up";
    private const string Reporter = "reporter";
    private const string Admin = "admin-1";

    [Fact]
    public void Entries_are_registered_once_and_values_in_code_are_checked_at_startup()
    {
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant.AddAgentMetadata<CostCenter>("Cost_Center")));
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant.AddAgentMetadata<CostCenter>(new string('a', 65))));
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant.AddAgentMetadata<CostCenter>("nh-cost-center")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Validate(assistant => assistant.AddAgentMetadata<CostCenter>(CostCenterEntry, entry => entry.MaxBytes(0))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Validate(assistant => assistant.AddAgentMetadata<CostCenter>(
            CostCenterEntry,
            entry => entry.MaxBytes(NhAssistantAgentMetadataLimits.MaxBytes + 1))));
        var twice = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .AddAgentMetadata<CostCenter>(CostCenterEntry)
            .AddAgentMetadata<CostCenter>(CostCenterEntry)));
        Assert.Contains("already registered", twice.Message);
        Assert.Throws<ArgumentException>(() => Validate(assistant => assistant
            .ConfigureAgent(CodeAgent, run => run.Metadata("Cost Center", new CostCenter("CC-1000")))));

        // Startup refuses a value in code that a save would refuse.
        var unknown = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(CodeAgent, run => run.Metadata(CostCenterEntry, new CostCenter("CC-1000")))));
        Assert.Contains($"sets metadata '{CostCenterEntry}', which is not registered", unknown.Message);
        var otherType = Assert.Throws<InvalidOperationException>(() => Validate(assistant => AddEntries(assistant)
            .ConfigureAgent(CodeAgent, run => run.Metadata(CostCenterEntry, new Team("Planning")))));
        Assert.Contains("as Team, but the entry holds CostCenter", otherType.Message);
        var refused = Assert.Throws<InvalidOperationException>(() => Validate(assistant => AddEntries(assistant)
            .ConfigureAgent(CodeAgent, run => run.Metadata(CostCenterEntry, new CostCenter("1000")))));
        Assert.Contains("value.code: unknown-code", refused.Message);
        var large = Assert.Throws<InvalidOperationException>(() => Validate(assistant => AddEntries(assistant)
            .ConfigureAgent(CodeAgent, run => run.Metadata(CostCenterEntry, new CostCenter("CC-1000", new string('o', 300))))));
        Assert.Contains("value: too-long", large.Message);

        Validate(assistant => AddEntries(assistant)
            .ConfigureAgent(CodeAgent, run => run
                .Metadata(CostCenterEntry, new CostCenter("CC-1000"))
                .Metadata(TeamEntry, new Team("Planning"))));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Entries_of_agents_created_at_runtime_are_checked_on_save_and_make_the_agents_next_version(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var admin = app.CreateClient(Admin, admin: true);

        // The run catalog names the registered entries.
        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"));
        Assert.Equal(
            [(CostCenterEntry, true, 200, true), (TeamEntry, false, NhAssistantAgentMetadataLimits.DefaultMaxBytes, true)],
            catalog.GetProperty("agentMetadata").EnumerateArray()
                .Select(entry => (
                    entry.GetProperty("name").GetString(),
                    entry.GetProperty("editable").GetBoolean(),
                    entry.GetProperty("maxBytes").GetInt32(),
                    entry.GetProperty("indexed").GetBoolean()))
                .ToArray());

        // A new agent's values are checked as every save checks them, field by field.
        await AssertFieldErrorsAsync(
            await admin.PostAsJsonAsync("/api/assistant/admin/agents", NewAgent(FollowUp, new Dictionary<string, object?>
            {
                [CostCenterEntry] = new { code = "1000" },
                [TeamEntry] = new { name = "Planning", color = "blue" },
                ["unknown"] = new { }
            })),
            ("metadata.cost-center.code", "unknown-code"),
            ("metadata.team", "invalid"),
            ("metadata.unknown", "not-found"));
        await AssertFieldErrorsAsync(
            await admin.PostAsJsonAsync("/api/assistant/admin/agents", NewAgent(FollowUp, new Dictionary<string, object?>
            {
                [CostCenterEntry] = new { code = "CC-1000", owner = new string('o', 300) }
            })),
            ("metadata.cost-center", "too-long"));

        // The agent is created with its values at its first version.
        var created = await ReadJsonAsync(
            await admin.PostAsJsonAsync("/api/assistant/admin/agents", NewAgent(FollowUp, new Dictionary<string, object?>
            {
                [CostCenterEntry] = new { code = " CC-2000 " }
            })),
            HttpStatusCode.Created);
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        var entries = await MetadataAsync(admin, FollowUp);
        var costCenter = entries[CostCenterEntry];
        Assert.Equal(("data", 1, 1, "cc-2000"), (
            costCenter.GetProperty("source").GetString(),
            costCenter.GetProperty("version").GetInt32(),
            costCenter.GetProperty("agentVersion").GetInt32(),
            costCenter.GetProperty("indexValue").GetString()));
        Assert.Equal(" CC-2000 ", costCenter.GetProperty("value").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, costCenter.GetProperty("value").GetProperty("owner").ValueKind);
        Assert.True(costCenter.GetProperty("editable").GetBoolean());
        Assert.Equal(Admin, costCenter.GetProperty("changedBy").GetString());
        // An entry that is read-only for agents in code is data of an agent created at runtime.
        Assert.Equal((true, JsonValueKind.Null, 1), (
            entries[TeamEntry].GetProperty("editable").GetBoolean(),
            entries[TeamEntry].GetProperty("source").ValueKind,
            entries[TeamEntry].GetProperty("version").GetInt32()));

        // One entry saved is the agent's next version; the version it was made on must be current.
        var saved = await ReadJsonAsync(await admin.PutAsJsonAsync(
            $"/api/assistant/admin/agents/{FollowUp}/metadata/{TeamEntry}",
            new { value = new { name = "Planning" }, expectedVersion = 1 }));
        Assert.Equal((2, 2, "data"), (saved.GetProperty("version").GetInt32(), saved.GetProperty("agentVersion").GetInt32(), saved.GetProperty("source").GetString()));
        Assert.Equal(2, (await AgentAsync(admin, FollowUp)).GetProperty("version").GetInt32());
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/metadata/{TeamEntry}", new { value = new { name = "Finance" }, expectedVersion = 1 }),
            HttpStatusCode.Conflict,
            NhAssistantAdminErrorCodes.VersionConflict);
        await AssertFieldErrorsAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/metadata/{TeamEntry}", new { value = new { name = "Finance" } }),
            ("expectedVersion", "required"));
        await AssertFieldErrorsAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/metadata/{TeamEntry}", new { expectedVersion = 2 }),
            ("value", "required"));
        await AssertFieldErrorsAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/metadata/{TeamEntry}", new { value = "Planning", expectedVersion = 2 }),
            ("value", "invalid"));
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}/metadata/unknown", new { value = new { }, expectedVersion = 2 }),
            HttpStatusCode.NotFound,
            NhAssistantAdminErrorCodes.MetadataEntryNotFound);
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/unknown-agent/metadata/{TeamEntry}", new { value = new { name = "Planning" }, expectedVersion = 1 }),
            HttpStatusCode.NotFound,
            NhAssistantErrorCodes.AgentNotFound);

        // The definition carries values too, with the same version; null removes one, others stay.
        var updated = await ReadJsonAsync(await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{FollowUp}", NewAgent(
            FollowUp,
            new Dictionary<string, object?> { [CostCenterEntry] = null },
            expectedVersion: 2)));
        Assert.Equal(3, updated.GetProperty("version").GetInt32());
        entries = await MetadataAsync(admin, FollowUp);
        Assert.Equal(JsonValueKind.Null, entries[CostCenterEntry].GetProperty("value").ValueKind);
        Assert.Equal("Planning", entries[TeamEntry].GetProperty("value").GetProperty("name").GetString());
        Assert.Equal(3, entries[TeamEntry].GetProperty("version").GetInt32());

        // Removing a value is the next version too; removing nothing changes nothing.
        var removed = await ReadJsonAsync(await admin.DeleteAsync($"/api/assistant/admin/agents/{FollowUp}/metadata/{TeamEntry}?expectedVersion=3"));
        Assert.Equal((4, JsonValueKind.Null), (removed.GetProperty("agentVersion").GetInt32(), removed.GetProperty("value").ValueKind));
        var nothing = await ReadJsonAsync(await admin.DeleteAsync($"/api/assistant/admin/agents/{FollowUp}/metadata/{TeamEntry}"));
        Assert.Equal(4, nothing.GetProperty("agentVersion").GetInt32());

        // The audit names the entries, never their values.
        var events = app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentMetadataChanged)
            .Select(evt => (evt.AgentId, evt.MetadataEntry, evt.ObjectId, evt.AgentVersion, evt.ResultCode, evt.ActorId))
            .ToArray();
        Assert.Equal(
            [
                (FollowUp, CostCenterEntry, CostCenterEntry, 1, "created", Admin),
                (FollowUp, TeamEntry, TeamEntry, 2, "created", Admin),
                (FollowUp, CostCenterEntry, CostCenterEntry, 3, "removed", Admin),
                (FollowUp, TeamEntry, TeamEntry, 4, "removed", Admin)
            ],
            events);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Editable_entries_of_agents_in_code_are_overrides_with_versions_of_their_own(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var admin = app.CreateClient(Admin, admin: true);

        var entries = await MetadataAsync(admin, CodeAgent);
        Assert.Equal(("code", JsonValueKind.Null, true), (
            entries[CostCenterEntry].GetProperty("source").GetString(),
            entries[CostCenterEntry].GetProperty("version").ValueKind,
            entries[CostCenterEntry].GetProperty("editable").GetBoolean()));
        Assert.Equal("CC-1000", entries[CostCenterEntry].GetProperty("value").GetProperty("code").GetString());
        Assert.False(entries[TeamEntry].GetProperty("editable").GetBoolean());
        var agentVersion = (await AgentAsync(admin, CodeAgent)).GetProperty("version").GetInt32();

        // A read-only entry stays as code says.
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{TeamEntry}", new { value = new { name = "Finance" } }),
            HttpStatusCode.Conflict,
            NhAssistantAdminErrorCodes.MetadataReadOnly);
        await AssertErrorAsync(
            await admin.DeleteAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{TeamEntry}?expectedVersion=1"),
            HttpStatusCode.Conflict,
            NhAssistantAdminErrorCodes.MetadataReadOnly);

        // An editable entry gets an override with a version of its own; the agent keeps its version.
        var overridden = await ReadJsonAsync(await admin.PutAsJsonAsync(
            $"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}",
            new { value = new { code = "CC-3000", owner = "Finance" } }));
        Assert.Equal(("override", 1, agentVersion), (
            overridden.GetProperty("source").GetString(),
            overridden.GetProperty("version").GetInt32(),
            overridden.GetProperty("agentVersion").GetInt32()));
        Assert.Equal("CC-3000", overridden.GetProperty("value").GetProperty("code").GetString());
        Assert.Equal("CC-1000", overridden.GetProperty("codeValue").GetProperty("code").GetString());
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}", new { value = new { code = "CC-3001" } }),
            HttpStatusCode.Conflict,
            NhAssistantAdminErrorCodes.VersionConflict);
        var next = await ReadJsonAsync(await admin.PutAsJsonAsync(
            $"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}",
            new { value = new { code = "CC-3001" }, expectedVersion = 1 }));
        Assert.Equal(2, next.GetProperty("version").GetInt32());
        Assert.Equal(agentVersion, (await AgentAsync(admin, CodeAgent)).GetProperty("version").GetInt32());

        // The definition of an agent in code carries no metadata; its entries change one by one.
        await AssertFieldErrorsAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{CodeAgent}", new
            {
                displayName = "Project assistant",
                description = "Helps with projects.",
                instructions = "Help with projects.",
                toolSelectors = new[] { "projects.*" },
                mcpServerIds = Array.Empty<string>(),
                autonomy = "execute",
                expectedVersion = agentVersion,
                metadata = new Dictionary<string, object?> { [CostCenterEntry] = new { code = "CC-4000" } }
            }),
            ("metadata", "not-allowed"));

        // Removing the override lets the value in code apply again.
        await AssertFieldErrorsAsync(
            await admin.DeleteAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}"),
            ("expectedVersion", "required"));
        await AssertErrorAsync(
            await admin.DeleteAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}?expectedVersion=1"),
            HttpStatusCode.Conflict,
            NhAssistantAdminErrorCodes.VersionConflict);
        var restored = await ReadJsonAsync(await admin.DeleteAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}?expectedVersion=2"));
        Assert.Equal(("code", "CC-1000"), (restored.GetProperty("source").GetString(), restored.GetProperty("value").GetProperty("code").GetString()));

        var events = app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentMetadataChanged)
            .Select(evt => (evt.AgentId, evt.MetadataEntry, evt.AgentVersion, evt.ResultCode))
            .ToArray();
        Assert.Equal(
            [
                (CodeAgent, CostCenterEntry, 1, "created"),
                (CodeAgent, CostCenterEntry, 2, "changed"),
                (CodeAgent, CostCenterEntry, 2, "removed")
            ],
            events);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Agents_are_found_by_a_searchable_value_and_their_metadata_goes_with_them(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);

        // The application creates and changes agents with metadata through INhAssistantAgentConfiguration.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            var refused = await configuration.CreateAgentAsync(
                Request(Reporter, new { code = "nope" }),
                Admin);
            Assert.False(refused.Success);
            Assert.Equal(["unknown-code"], NhAssistantResults.FieldErrorsOf(refused)["metadata.cost-center.code"]);
            Assert.True((await configuration.CreateAgentAsync(Request(FollowUp, new { code = "cc-1000" }), Admin)).Success);
            Assert.True((await configuration.CreateAgentAsync(Request(Reporter, new { code = "CC-2000" }), Admin)).Success);
            var team = await configuration.SaveMetadataAsync(
                Reporter,
                TeamEntry,
                new NhAssistantAgentMetadataRequest(JsonSerializer.SerializeToElement(new { name = "Reporting" }), 1),
                Admin);
            Assert.True(team.Success);
            Assert.Equal(2, team.Data!.AgentVersion);
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var metadata = scope.ServiceProvider.GetRequiredService<INhAssistantAgentMetadata>();

            // The value in code and the runtime agent's value match whatever the case.
            var found = await metadata.ListAgentsAsync(CostCenterEntry, "  CC-1000 ");
            Assert.Equal([FollowUp, CodeAgent], found.Select(agent => agent.AgentId));
            Assert.Equal(
                [NhAssistantAgentMetadataSources.Data, NhAssistantAgentMetadataSources.Code],
                found.Select(agent => agent.Metadata.Single(item => item.Name == CostCenterEntry).Source));
            Assert.Equal("cc-1000", found[0].Get<CostCenter>(CostCenterEntry)!.Code);
            Assert.Null(found[0].Get<Team>(TeamEntry));
            Assert.Equal("CC-1000", found[1].Get<CostCenter>(CostCenterEntry)!.Code);
            Assert.Equal("Planning", found[1].Get<Team>(TeamEntry)!.Name);
            Assert.Equal(["Reporting"], (await metadata.ListAgentsAsync(TeamEntry, "reporting")).Select(agent => agent.Get<Team>(TeamEntry)!.Name));
            Assert.Empty(await metadata.ListAgentsAsync(CostCenterEntry, " "));

            var reporter = (await metadata.GetAgentAsync(Reporter))!;
            Assert.Equal((NhAssistantAgentSources.Admin, 2), (reporter.Source, reporter.Version));
            Assert.Equal([CostCenterEntry, TeamEntry], reporter.Metadata.Select(item => item.Name));
            Assert.Null(await metadata.GetAgentAsync("unknown-agent"));
            await Assert.ThrowsAsync<ArgumentException>(() => metadata.ListAgentsAsync("unknown", "x"));
            Assert.Equal(
                [new NhAssistantAgentMetadataEntryDto(CostCenterEntry, true, 200, true), new NhAssistantAgentMetadataEntryDto(TeamEntry, false, NhAssistantAgentMetadataLimits.DefaultMaxBytes, true)],
                metadata.ListEntries());
        }

        // An override decides for its agent in code.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            Assert.True((await configuration.SaveMetadataAsync(
                CodeAgent,
                CostCenterEntry,
                new NhAssistantAgentMetadataRequest(JsonSerializer.SerializeToElement(new { code = "CC-2000" }), null),
                Admin)).Success);
            var metadata = scope.ServiceProvider.GetRequiredService<INhAssistantAgentMetadata>();
            Assert.Equal([FollowUp], (await metadata.ListAgentsAsync(CostCenterEntry, "CC-1000")).Select(agent => agent.AgentId));
            Assert.Equal([CodeAgent, Reporter], (await metadata.ListAgentsAsync(CostCenterEntry, "cc-2000")).Select(agent => agent.AgentId));
        }

        // The metadata of an agent created at runtime is removed with the agent.
        using var admin = app.CreateClient(Admin, admin: true);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/assistant/admin/agents/{Reporter}")).StatusCode);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
            Assert.Empty(await context.AgentMetadata.Where(item => item.AgentId == Reporter).ToListAsync());
            Assert.Equal(2, await context.AgentMetadata.CountAsync());
            var metadata = scope.ServiceProvider.GetRequiredService<INhAssistantAgentMetadata>();
            Assert.Equal([CodeAgent], (await metadata.ListAgentsAsync(CostCenterEntry, "CC-2000")).Select(agent => agent.AgentId));
        }
    }

    [Fact]
    public async Task People_who_do_not_administer_neither_read_nor_change_metadata()
    {
        await using var app = await StartAsync(AssistantTestProvider.PostgreSql);
        using var reader = app.CreateClient("reader-1");

        await AssertErrorAsync(await reader.GetAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata"), HttpStatusCode.Forbidden, NhAssistantAdminErrorCodes.Forbidden);
        await AssertErrorAsync(
            await reader.PutAsJsonAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}", new { value = new { code = "CC-9000" } }),
            HttpStatusCode.Forbidden,
            NhAssistantAdminErrorCodes.Forbidden);
        await AssertErrorAsync(
            await reader.DeleteAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata/{CostCenterEntry}?expectedVersion=1"),
            HttpStatusCode.Forbidden,
            NhAssistantAdminErrorCodes.Forbidden);
        using var anonymous = app.CreateClient(user: null, access: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/assistant/admin/agents/{CodeAgent}/metadata")).StatusCode);

        // Nothing was stored, and the agents' chat contract carries no metadata.
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Empty(await context.AgentMetadata.ToListAsync());
        var agents = await ReadJsonAsync(await reader.GetAsync("/api/assistant/agents"));
        Assert.All(agents.EnumerateArray(), agent => Assert.False(agent.TryGetProperty("metadata", out _)));
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
                // The code agent is configured without background operations, which runs need.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => AddEntries(assistant)
                .ConfigureAgent(CodeAgent, run => run
                    .Metadata(CostCenterEntry, new CostCenter("CC-1000"))
                    .Metadata(TeamEntry, new Team("Planning"))),
            provider: provider);
    }

    private static NhAssistantBuilder AddEntries(NhAssistantBuilder assistant)
    {
        return assistant
            .AddAgentMetadata<CostCenter>(CostCenterEntry, entry => entry
                .MaxBytes(200)
                .Validate((value, errors) =>
                {
                    if (!value.Code.Trim().StartsWith("CC-", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("code", "unknown-code");
                    }
                })
                .IndexBy(value => value.Code)
                .Editable())
            .AddAgentMetadata<Team>(TeamEntry, entry => entry.IndexBy(value => value.Name));
    }

    private static void Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_metadata", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" });
            configure(assistant);
        });
        services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>().ValidateAgentRuns();
    }

    private static object NewAgent(string id, IReadOnlyDictionary<string, object?> metadata, int? expectedVersion = null)
    {
        return new
        {
            id,
            displayName = "Follow-up",
            description = "Follows up projects.",
            instructions = "Follow up the projects.",
            toolSelectors = new[] { "projects.*" },
            mcpServerIds = Array.Empty<string>(),
            autonomy = "propose",
            expectedVersion,
            metadata
        };
    }

    private static NhAssistantAgentRequest Request(string id, object costCenter)
    {
        return new NhAssistantAgentRequest(id, "Agent " + id, "An agent of the test.", "Work on projects.", ["projects.*"], NhAiAutonomyLevel.Propose)
        {
            Metadata = new Dictionary<string, JsonElement> { [CostCenterEntry] = JsonSerializer.SerializeToElement(costCenter) }
        };
    }

    private static async Task<IReadOnlyDictionary<string, JsonElement>> MetadataAsync(HttpClient admin, string agentId)
    {
        var entries = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{agentId}/metadata"));
        return entries.EnumerateArray().ToDictionary(entry => entry.GetProperty("name").GetString()!, StringComparer.Ordinal);
    }

    private static async Task<JsonElement> AgentAsync(HttpClient admin, string agentId)
    {
        var agents = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"));
        return agents.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == agentId);
    }

    private static async Task AssertFieldErrorsAsync(HttpResponseMessage response, params (string Field, string Error)[] expected)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, body.GetProperty("code").GetString());
            Assert.Equal(
                expected.OrderBy(item => item.Field, StringComparer.Ordinal).ToArray(),
                body.GetProperty("errors").EnumerateObject()
                    .SelectMany(field => field.Value.EnumerateArray().Select(error => (field.Name, error.GetString()!)))
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .ToArray());
        }
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

    /// <summary>A neutral entry of the application: the cost center an agent's work is booked on.</summary>
    public sealed record CostCenter(string Code, string? Owner = null);

    /// <summary>A neutral entry the administration cannot change on agents in code.</summary>
    public sealed record Team(string Name);
}
