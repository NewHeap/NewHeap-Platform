using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// TC8 for agents created in the administration: only within the application's ceiling
/// (<c>UseCompositionsAsData</c>) an administrator switches compositions or scripts on per agent; the
/// setting is versioned with the definition, travels in agent packages, and a turn offers a feature's
/// tools only when the ceiling, the agent's setting and the application's switch all allow it.
/// </summary>
public sealed class AssistantDataAgentCompositionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Admin = "admin-1";
    private const string ScriptingAgent = "data-scripting";
    private const string PlainAgent = "data-plain";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_applications_ceiling_the_administration_refuses_to_switch_them_on(AssistantTestProvider provider)
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient(), provider: provider);
        using var admin = app.CreateClient(Admin, admin: true);

        using var refused = await admin.PostAsync("/api/assistant/admin/agents", Body(ScriptingAgent, compositions: true, scripts: true));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await ReadJsonAsync(refused);
        Assert.Equal("assistant-validation", body.GetProperty("code").GetString());
        Assert.Equal(["not-allowed"], Errors(body, "compositions"));
        Assert.Equal(["not-allowed"], Errors(body, "scripts"));
        // Off is always accepted, and the run catalog says that no ceiling exists.
        using var plain = await admin.PostAsync("/api/assistant/admin/agents", Body(PlainAgent, compositions: false, scripts: false));
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"));
        Assert.Equal(JsonValueKind.Null, catalog.GetProperty("compositionCeiling").ValueKind);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Within_the_ceiling_the_setting_is_saved_and_versioned_with_the_definition_and_only_administrators_save_it(AssistantTestProvider provider)
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            provider: provider,
            assistantBuilder: assistant => assistant.UseCompositionsAsData(data => data.AllowScripts()));
        using var admin = app.CreateClient(Admin, admin: true);
        using var member = app.CreateClient("member-1");
        using var anonymous = app.CreateClient(user: null, access: false);

        using var created = await admin.PostAsync("/api/assistant/admin/agents", Body(ScriptingAgent, scripts: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var agent = await ReadJsonAsync(created);
        Assert.True(agent.GetProperty("scripts").GetBoolean());
        Assert.False(agent.GetProperty("compositions").GetBoolean());
        Assert.Equal(1, agent.GetProperty("version").GetInt32());
        // The ceiling allows scripts only.
        using var compositions = await admin.PutAsync($"/api/assistant/admin/agents/{ScriptingAgent}", Body(ScriptingAgent, compositions: true, expectedVersion: 1));
        Assert.Equal(HttpStatusCode.BadRequest, compositions.StatusCode);
        Assert.Equal(["not-allowed"], Errors(await ReadJsonAsync(compositions), "compositions"));
        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"));
        Assert.False(catalog.GetProperty("compositionCeiling").GetProperty("compositions").GetBoolean());
        Assert.True(catalog.GetProperty("compositionCeiling").GetProperty("scripts").GetBoolean());

        // A save that leaves the setting out keeps it; switching it off is the next version.
        using var kept = await admin.PutAsync($"/api/assistant/admin/agents/{ScriptingAgent}", Body(ScriptingAgent, expectedVersion: 1, description: "Still scripting."));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.True((await ReadJsonAsync(kept)).GetProperty("scripts").GetBoolean());
        using var off = await admin.PutAsync($"/api/assistant/admin/agents/{ScriptingAgent}", Body(ScriptingAgent, scripts: false, expectedVersion: 2));
        var switchedOff = await ReadJsonAsync(off);
        Assert.Equal(3, switchedOff.GetProperty("version").GetInt32());
        Assert.False(switchedOff.GetProperty("scripts").GetBoolean());

        // Only administrators save it.
        using var forbidden = await member.PutAsync($"/api/assistant/admin/agents/{ScriptingAgent}", Body(ScriptingAgent, scripts: true, expectedVersion: 3));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("assistant-forbidden", (await ReadJsonAsync(forbidden)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/assistant/admin/agents", Body(PlainAgent, scripts: true))).StatusCode);
        Assert.False((await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ScriptingAgent)
            .GetProperty("scripts").GetBoolean());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_agent_is_offered_the_script_tool_only_when_the_ceiling_its_setting_and_the_switch_all_allow_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("With scripts.")
            .RespondWithText("Switched off.")
            .RespondWithText("Without a setting.");
        var switchState = new TestCompositionSwitchState();
        await using var host = await CreateHostAsync(provider, model, switchState);
        await CreateDataAgentAsync(host, ScriptingAgent, scripts: true);
        await CreateDataAgentAsync(host, PlainAgent, scripts: false);

        var scripting = await host.CreateConversationAsync(agentId: ScriptingAgent);
        await host.SendAsync(scripting.Id, "Count the late tasks.");
        switchState.Answer = _ => false;
        await host.SendAsync(scripting.Id, "And now?");
        switchState.Answer = _ => true;
        var plain = await host.CreateConversationAsync(agentId: PlainAgent);
        await host.SendAsync(plain.Id, "Count the late tasks.");

        Assert.Contains(TestCompositionToolCatalog.ScriptFunction, Offered(model, 0));
        Assert.DoesNotContain(TestCompositionToolCatalog.ScriptFunction, Offered(model, 1));
        Assert.DoesNotContain(TestCompositionToolCatalog.ScriptFunction, Offered(model, 2));
        // Compositions are outside the ceiling, so neither agent is offered one, whatever the switch says.
        Assert.All([0, 1, 2], request => Assert.DoesNotContain(TestCompositionToolCatalog.OverviewFunction, Offered(model, request)));
        // The switch was asked about scripts for the agent that has them, once per turn, and never for
        // the agent without the setting.
        Assert.Equal(2, switchState.Asked.Count);
        Assert.All(switchState.Asked, context =>
        {
            Assert.Equal(NhAiCompositionFeature.Scripts, context.Feature);
            Assert.Equal(ScriptingAgent, context.AgentId);
        });

        // The effective state names why.
        var scriptingState = (await StatusAsync(host, ScriptingAgent))!;
        Assert.Equal(NhAssistantCompositionReasons.On, Feature(scriptingState, "scripts").Reason);
        Assert.Equal(NhAssistantCompositionReasons.NotAllowedForDataAgents, Feature(scriptingState, "compositions").Reason);
        var plainState = (await StatusAsync(host, PlainAgent))!;
        Assert.Equal(NhAssistantCompositionReasons.OffForAgent, Feature(plainState, "scripts").Reason);
        Assert.All(Feature(plainState, "scripts").Channels, channel => Assert.Equal(NhAssistantCompositionReasons.OffForAgent, channel.Reason));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_setting_travels_in_an_agent_package_and_an_import_needs_the_importing_hosts_ceiling(AssistantTestProvider provider)
    {
        NhAssistantAgentPackage package;
        await using (var source = await CreateHostAsync(provider, new NhAiScriptedChatClient(), null))
        {
            await CreateDataAgentAsync(source, ScriptingAgent, scripts: true);
            await CreateDataAgentAsync(source, PlainAgent, scripts: false);
            var exported = await AssistantAgentPackageTests.PackagesAsync(source, packages => packages.ExportAsync(
                [ScriptingAgent, PlainAgent],
                new NhAssistantAgentPackageExportOptions("scripting-desk"),
                Admin));
            Assert.True(exported.Success, string.Join(", ", exported.AllErrorMessages));
            package = exported.Data!.Package;
        }

        // The package carries the setting only where it is on, so an agent without it hashes as before.
        Assert.True(package.Agents.Single(agent => agent.Id == ScriptingAgent).Definition!.Scripts);
        Assert.Null(package.Agents.Single(agent => agent.Id == ScriptingAgent).Definition!.Compositions);
        Assert.Null(package.Agents.Single(agent => agent.Id == PlainAgent).Definition!.Scripts);
        var json = NhAssistantAgentPackageFormat.Write(package);
        Assert.Contains("\"scripts\": true", json, StringComparison.Ordinal);
        var read = NhAssistantAgentPackageFormat.Read(json).Data!;

        await using (var within = await CreateHostAsync(provider, new NhAiScriptedChatClient(), null))
        {
            var imported = await AssistantAgentPackageTests.PackagesAsync(within, packages => packages.ImportAsync(read, new NhAssistantAgentPackageImportOptions(), Admin));
            Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
            var agent = await AgentAsync(within, ScriptingAgent);
            Assert.True(agent.Compositions.Scripts);
            Assert.False((await AgentAsync(within, PlainAgent)).Compositions.Scripts);
            // The same package again changes nothing and the agent is not changed locally.
            var again = await AssistantAgentPackageTests.PackagesAsync(within, packages => packages.PreviewImportAsync(read, new NhAssistantAgentPackageImportOptions()));
            Assert.All(again.Data!.Parts, part => Assert.Equal(NhAssistantPackagePartOutcomes.Unchanged, part.Outcome));
            Assert.All(again.Data.Parts, part => Assert.False(part.ChangedLocally));
        }

        await using var without = await CreateHostAsync(provider, new NhAiScriptedChatClient(), null, ceiling: false);
        var preview = await AssistantAgentPackageTests.PackagesAsync(without, packages => packages.PreviewImportAsync(read, new NhAssistantAgentPackageImportOptions()));
        Assert.False(preview.Data!.Importable);
        var refused = Assert.Single(preview.Data.Parts, part => part.AgentId == ScriptingAgent && part.Part == "agent");
        Assert.Equal(["not-allowed"], refused.Errors!["scripts"]);
    }

    private static NhAssistantCompositionFeatureStatusDto Feature(NhAssistantCompositionStatusDto status, string feature)
    {
        return status.Features.Single(item => item.Feature == feature);
    }

    private static IReadOnlyList<string> Offered(NhAiScriptedChatClient model, int request)
    {
        return (model.Requests[request].Options?.Tools ?? []).Select(tool => tool.Name).ToArray();
    }

    private static async Task CreateDataAgentAsync(AssistantTestHost host, string agentId, bool scripts)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().CreateAgentAsync(
            new NhAssistantAgentRequest(
                agentId,
                "Scripting desk",
                "Answers with short scripts.",
                "Answer questions about projects.",
                ["projects.*"],
                NhAiAutonomyLevel.Execute)
            {
                Scripts = scripts
            },
            Admin);
        Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
    }

    private static async Task<NhAssistantAgent> AgentAsync(AssistantTestHost host, string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, CancellationToken.None))!;
    }

    private static async Task<NhAssistantCompositionStatusDto?> StatusAsync(AssistantTestHost host, string agentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantCompositionStatus>()
            .GetAsync(agentId, new NhAssistantCompositionStatusRequest(null, Admin), CancellationToken.None);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        TestCompositionSwitchState? switchState,
        bool ceiling = true)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestCompositionToolRecorder>();
                services.AddSingleton(switchState ?? new TestCompositionSwitchState());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestCompositionToolCatalog>());
            },
            assistantBuilder: assistant =>
            {
                assistant.UseCompositionSwitch<TestCompositionSwitch>();
                if (ceiling)
                {
                    assistant.UseCompositionsAsData(data => data.AllowScripts());
                }
            });
    }

    private static HttpContent Body(
        string id,
        bool? compositions = null,
        bool? scripts = null,
        int? expectedVersion = null,
        string description = "Answers with short scripts.")
    {
        return ServerSentEventReader.Json(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["displayName"] = "Scripting desk",
            ["description"] = description,
            ["instructions"] = "Answer questions about projects.",
            ["toolSelectors"] = new[] { "projects.*" },
            ["autonomy"] = "execute",
            ["isEnabled"] = true,
            ["expectedVersion"] = expectedVersion,
            ["compositions"] = compositions,
            ["scripts"] = scripts
        });
    }

    private static string[] Errors(JsonElement body, string field)
    {
        return body.GetProperty("errors").GetProperty(field).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
