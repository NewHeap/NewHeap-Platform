using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-331: the sample's operations code changes the assistant through the platform's services. It
/// connects the milestones MCP server to the status advisor without changing the advisor's definition,
/// finds which skills and agents use the risk review's text, and compares a shipped agent package with
/// the host without an export. SPM-351: it proposes the risk review's version 2 as candidates in shadow.
/// SPM-353: an administrator compares the shipped package with the host over HTTP.
/// </summary>
public sealed class AssistantOperationsSamplesTests(AssistantOperationsSampleHost host)
    : IClassFixture<AssistantOperationsSampleHost>
{
    private const string RiskDesk = "sample-risk-desk";
    /// <summary>SPM-353: the package the risk desk ships in, apart from the one SPM-331 installs.</summary>
    private const string ShippedDeskPackage = "sample-risk-desk-shipped";
    private const string ApiKey = "sample-milestones-key-331";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_331_the_operations_code_assigns_an_mcp_server_to_the_status_advisor_through_the_service()
    {
        await using var milestones = await SampleMcpServer.StartAsync(ApiKey);
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        var advisor = await AgentAsync(administrator, SampleStatusAdvisorComposition.AgentId);
        try
        {
            var connected = await OperationsAsync(operations => operations.ConnectMilestonesAsync(milestones.Url, ApiKey, host.OwnerId, CancellationToken.None));

            Assert.True(connected.Success, string.Join("; ", connected.GetResultItems().Select(item => item.Name)));
            Assert.Equal([SampleStatusAdvisorComposition.AgentId], connected.Data!.AssignedAgentIds);
            Assert.True(connected.Data.HasSecret);
            Assert.All(milestones.ApiKeys, key => Assert.Equal(ApiKey, key));

            // The administration shows the same server, assigned to the advisor, without its key; the
            // advisor's definition and version stay as they were.
            var listed = await host.GetJsonAsync(administrator, "/api/assistant/admin/mcp-servers");
            var server = listed.EnumerateArray().Single(item => item.GetProperty("id").GetString() == SampleAssistantOperationsService.MilestonesServerId);
            Assert.Equal(SampleStatusAdvisorComposition.AgentId, Assert.Single(server.GetProperty("assignedAgentIds").EnumerateArray()).GetString());
            Assert.DoesNotContain(ApiKey, listed.GetRawText(), StringComparison.Ordinal);
            var tool = (await host.GetJsonAsync(administrator, "/api/assistant/admin/mcp-servers/milestones/tools")).EnumerateArray().Single();
            Assert.Equal((true, "read-only"), (tool.GetProperty("isEnabled").GetBoolean(), tool.GetProperty("effect").GetString()));
            var after = await AgentAsync(administrator, SampleStatusAdvisorComposition.AgentId);
            Assert.Equal(advisor.GetProperty("version").GetInt32(), after.GetProperty("version").GetInt32());
            Assert.Equal([SampleAssistantOperationsService.MilestonesServerId], after.GetProperty("mcpServerIds").EnumerateArray().Select(item => item.GetString()));

            // The administration's audit, and the assignment as its own event; never the key.
            var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
            Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerCreated && evt.ObjectId == SampleAssistantOperationsService.MilestonesServerId);
            Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpToolUpdated);
            var assigned = Assert.Single(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged);
            Assert.Equal((SampleStatusAdvisorComposition.AgentId, "assigned"), (assigned.AgentId, assigned.ResultCode));
            Assert.DoesNotContain(audit, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentUpdated && evt.ObjectId == SampleStatusAdvisorComposition.AgentId);
            Assert.DoesNotContain(audit, evt => JsonSerializer.Serialize(evt).Contains(ApiKey, StringComparison.Ordinal));

            // A second connection of the same server is refused like the administration refuses it.
            var again = await OperationsAsync(operations => operations.ConnectMilestonesAsync(milestones.Url, ApiKey, host.OwnerId, CancellationToken.None));
            Assert.Contains(again.GetResultItems(), item => item.Name == NhAssistantAdminErrorCodes.McpServerExists);
        }
        finally
        {
            Assert.True((await OperationsAsync(operations => operations.DisconnectMilestonesAsync(host.OwnerId, CancellationToken.None))).Success);
        }

        using var gone = await administrator.GetAsync("/api/assistant/admin/mcp-servers/milestones/tools");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Empty((await AgentAsync(administrator, SampleStatusAdvisorComposition.AgentId)).GetProperty("mcpServerIds").EnumerateArray());
    }

    [Fact]
    public async Task SPM_331_the_operations_code_finds_who_uses_the_risk_review_and_compares_a_shipped_package_without_an_export()
    {
        var first = SampleInstructionAssets.RiskReviewV1;
        var actor = host.OwnerId.ToString("D");
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        try
        {
            // The planner's risk review of SPM-320 and a desk created in the administration both take
            // their instructions from version 1 of the risk review's text.
            Assert.True((await ConfigureAsync(service => service.AddRiskReviewAsync(host.OwnerId, CancellationToken.None))).Success);
            await CreateRiskDeskAsync(administrator);

            var usage = await OperationsAsync(operations => operations.ListRiskReviewUsageAsync(CancellationToken.None));

            Assert.True(usage.Success);
            // The usage lists history too; SPM-351 may have left versions of an earlier risk review here.
            var live = usage.Data!.Skills.Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live).ToArray();
            var skill = Assert.Single(live);
            Assert.Equal(
                (SampleProjectPlannerComposition.AgentId, SampleInstructionAssetService.RiskReviewSkill, NhAssistantSkillDefinitionStatuses.Live, 1, first.Manifest.ContentHash),
                (skill.AgentId, skill.SkillId, skill.Status, skill.AssetVersion, skill.Hash));
            var desk = Assert.Single(usage.Data.Agents);
            Assert.Equal((RiskDesk, 1, first.Manifest.ContentHash), (desk.AgentId, desk.AssetVersion, desk.Hash));

            // Version 2 of the text exists, yet both keep naming version 1 until an administrator saves
            // it as their next version.
            Assert.Equal(2, SampleInstructionAssets.RiskReviewV2.Manifest.Version);
            Assert.DoesNotContain(usage.Data.Skills, item => item.Status == NhAssistantSkillDefinitionStatuses.Candidate);

            // The operator ships the desk as a package, and the deploy installs it on this host.
            NhAssistantAgentPackage shipped;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var packages = scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>();
                var exported = await packages.ExportAsync([RiskDesk], new NhAssistantAgentPackageExportOptions(RiskDesk, Description: "The risk desk of the sample."), actor);
                Assert.True(exported.Success, string.Join("; ", exported.GetResultItems().Select(item => item.Name)));
                shipped = exported.Data!.Package;
                var installed = await packages.ImportAsync(shipped, new NhAssistantAgentPackageImportOptions(Relink: [new(RiskDesk)]), actor);
                Assert.True(installed.Success, string.Join("; ", installed.GetResultItems().Select(item => item.Name)));
            }
            var exports = ExportCount();

            var same = await OperationsAsync(operations => operations.CompareWithHostAsync(shipped, CancellationToken.None));

            Assert.True(same.Success);
            Assert.Equal((RiskDesk, (int?)1, 1), (same.Data!.PackageId, same.Data.InstalledVersion, same.Data.ShippedVersion));
            Assert.Empty(same.Data.DifferentParts);

            // An administrator changes the desk's description on this host: the comparison names the
            // part, the package link says it changed locally, and nothing was exported for either.
            await DescribeRiskDeskAsync(administrator, "Reviews the riskiest tasks of a project, by hand on this host.");
            var changed = await OperationsAsync(operations => operations.CompareWithHostAsync(shipped, CancellationToken.None));

            Assert.Equal([RiskDesk + "/agent"], changed.Data!.DifferentParts);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var installed = (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>().GetPackageAsync(RiskDesk))!;
                Assert.Equal(shipped.Package.Version, installed.Version);
                Assert.True(Assert.Single(installed.Agents).Parts.Single(part => part.Part == "agent").ChangedLocally);
            }
            Assert.Equal(exports, ExportCount());
        }
        finally
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                // Unlinked first: an agent that belongs to a package cannot be deleted.
                await scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>().UnlinkAsync(RiskDesk, null, actor);
            }
            using var deleted = await administrator.DeleteAsync($"/api/assistant/admin/agents/{RiskDesk}");
            Assert.True((await ConfigureAsync(service => service.RemoveRiskReviewAsync(host.OwnerId, CancellationToken.None))).Success);
        }
    }

    [Fact]
    public async Task SPM_351_the_operations_code_proposes_version_2_of_the_risk_review_as_a_candidate_of_the_skills_on_version_1()
    {
        var second = SampleInstructionAssets.RiskReviewV2;
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        try
        {
            // The planner's risk review of SPM-320 and the risk desk take version 1 of the text.
            Assert.True((await ConfigureAsync(service => service.AddRiskReviewAsync(host.OwnerId, CancellationToken.None))).Success);
            await CreateRiskDeskAsync(administrator);

            var proposed = await OperationsAsync(operations => operations.ProposeRiskReviewVersionAsync(second, host.OwnerId, CancellationToken.None));

            Assert.True(proposed.Success, string.Join("; ", proposed.GetResultItems().Select(item => item.Name)));
            Assert.Equal(
                (SampleInstructionAssetComposition.RiskReviewAsset, 2, second.Manifest.ContentHash),
                (proposed.Data!.AssetId, proposed.Data.AssetVersion, proposed.Data.Hash));
            var skill = Assert.Single(proposed.Data.Skills);
            Assert.Equal(
                (SampleProjectPlannerComposition.AgentId, SampleInstructionAssetService.RiskReviewSkill, NhAssistantInstructionAssetCandidateOutcomes.Proposed, 1),
                (skill.AgentId, skill.SkillId, skill.Outcome, skill.LiveAssetVersion));
            // The desk is listed only: an administrator saves it as its next version.
            var desk = Assert.Single(proposed.Data.Agents);
            Assert.Equal((RiskDesk, 1), (desk.AgentId, desk.AssetVersion));

            // The live risk review keeps version 1; the candidate names version 2 and runs in shadow
            // next to it until an administrator promotes or discards it.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var configuration = (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetAsync(SampleProjectPlannerComposition.AgentId))!;
                var review = configuration.DataSkills.Single(item => item.SkillId == SampleInstructionAssetService.RiskReviewSkill);
                Assert.Equal((skill.LiveVersion, 1), (review.Version, review.InstructionsAsset!.Version));
                Assert.Equal(
                    (skill.CandidateVersion, 2, second.Manifest.ContentHash, "Version 2 of the risk review"),
                    (review.Candidate!.Version, review.Candidate.InstructionsAsset!.Version, review.Candidate.InstructionsAsset.Hash, review.Candidate.Reason));
            }
            Assert.Contains(
                host.Services.GetRequiredService<SampleAssistantAuditLog>().Events,
                evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged
                       && evt.SkillId == SampleInstructionAssetService.RiskReviewSkill
                       && evt.ResultCode == "candidate"
                       && evt.InstructionsVersion == SampleInstructionAssetComposition.RiskReviewAsset + "@2");

            // Proposed again, the candidate stays as it is.
            var again = await OperationsAsync(operations => operations.ProposeRiskReviewVersionAsync(second, host.OwnerId, CancellationToken.None));
            Assert.Equal(
                (NhAssistantInstructionAssetCandidateOutcomes.AlreadyProposed, skill.CandidateVersion),
                (Assert.Single(again.Data!.Skills).Outcome, again.Data.Skills[0].CandidateVersion));
        }
        finally
        {
            using var deleted = await administrator.DeleteAsync($"/api/assistant/admin/agents/{RiskDesk}");
            Assert.True((await ConfigureAsync(service => service.RemoveRiskReviewAsync(host.OwnerId, CancellationToken.None))).Success);
        }
    }

    [Fact]
    public async Task SPM_353_an_administrator_compares_the_shipped_desk_with_the_host_over_http_without_an_export()
    {
        var actor = host.OwnerId.ToString("D");
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        try
        {
            // The operator ships the risk desk as a package and the deploy installs it on this host.
            await CreateRiskDeskAsync(administrator);
            NhAssistantAgentPackage shipped;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var packages = scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>();
                var installedBefore = (await packages.GetPackageAsync(ShippedDeskPackage))?.Version ?? 0;
                var exported = await packages.ExportAsync([RiskDesk], new NhAssistantAgentPackageExportOptions(ShippedDeskPackage, installedBefore + 1, "The risk desk of the sample."), actor);
                Assert.True(exported.Success, string.Join("; ", exported.GetResultItems().Select(item => item.Name)));
                shipped = exported.Data!.Package;
                var installed = await packages.ImportAsync(shipped, new NhAssistantAgentPackageImportOptions(Relink: [new(RiskDesk)]), actor);
                Assert.True(installed.Success, string.Join("; ", installed.GetResultItems().Select(item => item.Name)));
            }
            await DescribeRiskDeskAsync(administrator, "Reviews the riskiest tasks of a project, by hand on this host.");
            var exports = ExportCount();

            // The administration reads the desk's parts as an export builds them, and the installed package.
            using var read = await administrator.PostAsJsonAsync(
                "/api/assistant/" + NhAssistantAgentPackageRoutes.Read,
                new NhAssistantAgentPackageReadRequest([RiskDesk]),
                Json);
            Assert.True(read.StatusCode == HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
            var parts = await read.Content.ReadFromJsonAsync<JsonElement>(Json);
            var installedPackage = await host.GetJsonAsync(administrator, $"/api/assistant/admin/agent-packages/{ShippedDeskPackage}");

            // The definition the administrator changed has another hash than the shipped one, and its
            // link says it changed locally; nothing was exported.
            var hashes = Assert.Single(parts.GetProperty("agents").EnumerateArray()).GetProperty("hashes");
            var different = shipped.Agents.Single().Hashes!
                .Where(part => hashes.GetProperty(part.Key).GetString() != part.Value)
                .Select(part => part.Key)
                .ToArray();
            Assert.Equal(["agent"], different);
            Assert.Equal(shipped.Package.Version, installedPackage.GetProperty("version").GetInt32());
            var agentLink = Assert.Single(installedPackage.GetProperty("agents").EnumerateArray())
                .GetProperty("parts").EnumerateArray()
                .Single(part => part.GetProperty("part").GetString() == "agent");
            Assert.True(agentLink.GetProperty("changedLocally").GetBoolean());
            Assert.Equal(shipped.Agents.Single().Hashes!["agent"], agentLink.GetProperty("hash").GetString());
            Assert.Equal(exports, ExportCount());

            // A package this host never installed is not found, and only administrators read either.
            using (var missing = await administrator.GetAsync("/api/assistant/admin/agent-packages/sample-unknown-package"))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                Assert.Equal(NhAssistantErrorCodes.PackageNotFound, (await missing.Content.ReadFromJsonAsync<NhAssistantErrorDto>(Json))!.Code);
            }
            using (var forbidden = await colleague.PostAsJsonAsync("/api/assistant/admin/agent-packages/read", new NhAssistantAgentPackageReadRequest([RiskDesk]), Json))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }
            using (var forbidden = await colleague.GetAsync($"/api/assistant/admin/agent-packages/{ShippedDeskPackage}"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }
        }
        finally
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                // Unlinked first: an agent that belongs to a package cannot be deleted.
                await scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>().UnlinkAsync(RiskDesk, null, actor);
            }
            using var deleted = await administrator.DeleteAsync($"/api/assistant/admin/agents/{RiskDesk}");
        }
    }

    private int ExportCount()
    {
        return host.Services.GetRequiredService<SampleAssistantAuditLog>().Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentPackageExported);
    }

    /// <summary>
    /// A desk created in the administration whose instructions are version 1 of the risk review's text.
    /// </summary>
    private static async Task CreateRiskDeskAsync(HttpClient administrator)
    {
        using var created = await administrator.PostAsJsonAsync("/api/assistant/admin/agents", new
        {
            id = RiskDesk,
            displayName = "Risk desk",
            description = "Reviews the riskiest tasks of a project.",
            instructionsAsset = SampleInstructionAssets.ReferenceOf(SampleInstructionAssets.RiskReviewV1),
            toolSelectors = new[] { "projects.*" },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = SampleAssistantComposition.AccessPolicy,
            autonomy = "propose",
            offeredInChat = false,
            worksOnSubjectTypes = new[] { ProjectSubjects.Type },
            worksOnAllSubjects = false
        }, Json);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Changes the desk's description in the administration, keeping everything else.
    /// </summary>
    private async Task DescribeRiskDeskAsync(HttpClient administrator, string description)
    {
        var desk = await AgentAsync(administrator, RiskDesk);
        using var updated = await administrator.PutAsJsonAsync($"/api/assistant/admin/agents/{RiskDesk}", new
        {
            id = RiskDesk,
            displayName = desk.GetProperty("displayName").GetString(),
            description,
            instructionsAsset = SampleInstructionAssets.ReferenceOf(SampleInstructionAssets.RiskReviewV1),
            toolSelectors = desk.GetProperty("toolSelectors").EnumerateArray().Select(item => item.GetString()).ToArray(),
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = desk.GetProperty("requiredPolicy").GetString(),
            autonomy = desk.GetProperty("autonomy").GetString(),
            isEnabled = true,
            expectedVersion = desk.GetProperty("version").GetInt32()
        }, Json);
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> AgentAsync(HttpClient administrator, string agentId)
    {
        var agents = await host.GetJsonAsync(administrator, "/api/assistant/admin/agents");
        return agents.EnumerateArray().Single(item => item.GetProperty("id").GetString() == agentId);
    }

    private async Task<TaskResult<T>> OperationsAsync<T>(Func<SampleAssistantOperationsService, Task<TaskResult<T>>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleAssistantOperationsService>());
    }

    private async Task<TaskResult> OperationsAsync(Func<SampleAssistantOperationsService, Task<TaskResult>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleAssistantOperationsService>());
    }

    private async Task<TaskResult<T>> ConfigureAsync<T>(Func<SampleInstructionAssetService, Task<TaskResult<T>>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleInstructionAssetService>());
    }

    private async Task<TaskResult> ConfigureAsync(Func<SampleInstructionAssetService, Task<TaskResult>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleInstructionAssetService>());
    }
}

