using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Agent work on the application's own subjects: everything on one subject for a detail page,
/// counts for a list, and the conversation with an agent about a subject.
/// </summary>
internal static class NhAssistantSubjectEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("subjects/{type}/{id}/work", GetWorkAsync)
            .WithName("NhAssistantGetSubjectWork")
            .WithSummary("Read the agent work on a subject")
            .WithDescription("Returns the caller's role on the subject, every agent with a thread on it (and the requested agent) with its thread, active run and whether the caller may start it, and the open proposals of those threads with the caller's live decision right. The caller needs a role on the subject.")
            .Produces<NhAssistantSubjectWorkDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("subjects/{type}/summary", SummarizeAsync)
            .WithName("NhAssistantSummarizeSubjects")
            .WithSummary("Summarize the agent work on many subjects")
            .WithDescription("Returns, for at most 200 subjects of one type, the open proposals, the proposals the caller may decide and whether an agent works on it now, in one query. Counts follow the subjects' member snapshot like the approval inbox; subjects the caller does not see count zero.")
            .Produces<NhAssistantSubjectSummaryListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("threads/open", OpenThreadAsync)
            .WithName("NhAssistantOpenThread")
            .WithSummary("Open the conversation with an agent about a subject")
            .WithDescription("Returns the agent's thread about the subject and creates it, without starting a run, when it does not exist yet. Readers may open an existing thread; creating one needs the participant role and the agent's required policy.")
            .Produces<NhAssistantThreadLookupDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetWorkAsync(
        string type,
        string id,
        string? agentId,
        HttpContext httpContext,
        INhAssistantSubjectWork work)
    {
        if (!NhAiSubjectRef.IsValidType(type)
            || string.IsNullOrWhiteSpace(id)
            || id.Length > NhAiSubjectRef.MaxIdLength
            || agentId is { Length: 0 or > 128 })
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.SubjectNotFound);
        }

        var result = await work.GetForRequestAsync(httpContext, new NhAiSubjectRef(type, id), agentId);
        if (!result.Success)
        {
            return Failure(result);
        }

        return TypedResults.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSubjectWorkDto);
    }

    private static async Task<IResult> SummarizeAsync(
        string type,
        HttpContext httpContext,
        INhAssistantSubjectWork work)
    {
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSubjectSummaryRequest);
        if (request?.SubjectIds is not { Count: > 0 } subjectIds)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }

        var result = await work.SummarizeForRequestAsync(httpContext, type, subjectIds);
        if (!result.Success)
        {
            return Failure(result);
        }

        return TypedResults.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSubjectSummaryListDto);
    }

    private static async Task<IResult> OpenThreadAsync(
        HttpContext httpContext,
        NhAssistantRegistrationState registration,
        NhAssistantSubjectAccessService subjects,
        NhAssistantThreadService threads,
        NhAssistantRunStore runs,
        NhAssistantAgentAccess agentAccess,
        NhAssistantAgentCatalog agents)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantOpenThreadRequest);
        if (request is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.SubjectNotFound);
        }
        if (!NhAssistantAgentRunEndpoints.TryReadSubject(request.AgentId, request.SubjectType, request.SubjectId, registration, subjects, out var subject, out var error))
        {
            return error!;
        }

        var cancellationToken = httpContext.RequestAborted;
        var agentId = request.AgentId!;
        var info = await subjects.DescribeAsync(subject!, cancellationToken);
        var access = info is null ? NhAiSubjectAccess.None : await subjects.ResolveAsync(subject!, caller.Data!.ActorId, cancellationToken);
        if (info is null || access.Role == NhAiSubjectRole.None)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.SubjectNotFound);
        }
        if (await NhAssistantEndpointRouteBuilderExtensions.IsHiddenShadowThreadAsync(httpContext, agentId))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.AgentNotFound);
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: false, cancellationToken);
        var mayUse = agent is not null && await agentAccess.CanUseAsync(httpContext.User, agent.Definition);
        var thread = await runs.FindThreadAsync(info.ScopeKey, agentId, subject!, cancellationToken);
        if (thread is null)
        {
            // A new thread holds the conversation people start; no run and no model call happen here.
            if (!access.Has(NhAiSubjectRole.Participant) || !mayUse)
            {
                return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
            }

            var created = await threads.GetOrCreateAsync(agent!, info, cancellationToken);
            if (!created.Success)
            {
                return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, FirstCode(created));
            }

            thread = created.Data!;
        }

        await threads.ResolveAccessAsync(thread.ConversationId, caller.Data!.ActorId, null, cancellationToken);
        var canStart = mayUse
            && registration.AgentRuns[agentId].Enabled
            && access.Has(NhAiSubjectRole.Participant);
        return TypedResults.Json(
            new NhAssistantThreadLookupDto(
                await NhAssistantAgentRunEndpoints.ToThreadDtoAsync(thread, access.Role, runs, cancellationToken),
                canStart,
                NhAssistantAgentRunDtoMapper.RoleName(access.Role)),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantThreadLookupDto);
    }

    private static IResult Failure(NewHeap.Platform.Common.Models.TaskResult result)
    {
        var code = FirstCode(result);
        var status = code switch
        {
            NhAssistantErrorCodes.SubjectNotFound or NhAssistantErrorCodes.SubjectTypeUnknown => StatusCodes.Status404NotFound,
            NhAssistantErrorCodes.ContextUnavailable => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest
        };
        return NhAssistantEndpointRouteBuilderExtensions.Error(status, code);
    }

    private static string FirstCode(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? NhAssistantErrorCodes.TurnFailed;
    }
}
