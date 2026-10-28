using System.Net;
using System.Net.Http.Json;
using System.Resources;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-364: a participant asks on the project page for another project status. The API records it as the
/// intake reviewer's deferred proposal for that person without a run (<c>POST projects/{id}/status-requests</c>,
/// <see cref="SampleProjectStatusRequests"/> over <see cref="INhAssistantProposals.ProposeForAsync"/>), with the
/// texts its approvers read in English and Dutch and the page's request id as the idempotency key. The proposal
/// waits in the reviewer's thread at the project under the intake policy, an approver other than the person who
/// asked decides it, and it executes as a background operation, as in Program.cs.
/// </summary>
public sealed class AssistantProposalForPersonSamplesTests(AssistantProposalForPersonSampleHost host)
    : IClassFixture<AssistantProposalForPersonSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_364_a_participant_asks_for_another_project_status_which_another_approver_approves()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var project = await host.CreateProjectAsync("HOLD", ownerId: host.OwnerId);
        await host.AddMemberAsync(project, host.ColleagueId, ProjectMemberRole.Approver);
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        var request = new ProjectStatusRequestMutateModel
        {
            Status = ProjectStatus.OnHold,
            Reason = "The supplier delivers in three weeks.",
            RequestId = "hold-" + project.ToString("N")
        };

        using var asked = await participant.PostAsJsonAsync($"/projects/{project}/status-requests", request, Json, cancellation);

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var recorded = (await asked.Content.ReadFromJsonAsync<ProjectStatusRequestViewModel>(Json, cancellation))!;
        // The intake policy keeps the person who asked out, and the selector of SPM-356 keeps four eyes while
        // other approvers may decide.
        Assert.Equal(
            ("created", NhAssistantApprovalStatuses.Pending, SampleIntakeReviewerComposition.ApprovalPolicyId, true, false),
            (recorded.Outcome, recorded.Status, recorded.PolicyId, recorded.FourEyes, recorded.Repeated));

        // No run: the proposal waits in the reviewer's thread at the project, for the person who asked.
        var waiting = Assert.Single(await ProposalsAsync(owner, recorded.ConversationId!.Value));
        Assert.Equal(
            (recorded.ApprovalId!.Value, (Guid?)null, host.ParticipantId.ToString("D"), NhAssistantApprovalModes.Deferred, "delegated"),
            (waiting.ApprovalId, waiting.RunId, waiting.InitiatorActorId, waiting.Mode, waiting.GrantSource));
        Assert.Equal((SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type, project.ToString()), (waiting.AgentId, waiting.SubjectType, waiting.SubjectId));
        // Approvers read the texts the application wrote, in their own language, with the person's reason.
        var texts = new ResourceManager(typeof(SampleProjectStatusRequestTexts));
        var presentation = Assert.IsType<NhAssistantApprovalPresentationDto>(waiting.Presentation);
        Assert.Equal(texts.GetString("ToolName", System.Globalization.CultureInfo.GetCultureInfo("en-US")), presentation.ToolDisplayName);
        Assert.Contains(presentation.Fields, field => field.Value == request.Reason);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == project);

        // The page sends the request again: the same proposal, nothing new.
        using var again = await participant.PostAsJsonAsync($"/projects/{project}/status-requests", request, Json, cancellation);
        var repeated = (await again.Content.ReadFromJsonAsync<ProjectStatusRequestViewModel>(Json, cancellation))!;
        Assert.Equal((recorded.ApprovalId, true), (repeated.ApprovalId, repeated.Repeated));
        // Someone outside the project may not ask.
        using var refused = await outsider.PostAsJsonAsync($"/projects/{project}/status-requests", request, Json, cancellation);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(
            NhAssistantErrorCodes.SubjectAccessDenied,
            (await refused.Content.ReadFromJsonAsync<ProjectStatusRequestErrorViewModel>(Json, cancellation))!.Code);

        // The owner approves; the change executes once, with the rights of the person who asked.
        using var decided = await owner.PostAsJsonAsync(
            $"/api/assistant/approvals/{waiting.ApprovalId}/decisions",
            new NhAssistantProposalDecisionRequest("approve", waiting.ProposalHash, "The supplier confirmed the delay."),
            Json,
            cancellation);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var executed = await WaitForStatusAsync(owner, recorded.ConversationId.Value, waiting.ApprovalId, NhAssistantApprovalStatuses.Executed);
        Assert.Equal(host.OwnerId.ToString("D"), executed.DecidedByActorId);
        Assert.Single(host.Projects.StatusChanges, change => change.ProjectId == project && change.Status == ProjectStatus.OnHold);
    }

    private static async Task<NhAssistantProposalDto[]> ProposalsAsync(HttpClient client, Guid conversationId)
    {
        return await client.GetFromJsonAsync<NhAssistantProposalDto[]>($"/api/assistant/threads/{conversationId}/proposals", Json) ?? [];
    }

    /// <summary>
    /// Waits until the proposal has the status; the host's background operations execute it.
    /// </summary>
    private static async Task<NhAssistantProposalDto> WaitForStatusAsync(HttpClient client, Guid conversationId, Guid approvalId, string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ProposalsAsync(client, conversationId)).FirstOrDefault(item => item.ApprovalId == approvalId && item.Status == status) is { } found)
            {
                return found;
            }

            await Task.Delay(AssistantAgentRunSampleHost.PollInterval);
        }

        throw new TimeoutException($"Proposal {approvalId} is not {status} after 90 seconds.");
    }
}

/// <summary>
/// SPM-364: the agent run host with the approval policy selection of Program.cs and the project page's status
/// requests.
/// </summary>
public sealed class AssistantProposalForPersonSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleApprovalPolicySelection();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddScoped<SampleProjectStatusRequests>();
        // The project page's status requests next to the controllers of the agent run host.
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new StatusRequestController()));
    }

    private sealed class StatusRequestController : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo)
        {
            return typeInfo.AsType() == typeof(ProjectStatusRequestController);
        }
    }
}