/// <summary>
/// SPM-331: the agent run host of the samples with the operations service, as Program.cs registers it.
/// </summary>
public sealed class AssistantOperationsSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddScoped<SampleAssistantOperationsService>();
    }
}

/// <summary>
/// SPM-331: the sample's integration tests may run a host of the environment <c>Testing</c>. Such a
/// host reaches an MCP server on loopback only because it sets
/// <c>NewHeap:AI:Assistant:Mcp:AllowLoopback</c>, which a Production host refuses at start; cloud
/// metadata hosts and link-local addresses stay blocked.
/// </summary>
public sealed class AssistantLoopbackSamplesTests(AssistantLoopbackSampleHost host)
    : IClassFixture<AssistantLoopbackSampleHost>
{
    private const string ApiKey = "sample-milestones-key-331";

    [Fact]
    public async Task SPM_331_a_testing_host_reaches_a_loopback_mcp_server_with_allow_loopback()
    {
        const string actor = "spm-331-operator";
        await using var milestones = await SampleMcpServer.StartAsync(ApiKey);
        await using var scope = host.Services.CreateAsyncScope();
        var servers = scope.ServiceProvider.GetRequiredService<INhAssistantMcpServers>();

        var created = await servers.CreateAsync(Server(milestones.Url), actor);
        var tested = await servers.TestAsync(SampleAssistantOperationsService.MilestonesServerId);
        var metadata = await servers.CreateAsync(Server("http://169.254.169.254/latest/mcp") with { Id = "metadata" }, actor);

        Assert.True(created.Success, string.Join("; ", created.GetResultItems().Select(item => item.Name)));
        Assert.True(tested.Data!.Ok, tested.Data.Code);
        Assert.Equal(1, tested.Data.ToolCount);
        Assert.Contains(metadata.GetResultItems(), item => item.Name == NhAssistantAdminErrorCodes.McpHostBlocked);
        Assert.True((await servers.DeleteAsync(SampleAssistantOperationsService.MilestonesServerId, actor)).Success);
    }

    private static NhAssistantMcpServerRequest Server(string url)
    {
        return new NhAssistantMcpServerRequest(
            SampleAssistantOperationsService.MilestonesServerId,
            "Milestones",
            url,
            NhAssistantMcpAuthModes.ApiKeyHeader,
            null,
            ApiKey,
            null,
            true);
    }
}

/// <summary>
/// SPM-331: the sample assistant in a host of the environment <c>Testing</c> that lets MCP servers use
/// loopback, as an application's integration tests configure it.
/// </summary>
public sealed class AssistantLoopbackSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    protected override string EnvironmentName => "Testing";

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        // Off by default and refused in Production; Development needs no setting.
        settings["NewHeap:AI:Assistant:Mcp:AllowLoopback"] = "true";
    }
}
