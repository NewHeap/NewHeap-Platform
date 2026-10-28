using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Mcp;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-362: an imported MCP tool is never exported again (FB31). The API offers what the planning
/// service's <c>next_milestone</c> knows through a tool it owns, <c>projects.next-milestone</c>: its own
/// contract, policy, capability and exposure, a project of the authorized division only, one fixed
/// remote tool with fixed arguments on the application's own credential, and a bounded answer.
/// </summary>
public sealed class PlanningServiceWrapperSamplesTests
{
    private const string ApiKey = "test-planning-key";
    private const string ProjectKey = "PLAN-1";

    [Fact]
    public async Task The_api_offers_the_planning_services_tool_as_a_tool_of_its_own()
    {
        await using var planning = await SampleMcpServer.StartAsync(ApiKey);
        var divisionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await using var provider = Services(divisionId, projectId, planning.Url);

        // The tool is the application's own: generated from its code, read only, behind its view
        // policy and read capability, and offered to the API's MCP clients like its other tools.
        var descriptor = Assert.Single(new ProjectPlanningMilestoneToolsNhAiCatalog().Descriptors);
        Assert.Equal(ProjectPlanningMilestoneTools.NextMilestoneToolId, descriptor.Id);
        Assert.Equal(NhAiToolEffect.ReadOnly, descriptor.Effect);
        Assert.True(descriptor.Exposure.HasFlag(NhAiToolExposure.Mcp));
        Assert.Contains(ProjectAiTools.ViewPolicy, descriptor.AuthorizationPolicies);
        Assert.Contains(ProjectAiTools.ReadCapability, descriptor.RequiredCapabilities);

        await using var scope = provider.CreateAsyncScope();
        var tools = scope.ServiceProvider.GetRequiredService<ProjectPlanningMilestoneTools>();
        var answered = await tools.NextMilestoneAsync(new ProjectNextMilestoneInput(projectId), Context(divisionId), CancellationToken.None);

        Assert.True(answered.Success);
        Assert.Equal(new ProjectNextMilestone(projectId, $"The next milestone of {ProjectKey} is on Friday."), answered.Data);
        // The planning service heard the project's key only, on the application's own key.
        Assert.Equal([ProjectKey], planning.Calls);
        Assert.All(planning.ApiKeys, key => Assert.Equal(ApiKey, key));

        // A project outside the authorized division never reaches the planning service.
        var foreign = await tools.NextMilestoneAsync(new ProjectNextMilestoneInput(Guid.NewGuid()), Context(divisionId), CancellationToken.None);
        Assert.False(foreign.Success);
        Assert.Single(planning.Calls);
    }

    [Fact]
    public async Task An_imported_planning_tool_may_serve_chats_and_agents_but_is_never_exported_over_mcp()
    {
        await using var planning = await SampleMcpServer.StartAsync(ApiKey);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(planning.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey }
        }));
        var remote = await client.ListToolsAsync();
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAIMcp();
        await using var provider = services.BuildServiceProvider();
        var importer = provider.GetRequiredService<INhAiMcpClientToolImporter>();
        NhAiMcpImportOptions Import(NhAiToolExposure exposure) => new(
            "planning",
            "planning",
            [new NhAiMcpImportedToolPolicy("next_milestone", "next-milestone", "Returns the next milestone of a project.", NhAiToolEffect.ReadOnly, exposure, [ProjectAiTools.ViewPolicy])]);

        // The boundary holds in the importer: an imported tool cannot ask for the MCP exposure.
        Assert.Throws<InvalidOperationException>(() => importer.Import(remote, Import(NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp)));
        var imported = Assert.Single(importer.Import(remote, Import(NhAiToolExposure.Local | NhAiToolExposure.Agent)).Descriptors);
        Assert.False(imported.Exposure.HasFlag(NhAiToolExposure.Mcp));
    }

    [Fact]
    public async Task Without_its_planning_service_the_tool_fails_closed()
    {
        var divisionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await using var provider = Services(divisionId, projectId, url: null);
        await using var scope = provider.CreateAsyncScope();

        var refused = await scope.ServiceProvider.GetRequiredService<ProjectPlanningMilestoneTools>()
            .NextMilestoneAsync(new ProjectNextMilestoneInput(projectId), Context(divisionId), CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Contains(refused.GetResultItems(), item => item.Name == ProjectPlanningMilestoneTools.NotConfiguredCode);
    }

    private static ServiceProvider Services(Guid divisionId, Guid projectId, string? url)
    {
        var settings = new Dictionary<string, string?>();
        if (url is not null)
        {
            settings[SamplePlanningMilestonesComposition.SectionName + ":Url"] = url;
            settings[SamplePlanningMilestonesComposition.SectionName + ":ApiKeyCredential"] = ApiKey;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProjectAiReadService>(new OneProject(divisionId, new ProjectAiApprovalItem(projectId, ProjectKey, "Planning", ProjectStatus.Active)));
        services.AddSamplePlanningMilestones(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    private static NhAiInvocationContext Context(Guid divisionId)
    {
        return new NhAiInvocationContext(
            "actor-1",
            "sample",
            new Dictionary<string, string>
            {
                [ProjectAiTools.DivisionScopeKey] = divisionId.ToString()
            });
    }

    /// <summary>
    /// One project of one division, as the sample's read service finds it.
    /// </summary>
    private sealed class OneProject(Guid divisionId, ProjectAiApprovalItem project) : IProjectAiReadService
    {
        public Task<ProjectAiApprovalItem?> GetForAiApprovalAsync(Guid division, Guid projectId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(division == divisionId && projectId == project.Id ? project : null);
        }

        public Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(Guid division, string? query, int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProjectAiSearchItem>>([]);
        }
    }
}
