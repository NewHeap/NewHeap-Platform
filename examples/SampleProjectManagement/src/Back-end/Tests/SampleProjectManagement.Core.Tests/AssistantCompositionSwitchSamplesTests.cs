using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-371: the application owns the switch of compositions and the script tool. Its own setting, per
/// environment and agent in its own table, answers the assistant's <see cref="INhAssistantCompositionSwitch"/>
/// from the next turn on; code keeps the ceiling, and the assistant's effective state says what it
/// actually does and why. The scripting desk, an agent created as data, uses the script tool within the
/// sample's ceiling for such agents.
/// </summary>
public sealed class AssistantCompositionSwitchSamplesTests(AssistantCompositionSwitchSampleHost host)
    : IClassFixture<AssistantCompositionSwitchSampleHost>
{
    private const string ProjectAssistant = SampleAssistantComposition.AgentId;

    [Fact]
    public async Task SPM_371_the_portals_own_setting_switches_compositions_and_scripts_per_environment_and_agent()
    {
        var cancellation = TestContext.Current.CancellationToken;

        // Off until the portal stores a setting.
        Assert.False(await IsOnAsync(NhAiCompositionFeature.Scripts, ProjectAssistant, tenant: null));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SampleAssistantCompositionSettings>()
                .SaveAsync(string.Empty, ProjectAssistant, compositions: false, scripts: true, Guid.NewGuid(), cancellation);
        }

        // The next question reads the saved setting: scripts on, compositions off, in this environment only.
        Assert.True(await IsOnAsync(NhAiCompositionFeature.Scripts, ProjectAssistant, tenant: null));
        Assert.False(await IsOnAsync(NhAiCompositionFeature.Compositions, ProjectAssistant, tenant: null));
        Assert.False(await IsOnAsync(NhAiCompositionFeature.Scripts, ProjectAssistant, tenant: "another-environment"));
        Assert.False(await IsOnAsync(NhAiCompositionFeature.Scripts, SampleRuntimeAgentSeeder.ScriptingDeskAgentId, tenant: null));
        // An MCP request, which no agent makes, follows the setting of the exposure.
        Assert.False(await IsOnAsync(NhAiCompositionFeature.Scripts, agentId: null, tenant: null));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var listed = await scope.ServiceProvider.GetRequiredService<SampleAssistantCompositionSettings>().ListAsync(string.Empty, cancellation);
            Assert.Contains(listed, setting => setting.AgentId == ProjectAssistant && setting.Scripts && !setting.Compositions);
        }
    }

    [Fact]
    public async Task SPM_371_the_assistant_shows_its_effective_state_and_a_data_agent_gets_scripts_only_within_the_ceiling()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var admin = host.CreateClient("spm-371-admin", admin: true);
        using var member = host.CreateClient("spm-371-member");

        // The scripting desk is created as data with the script tool, within UseCompositionsAsData.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var seeded = await SampleRuntimeAgentSeeder.SeedScriptingDeskAsync(scope.ServiceProvider, Guid.NewGuid(), cancellation);
            Assert.True(seeded.Success, string.Join("; ", seeded.AllErrorMessages));
        }

        var agents = await host.GetJsonAsync(admin, "/api/assistant/admin/agents");
        var desk = agents.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == SampleRuntimeAgentSeeder.ScriptingDeskAgentId);
        Assert.True(desk.GetProperty("scripts").GetBoolean());
        Assert.False(desk.GetProperty("compositions").GetBoolean());
        var assistant = agents.EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == ProjectAssistant);
        // The project assistant allows both in code.
        Assert.True(assistant.GetProperty("compositions").GetBoolean());
        Assert.True(assistant.GetProperty("scripts").GetBoolean());
        var catalog = await host.GetJsonAsync(admin, "/api/assistant/admin/run-catalog");
        Assert.False(catalog.GetProperty("compositionCeiling").GetProperty("compositions").GetBoolean());
        Assert.True(catalog.GetProperty("compositionCeiling").GetProperty("scripts").GetBoolean());

        // Compositions are outside the ceiling for agents created in the administration.
        using var refused = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "spm-371-composer",
            displayName = "Composer",
            description = "Would combine tools.",
            instructions = "Combine the project tools.",
            toolSelectors = new[] { "projects.search" },
            autonomy = "explain",
            isEnabled = true,
            compositions = true
        }), cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using (var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(cancellation)))
        {
            Assert.Equal("not-allowed", body.RootElement.GetProperty("errors").GetProperty("compositions")[0].GetString());
        }

        // What the assistant actually does, next to the portal's checkbox. The sample registers the script
        // tool (SPM-373) and the project overview (SPM-372): compositions are outside the ceiling for
        // agents created in the administration, and the desk's own definition uses the script tool, which
        // the portal's setting of the desk has off.
        var state = await host.GetJsonAsync(admin, $"/api/assistant/admin/agents/{SampleRuntimeAgentSeeder.ScriptingDeskAgentId}/compositions");
        Assert.Equal(SampleRuntimeAgentSeeder.ScriptingDeskAgentId, state.GetProperty("agentId").GetString());
        var features = state.GetProperty("features").EnumerateArray().ToDictionary(
            feature => feature.GetProperty("feature").GetString()!,
            feature => (On: feature.GetProperty("on").GetBoolean(), Reason: feature.GetProperty("reason").GetString()!));
        Assert.Equal((false, NhAssistantCompositionReasons.NotAllowedForDataAgents), features["compositions"]);
        Assert.Equal((false, NhAssistantCompositionReasons.OffByApplication), features["scripts"]);

        // Only the assistant's administrators read it.
        using var forbidden = await member.GetAsync($"/api/assistant/admin/agents/{ProjectAssistant}/compositions", cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private async Task<bool> IsOnAsync(NhAiCompositionFeature feature, string? agentId, string? tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantCompositionSwitch>().IsOnAsync(
            new NhAssistantCompositionSwitchContext(feature, agentId is null ? NhAssistantCompositionChannel.Mcp : NhAssistantCompositionChannel.Chat)
            {
                AgentId = agentId,
                TenantId = tenant
            },
            TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// The sample assistant with the portal's own composition switch and the project overview, as Program.cs
/// composes them after <c>AddSampleAssistant</c>, which registers the script tool.
/// </summary>
public sealed class AssistantCompositionSwitchSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        services.AddSampleAssistantCompositions();
        services.AddSampleProjectOverview();
    }
}
