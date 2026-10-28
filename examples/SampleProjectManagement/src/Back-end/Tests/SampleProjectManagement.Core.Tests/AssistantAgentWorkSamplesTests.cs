using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-365: an operator switches agent work off on the API host with
/// <c>NewHeap:AI:Assistant:AgentWork</c> (<see cref="NhAssistantOptions.AgentWork"/>), for example while
/// the planning service is down, and the host keeps serving. People chat with the project assistant, and
/// its consultation of the intake reviewer answers <c>assistant-agent-work-off</c> as a tool result;
/// <c>GET status</c>, the administration and the maintenance page report <c>agentWork: false</c>; a new
/// project's intake review is refused with that code and starts no run. Switching it on again in the
/// configuration, which the host reloads, lets the next project's review start. Runs execute as
/// background operations through Hangfire, as in Program.cs.
/// </summary>
public sealed class AssistantAgentWorkSamplesTests(AssistantAgentWorkSampleHost host)
    : IClassFixture<AssistantAgentWorkSampleHost>
{
    [Fact]
    public async Task SPM_365_the_host_serves_people_and_the_administration_while_its_agents_do_not_work()
    {
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        var project = await host.CreateProjectAsync("QUIET", ownerId: host.OwnerId);

        // The status and the administration serve and say that agents do not work.
        var status = await host.GetJsonAsync(owner, "/api/assistant/status");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("agentWork").GetBoolean());
        var agents = await host.GetJsonAsync(administrator, "/api/assistant/admin/agents");
        Assert.All(agents.EnumerateArray(), agent => Assert.False(agent.GetProperty("agentWork").GetBoolean()));
        var states = await MaintenanceStatesAsync();
        Assert.All(states, state =>
        {
            Assert.False(state.AgentWork);
            Assert.False(state.Active);
        });
        Assert.Equal(
            NhAssistantAgentRunStates.Active,
            states.Single(state => state.AgentId == SampleIntakeReviewerComposition.AgentId).RunState);

        // A new project's intake review starts no run.
        var refused = Assert.Single(await host.PublishProjectCreatedAsync(project, createdBy: host.OwnerId));
        Assert.Equal(SampleIntakeReviewerComposition.TriggerId, refused.TriggerId);
        Assert.Equal(NhAssistantTriggerOutcomes.Rejected, refused.Outcome);
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, refused.ReasonCode);
        Assert.Null(refused.RunId);

        // The owner keeps chatting; the assistant's consultation of the intake reviewer is refused.
        var chat = await host.CreateConversationAsync(owner);
        var events = await host.SendAsync(owner, chat, $"What does the intake reviewer know about project {project:D}?");
        var consulted = events.Single(evt => evt.Name == "tool.completed").Data;
        Assert.Equal("failed", consulted.GetProperty("status").GetString());
        Assert.Equal(NhAssistantErrorCodes.AgentWorkOff, consulted.GetProperty("resultCode").GetString());
        Assert.Equal(
            NhAssistantTurnStatuses.Completed,
            events.Single(evt => evt.Name == "turn.completed").Data.GetProperty("status").GetString());

        // Switched on again in the configuration, the next project's review starts.
        host.SetAgentWork(true);
        try
        {
            Assert.True((await host.GetJsonAsync(owner, "/api/assistant/status")).GetProperty("agentWork").GetBoolean());
            var next = await host.CreateProjectAsync("AWAKE", ownerId: host.OwnerId);
            var started = Assert.Single(await host.PublishProjectCreatedAsync(next, createdBy: host.OwnerId));
            Assert.Equal(NhAssistantTriggerOutcomes.Started, started.Outcome);
            Assert.True((await MaintenanceStatesAsync()).Single(state => state.AgentId == SampleIntakeReviewerComposition.AgentId).Active);
        }
        finally
        {
            host.SetAgentWork(false);
        }
    }

    private async Task<IReadOnlyList<NhAssistantAgentState>> MaintenanceStatesAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SampleAgentMaintenanceService>().ListAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// SPM-365: the agent run host of Program.cs with <c>NewHeap:AI:Assistant:AgentWork</c> set to false, as
/// an operator sets it in the host's configuration.
/// </summary>
public sealed class AssistantAgentWorkSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    public const string AgentWorkKey = NhAssistantOptions.SectionName + ":AgentWork";

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        base.ConfigureSettings(settings);
        settings[AgentWorkKey] = "false";
    }

    /// <summary>
    /// Changes the setting and reloads the configuration, as a host does when its settings file changes;
    /// the next piece of work reads it.
    /// </summary>
    public void SetAgentWork(bool on)
    {
        var configuration = (IConfigurationRoot)Services.GetRequiredService<IConfiguration>();
        configuration[AgentWorkKey] = on ? "true" : "false";
        configuration.Reload();
    }
}
