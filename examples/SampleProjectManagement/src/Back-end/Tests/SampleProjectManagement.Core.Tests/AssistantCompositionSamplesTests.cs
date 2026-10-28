using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-372: the project overview composition chains three governed reads into
/// one tool of the project assistant. The model calls it once; the steps run with the person's active
/// division, the later ones with the project's id from the first, and only the declared overview returns
/// to the model, which saves two rounds. The agent's activity shows the tool calls the call ran in order
/// and the rounds it saved.
/// </summary>
public sealed class AssistantCompositionSamplesTests(AssistantCompositionSampleHost host) : IClassFixture<AssistantCompositionSampleHost>
{
    [Fact]
    public async Task SPM_372_the_project_overview_chains_three_reads_in_one_call_and_the_activity_shows_what_it_saved()
    {
        var projectId = host.Overview.ProjectId;
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_overview_v1", new { input = new { projectId } }, "call-overview")
            .RespondWithText("Roadmap has two open tasks; Ada owns it.");
        host.Model.Use(model);
        using var client = host.CreateClient("spm-372-user");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await host.SendAsync(client, conversationId, "Give me the overview of the roadmap project.");

        // One card for the composition: its steps are calls of the turn, but not of the conversation.
        var completed = Assert.Single(events, evt => evt.Name == "tool.completed").Data;
        Assert.Equal("succeeded", completed.GetProperty("status").GetString());
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Equal(
            [ProjectOverviewAiTools.GetToolId, ProjectOverviewAiTools.ListOpenTasksToolId, ProjectOverviewAiTools.ListMembersToolId],
            host.Overview.Reads.Select(read => read.ToolId).ToArray());
        Assert.All(host.Overview.Reads, read =>
        {
            Assert.Equal(host.DivisionId, read.DivisionId);
            Assert.Equal(projectId, read.ProjectId);
        });

        // The model read the overview only, with the owner resolved from the members.
        var overview = System.Text.Json.JsonSerializer.SerializeToElement(Assert.Single(model.FunctionResults).Result);
        Assert.True(overview.GetProperty("success").GetBoolean());
        var data = overview.GetProperty("data");
        Assert.Equal("Roadmap", data.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("ada", data.GetProperty("owner").GetString());
        Assert.Equal(2, data.GetProperty("openTaskCount").GetInt32());

        // The activity shows the call with its three tool calls and the two rounds they saved.
        using var admin = host.CreateClient("spm-372-admin", admin: true, "app.project.manage");
        var activity = await host.GetJsonAsync(admin, $"/api/assistant/admin/agents/{SampleAssistantComposition.AgentId}/compositions");
        var call = Assert.Single(activity.GetProperty("calls").EnumerateArray());
        Assert.Equal(SampleProjectOverviewComposition.CompositionId, call.GetProperty("toolId").GetString());
        Assert.Equal(
            [ProjectOverviewAiTools.GetToolId, ProjectOverviewAiTools.ListOpenTasksToolId, ProjectOverviewAiTools.ListMembersToolId],
            call.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("toolId").GetString()).ToArray());
        Assert.Equal(2, call.GetProperty("roundsSaved").GetInt32());
        Assert.True(call.GetProperty("resultCharacters").GetInt32() > 0);
    }
}

/// <summary>
/// The sample host with the project overview and compositions switched on in configuration, and the
/// overview's reads answered from memory.
/// </summary>
public sealed class AssistantCompositionSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    public FakeProjectOverview Overview { get; } = new();

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings["NewHeap:AI:Assistant:Compositions"] = "true";
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IProjectAiOverviewReadService>(Overview);
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        services.AddSampleProjectOverview();
    }
}

/// <summary>
/// One project with two open tasks and two members, and the reads the overview made.
/// </summary>
public sealed class FakeProjectOverview : IProjectAiOverviewReadService
{
    private static readonly Guid OwnerId = Guid.Parse("8d1f4c2e-5b6a-4f7e-9c0d-1a2b3c4d5e6f");

    public Guid ProjectId { get; } = Guid.NewGuid();

    public ConcurrentQueue<(string ToolId, Guid DivisionId, Guid ProjectId)> Reads { get; } = new();

    public Task<ProjectAiProjectItem?> GetProjectForAiAsync(Guid divisionId, Guid projectId, CancellationToken cancellationToken = default)
    {
        Reads.Enqueue((ProjectOverviewAiTools.GetToolId, divisionId, projectId));
        return Task.FromResult<ProjectAiProjectItem?>(projectId == ProjectId
            ? new ProjectAiProjectItem(ProjectId, "PRJ-372", "Roadmap", ProjectStatus.Active, null, OwnerId)
            : null);
    }

    public Task<IReadOnlyList<ProjectAiOpenTaskItem>> ListOpenTasksForAiAsync(Guid divisionId, Guid projectId, int limit, CancellationToken cancellationToken = default)
    {
        Reads.Enqueue((ProjectOverviewAiTools.ListOpenTasksToolId, divisionId, projectId));
        return Task.FromResult<IReadOnlyList<ProjectAiOpenTaskItem>>(
        [
            new ProjectAiOpenTaskItem(Guid.NewGuid(), "Plan the launch"),
            new ProjectAiOpenTaskItem(Guid.NewGuid(), "Book the venue")
        ]);
    }

    public Task<IReadOnlyList<ProjectAiMemberItem>> ListMembersForAiAsync(Guid divisionId, Guid projectId, CancellationToken cancellationToken = default)
    {
        Reads.Enqueue((ProjectOverviewAiTools.ListMembersToolId, divisionId, projectId));
        return Task.FromResult<IReadOnlyList<ProjectAiMemberItem>>(
        [
            new ProjectAiMemberItem(OwnerId, "ada", ProjectMemberRole.Approver),
            new ProjectAiMemberItem(Guid.NewGuid(), "grace", ProjectMemberRole.Participant)
        ]);
    }
}
