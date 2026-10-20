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
        group.MapGet("subjects/{type}/{id}/agents", ListAgentsAsync)
            .WithName("NhAssistantListSubjectAgents")
            .WithSummary("List the agents that work on a subject")
            .WithDescription("Returns the agents that work on the subject (an object, or the whole application as nh-system/global or nh-system/division-{id:N}) and that the caller may use, ordered by id: what each works on includes the subject and its scope, the caller passes its required policy, and shadow variants only for administrators. Per agent whether the caller may run it now (on an object the participant role, on the whole application the agent's run policy), the thread people open, the newest run with its status, time and the first 300 characters of its result, and the number of open proposals. The caller needs a role on the subject.")
            .Produces<NhAssistantObjectAgentsDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("subjects/{type}/search", SearchAsync)
            .WithName("NhAssistantSearchSubjects")
            .WithSummary("Find subjects of one type by name")
            .WithDescription("Searches the subjects of the type through the application's INhAiSubjectSearch with query (2 to 100 characters) and returns at most limit (default 20, at most 50) that the caller may read: the resolver gives the caller at least the reader role, and the subject is in the caller's tenant. An unknown type answers 404 assistant-subject-type-unknown, a type without search 404 assistant-subject-search-unavailable; people then give the subject's id.")
            .Produces<NhAssistantSubjectSearchDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("subjects/{type}/summary", SummarizeAsync)
            .WithName("NhAssistantSummarizeSubjects")
            .WithSummary("Summarize the agent work on many subjects")
            .WithDescription("Returns, for at most 200 subjects of one type, the open proposals, the proposals the caller may decide and whether an agent works on it now, in one query. Counts follow the subjects' member snapshot like the approval inbox, and the caller's live role for the whole application (nh-system), which lists no members; subjects the caller does not see count zero.")
            .Produces<NhAssistantSubjectSummaryListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("threads/open", OpenThreadAsync)
            .WithName("NhAssistantOpenThread")
            .WithSummary("Open the conversation with an agent about a subject")
            .WithDescription("Returns the agent's thread about the subject and creates it, without starting a run, when it does not exist yet. Readers may open an existing thread; creating one needs what starting the agent by hand needs: on an object the participant role and the agent's required policy, on the whole application the agent's run policy (by default the administration policy). The agent must work on the subject and, for the whole application, its scope (409 assistant-agent-does-not-work-on otherwise).")
            .Produces<NhAssistantThreadLookupDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
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

    private static async Task<IResult> ListAgentsAsync(
        string type,
        string id,
        HttpContext httpContext,
        NhAssistantObjectAgents objectAgents)
    {
        if (!NhAiSubjectRef.IsValidType(type)
            || string.IsNullOrWhiteSpace(id)
            || id.Length > NhAiSubjectRef.MaxIdLength)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.SubjectNotFound);
        }

        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var result = await objectAgents.ListAsync(new NhAiSubjectRef(type, id), caller.Data!.ActorId, httpContext.User, httpContext.RequestAborted);
        if (!result.Success)
        {
            return Failure(result);
        }

        return TypedResults.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantObjectAgentsDto);
    }

    private static async Task<IResult> SearchAsync(
        string type,
        string? query,
        int? limit,
        HttpContext httpContext,
        NhAssistantSubjectAccessService subjects,
        NhAssistantSubjectSearchService search)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var found = await search.SearchAsync(
            type,
            query,
            limit,
            caller.Data!.ActorId,
            caller.Data.TenantId,
            httpContext.RequestAborted);
        if (!found.Success)
        {
            return FirstCode(found) == NhAssistantAdminErrorCodes.ValidationFailed
                ? NhAssistantAdminEndpoints.Failure(found)
                : Failure(found);
        }

        var subjectType = subjects.FindType(type)!;
        return TypedResults.Json(
            new NhAssistantSubjectSearchDto(
                type,
                found.Data!
                    .Select(info => new NhAssistantSubjectSearchResultDto(
                        info.Subject.Type,
                        info.Subject.Id,
                        info.DisplayName,
                        info.ScopeKey,
                        info.DivisionId)
                    {
                        Link = subjectType.LinkFor(info.Subject.Id)
                    })
                    .ToArray()),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSubjectSearchDto);
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
        NhAssistantManualStarts starts,
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
        // The agent's thread of the whole application is only for people who pass its view policy.
        var access = info is null
            ? NhAiSubjectAccess.None
            : await subjects.ResolveForAgentAsync(subject!, agentId, caller.Data!.ActorId, cancellationToken);
        if (info is null || access.Role == NhAiSubjectRole.None)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.SubjectNotFound);
        }
        if (await NhAssistantEndpointRouteBuilderExtensions.IsHiddenVariantAsync(httpContext, agentId))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.AgentNotFound);
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: false, cancellationToken);
        var runOptions = registration.AgentRuns[agentId];
        // People open the live thread; a shadow variant has shadow threads only.
        var variant = runOptions.ShadowOfAgentId is not null;
        var thread = await runs.FindThreadAsync(info.ScopeKey, agentId, subject!, cancellationToken, variant);
        if (thread is null)
        {
            // A new thread holds the conversation people start; no run and no model call happen here.
            // Whoever may start the agent by hand creates it (K9): an agent gets no new thread about
            // what it does not work on, and an agent in shadow no new live thread from anyone but an
            // administrator.
            if (await starts.RefusalAsync(agent, subject!, access, caller.Data!.ActorId, httpContext.User, cancellationToken) is { } refusal)
            {
                return refusal.Status == StatusCodes.Status409Conflict
                    ? NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status409Conflict, refusal.Code)
                    : NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
            }

            var created = await threads.GetOrCreateAsync(agent!, info, cancellationToken, variant);
            if (!created.Success)
            {
                return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, FirstCode(created));
            }

            thread = created.Data!;
        }

        await threads.ResolveAccessAsync(thread.ConversationId, caller.Data!.ActorId, null, cancellationToken);
        var canStart = await starts.CanStartAsync(agent, subject!, access, caller.Data.ActorId, httpContext.User, cancellationToken);
        return TypedResults.Json(
            new NhAssistantThreadLookupDto(
                await NhAssistantAgentRunEndpoints.ToThreadDtoAsync(thread, access.Role, runs, subjects, cancellationToken),
                canStart,
                NhAssistantAgentRunDtoMapper.RoleName(access.Role)),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantThreadLookupDto);
    }

    private static IResult Failure(NewHeap.Platform.Common.Models.TaskResult result)
    {
        var code = FirstCode(result);
        var status = code switch
        {
            NhAssistantErrorCodes.SubjectNotFound
                or NhAssistantErrorCodes.SubjectTypeUnknown
                or NhAssistantErrorCodes.SubjectSearchUnavailable => StatusCodes.Status404NotFound,
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
