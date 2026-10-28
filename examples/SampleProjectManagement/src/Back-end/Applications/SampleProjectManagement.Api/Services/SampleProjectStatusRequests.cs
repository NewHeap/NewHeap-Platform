using System.Globalization;
using System.Resources;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-364: a person asks on the project page for another project status, a change that needs a second
/// person's approval. The application does not start a run for it: it records the request as the intake
/// reviewer's deferred proposal for that person through the platform's
/// <see cref="INhAssistantProposals.ProposeForAsync"/>, with the texts its approvers read in English and
/// Dutch, written here, and the page's request id as the idempotency key. The platform checks what a run's
/// proposal meets: the person takes part in the project, the change-status tool's contract and the person's
/// own manage rights, the intake policy with its four eyes (and the application's selector of SPM-356), open
/// duplicates and recent rejections. The proposal waits in the reviewer's thread at the project, the person
/// who asked may not approve it, and once a second approver approves, it executes with the rights of the
/// person who asked.
/// </summary>
public sealed class SampleProjectStatusRequests(
    INhAssistantProposals proposals,
    SampleProjectManagementDbContext dbContext,
    IOptions<RequestLocalizationOptions> localization)
{
    /// <summary>
    /// The longest reason a person gives, which approvers read as a field of the proposal.
    /// </summary>
    public const int MaxReasonLength = 200;

    private static readonly ResourceManager Texts = new(typeof(SampleProjectStatusRequestTexts));

    private static readonly ResourceManager StatusTexts = new(typeof(SampleAssistantToolPresenter));

    /// <summary>
    /// Records the request of <paramref name="user"/> to set the project to <paramref name="status"/>.
    /// Succeeds with the proposal, also when the same request id recorded it before
    /// (<see cref="NhAssistantProposalResult.Repeated"/>) or the same change waits already; fails with the
    /// platform's code, such as <c>assistant-subject-access-denied</c> for someone outside the project or
    /// <c>ai-tool-agent-not-granted</c> for a person who may not change the status themselves.
    /// </summary>
    /// <param name="user">The person's principal, as the API authenticated them.</param>
    /// <param name="userId">The person's user id.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="status">The status the person asks for.</param>
    /// <param name="reason">Why, in the person's own words; approvers read it.</param>
    /// <param name="requestId">The page's id of this request, so a request sent twice records one proposal.</param>
    /// <param name="culture">The UI culture the person asks in.</param>
    public async Task<TaskResult<NhAssistantProposalResult>> RequestAsync(
        ClaimsPrincipal user,
        Guid userId,
        Guid projectId,
        ProjectStatus status,
        string? reason,
        string requestId,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(culture);

        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(item => item.Id == projectId)
            .Select(item => new { item.Key, item.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return TaskResult<NhAssistantProposalResult>.Failed(NhAssistantErrorCodes.SubjectNotFound, "The project was not found.");
        }

        var person = await dbContext.Users
            .AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => item.UserName ?? item.Email)
            .SingleOrDefaultAsync(cancellationToken) ?? userId.ToString("D");
        var presentations = new Dictionary<string, NhAssistantApprovalPresentation>(StringComparer.OrdinalIgnoreCase);
        foreach (var presented in localization.Value.SupportedUICultures ?? [])
        {
            presentations[presented.Name] = Present(presented, project.Key, project.Name, person, status, reason);
        }

        // A host with tenants passes the tenant the person signs in with; the sample has none.
        return await proposals.ProposeForAsync(
            new NhAssistantProposalRequest(
                new NhAssistantDecidingPerson(user, userId.ToString("D"), TenantId: null) { Culture = culture.Name },
                SampleIntakeReviewerComposition.AgentId,
                ProjectAiTools.ChangeStatusToolId,
                new ProjectAiStatusChangeInput(projectId, status),
                ProjectSubjects.For(projectId))
            {
                Presentations = presentations,
                IdempotencyKey = "status-request:" + requestId
            },
            cancellationToken);
    }

    /// <summary>
    /// What approvers read in <paramref name="culture"/>: who asks for which status of which project, and why.
    /// It keeps within the platform's presentation limits, so the platform refuses nothing here.
    /// </summary>
    private static NhAssistantApprovalPresentation Present(
        CultureInfo culture,
        string projectKey,
        string projectName,
        string person,
        ProjectStatus status,
        string? reason)
    {
        var statusName = StatusTexts.GetString("ProjectStatus" + status, culture) ?? status.ToString();
        var projectValue = string.Format(culture, StatusTexts.GetString("ApprovalProjectValue", culture)!, projectKey, projectName);
        var fields = new List<NhAssistantPresentationField>
        {
            new(StatusTexts.GetString("ApprovalFieldProject", culture)!, Bound(projectValue, NhAssistantPresentationLimits.MaxFieldValueLength)),
            new(StatusTexts.GetString("ApprovalFieldNewStatus", culture)!, statusName),
            new(Texts.GetString("FieldRequestedBy", culture)!, Bound(person, NhAssistantPresentationLimits.MaxFieldValueLength))
        };
        if (!string.IsNullOrWhiteSpace(reason))
        {
            fields.Add(new NhAssistantPresentationField(Texts.GetString("FieldReason", culture)!, Bound(reason.Trim(), MaxReasonLength)));
        }

        return new NhAssistantApprovalPresentation(
            Texts.GetString("ToolName", culture)!,
            Bound(string.Format(culture, Texts.GetString("Summary", culture)!, person, projectKey, projectName, statusName), NhAssistantPresentationLimits.MaxSummaryLength),
            fields);
    }

    private static string Bound(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }
}
