using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-364 and SPM-370: the project page's status request (<see cref="SampleProjectStatusRequests"/>) as
/// the MCP tool <c>projects_request_status_v1</c> (<see cref="ProjectStatusRequestAiTools"/>). The person is
/// the MCP caller the API authenticated: the request's principal and the invocation's actor, never an id in
/// the arguments. The proposal it records waits for another approver; with
/// <c>NewHeap:AI:Assistant:Mcp:ApprovalLinkTemplate</c> (appsettings.Development.json) the platform sends an
/// MCP client that supports links to the portal's intake reviews with that proposal and returns its
/// decision on the client's retry.
/// </summary>
public static class SampleProjectStatusRequestTool
{
    public static IServiceCollection AddSampleProjectStatusRequestTool(this IServiceCollection services)
    {
        services.TryAddScoped<SampleProjectStatusRequests>();
        services.TryAddScoped<IProjectStatusRequests, McpStatusRequests>();
        services.TryAddScoped<ProjectStatusRequestAiTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectStatusRequestAiToolsNhAiCatalog>());
        return services;
    }

    /// <summary>
    /// The status request of the person whose MCP call this is.
    /// </summary>
    private sealed class McpStatusRequests(
        SampleProjectStatusRequests requests,
        IHttpContextAccessor httpContextAccessor) : IProjectStatusRequests
    {
        public async Task<TaskResult<ProjectStatusRequestReceipt>> RequestAsync(
            ProjectStatusRequestInput input,
            NhAiInvocationContext context,
            CancellationToken cancellationToken)
        {
            if (httpContextAccessor.HttpContext?.User is not { } user
                || !Guid.TryParse(context.ActorId, out var userId)
                || string.IsNullOrWhiteSpace(input.RequestId))
            {
                return TaskResult<ProjectStatusRequestReceipt>.Failed(
                    NhAssistantErrorCodes.ContextUnavailable,
                    "A status request needs the person who asks and a request id.");
            }

            var requested = await requests.RequestAsync(
                user,
                userId,
                input.ProjectId,
                input.Status,
                input.Reason,
                input.RequestId,
                CultureInfo.CurrentUICulture,
                cancellationToken);
            if (!requested.Success)
            {
                return TaskResult<ProjectStatusRequestReceipt>.Failed(requested);
            }

            var result = requested.Data!;
            return TaskResult<ProjectStatusRequestReceipt>.Succeeded(
                new ProjectStatusRequestReceipt(result.ApprovalId, OutcomeOf(result.Outcome), result.Status));
        }

        private static string OutcomeOf(NhAssistantProposeOutcome outcome)
        {
            return outcome switch
            {
                NhAssistantProposeOutcome.AlreadyOpen => "already-open",
                NhAssistantProposeOutcome.RecentlyRejected => "recently-rejected",
                NhAssistantProposeOutcome.NotNeeded => "not-needed",
                _ => "created"
            };
        }
    }
}
