using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Core.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-364 and SPM-370: the API offers the project page's status request to a person's own AI app as the
/// MCP tool <c>projects_request_status_v1</c>. The call records a proposal and changes nothing itself, so
/// it needs no approval of its own, and the request id keeps one proposal per request. In Development the
/// API sends an MCP client that supports links to the portal's intake reviews with that proposal
/// (<c>NewHeap:AI:Assistant:Mcp:ApprovalLinkTemplate</c>); the platform's tests prove the round trip, the
/// stateless retry and that a client's accept never approves.
/// </summary>
public sealed class AssistantMcpElicitationSamplesTests
{
    [Fact]
    public void SPM_364_the_status_request_tool_is_offered_over_mcp_and_records_a_proposal_without_an_approval_of_its_own()
    {
        var tool = Assert.Single(
            new ProjectStatusRequestAiToolsNhAiCatalog().Descriptors,
            descriptor => descriptor.Id == ProjectStatusRequestAiTools.RequestStatusToolId);

        Assert.Equal(ProjectStatusRequestAiTools.RequestStatusToolName, tool.ExportName);
        Assert.Equal(NhAiToolExposure.Mcp, tool.Exposure);
        Assert.Equal(NhAiToolEffect.IdempotentMutation, tool.Effect);
        // The status change is the proposal a second approver decides; the call itself needs no approval.
        Assert.Equal(NhAiApprovalRequirement.NotRequired, tool.Approval);
        Assert.Equal(NhAiIdempotencySupport.ConsumerAuthoritative, tool.Idempotency);
        Assert.Contains(ProjectAiTools.ViewPolicy, tool.AuthorizationPolicies);
    }

    [Fact]
    public void SPM_370_development_sends_mcp_clients_to_the_portals_intake_reviews_for_a_proposal()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ApiDirectory(), "appsettings.Development.json"), optional: false)
            .Build();
        var options = new NhAssistantOptions();
        configuration.GetSection(NhAssistantOptions.SectionName).Bind(options);

        var template = options.Mcp.ApprovalLinkTemplate;

        Assert.NotNull(template);
        Assert.StartsWith("http://sample-project-management-management.dev.localhost:4210/intake-reviews?", template, StringComparison.Ordinal);
        Assert.Single(template.Split(NhAssistantMcpOptions.ApprovalLinkPlaceholder)[1..]);
    }

    private static string ApiDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Applications", "SampleProjectManagement.Api")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("The sample API's directory was not found.")
            : Path.Combine(directory.FullName, "Applications", "SampleProjectManagement.Api");
    }
}
