using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// The input of the project overview: the project, by the id the search or the page names.
/// </summary>
public sealed record SampleProjectOverviewInput(Guid ProjectId);

/// <summary>
/// SPM-372: the project overview, a composition (TC2) that chains three governed reads into one tool,
/// <c>projects.overview</c>. The project assistant otherwise reads a project, then its open tasks, then
/// its members, reading every intermediate result in between; the composition runs the chain on the
/// server and returns only the overview, so the model saves two rounds. Every step is a call of its tool
/// through the shared invoker with the person's own rights and active division, audited under the
/// composition's call, and the agent's activity in the administration shows the tool calls it ran and
/// the rounds it saved (TC1). The composition reads only, so it runs at once in the chat and is
/// read-only over MCP (TC7), where the API's exposure allows compositions.
/// <para>
/// A composition is offered where code allows compositions for the agent
/// (<c>ConfigureAgentContext(...).AllowCompositions()</c>) and the application's switch has them on, in
/// the sample the portal's own setting per environment and agent (SPM-371,
/// <see cref="SampleAssistantCompositionSwitch"/>), and only with the tools its steps call. Call it after
/// <c>AddSampleAssistant</c>.
/// </para>
/// <para>
/// The steps call tools whose descriptors do not depend on the request: the assistant reads them once, at
/// startup, in a scope of its own, so a tool catalog the application registers per scope, such as one
/// over the tools a run imported, does not stop a host that validates scopes, as Development does. Each
/// step runs with the tool of the turn's own services.
/// </para>
/// </summary>
public static class SampleProjectOverviewComposition
{
    public const string CompositionId = "projects.overview";

    public static IServiceCollection AddSampleProjectOverview(this IServiceCollection services)
    {
        services.TryAddScoped<IProjectAiOverviewReadService, ProjectAiOverviewReadService>();
        services.TryAddScoped<ProjectOverviewAiTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectOverviewAiToolsNhAiCatalog>());

        services.AddNewHeapAssistant(assistant => assistant
            .AddComposition<SampleProjectOverviewInput>(CompositionId, composition => composition
                .Describe("Show one project of the active division with its open tasks and its members, and who owns it, in one call.")
                .ExposeOverMcp()
                .Step("project", ProjectOverviewAiTools.GetToolId, context => new { projectId = context.Input.ProjectId })
                // The later steps take the project's id as the application resolved it in the division.
                .Step("open-tasks", ProjectOverviewAiTools.ListOpenTasksToolId, context => new
                {
                    projectId = context.Result("project").GetProperty("id").GetGuid()
                })
                .Step("members", ProjectOverviewAiTools.ListMembersToolId, context => new
                {
                    projectId = context.Result("project").GetProperty("id").GetGuid()
                })
                .Returns(context => Overview(context.Result("project"), context.Result("open-tasks"), context.Result("members"))))
            // The project assistant may use compositions; the application's switch decides whether it does.
            .ConfigureAgentContext(SampleAssistantComposition.AgentId, context => context.AllowCompositions()));
        return services;
    }

    /// <summary>
    /// What returns to the model: the project with its owner, its open tasks and its members.
    /// </summary>
    private static object Overview(JsonElement project, JsonElement openTasks, JsonElement members)
    {
        var ownerId = project.TryGetProperty("ownerUserId", out var owner) && owner.ValueKind == JsonValueKind.String
            ? owner.GetString()
            : null;
        var ownerName = members.EnumerateArray()
            .Where(member => string.Equals(member.GetProperty("userId").GetString(), ownerId, StringComparison.OrdinalIgnoreCase))
            .Select(member => member.GetProperty("displayName").GetString())
            .FirstOrDefault();
        return new
        {
            project,
            owner = ownerName,
            openTaskCount = openTasks.GetArrayLength(),
            openTasks,
            members
        };
    }
}
