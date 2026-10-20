using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The HTTP API of agent runs: threads per subject, starting an agent, runs with cancel and retry,
/// the approval inbox and proposal decisions. Access follows the subject access resolver on every
/// request.
/// </summary>
internal static class NhAssistantAgentRunEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("threads", GetThreadAsync)
            .WithName("NhAssistantGetThread")
            .WithSummary("Find an agent thread")
            .WithDescription("Returns the thread of an agent about a subject and whether the caller may start the agent. The caller needs a role on the subject.")
            .Produces<NhAssistantThreadLookupDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("threads/start", StartAgentAsync)
            .WithName("NhAssistantStartAgent")
            .WithSummary("Start an agent on a subject")
            .WithDescription("Starts a run of the agent with the caller as initiator. The caller needs at least the participant role on the subject, and the agent must work on the subject's type (409 assistant-agent-does-not-work-on otherwise).")
            .Produces<NhAssistantTriggerReceiptDto>(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        group.MapGet("threads/{id:guid}/runs", ListRunsAsync)
            .WithName("NhAssistantListRuns")
            .WithSummary("List the runs of a thread")
            .WithDescription("Returns the runs of an agent thread, newest first, with status, failure class, usage and proposal count.")
            .Produces<NhAssistantRunListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("threads/{id:guid}/consultations", ListConsultationsAsync)
            .WithName("NhAssistantListThreadConsultations")
            .WithSummary("List the consultations of a thread")
            .WithDescription("Returns who consulted the thread's agent through which assistant, newest first, with outcome, usage and proposal count. Questions and answers stay in the asking conversations. The caller needs a role on the subject.")
            .Produces<NhAssistantConsultationListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("threads/{id:guid}/proposals", ListThreadProposalsAsync)
            .WithName("NhAssistantListThreadProposals")
            .WithSummary("List the proposals of a thread")
            .WithDescription("Returns the proposals of an agent thread, newest first, with their decision and execution state.")
            .Produces<NhAssistantProposalDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("threads/{id:guid}/memory", GetMemoryAsync)
            .WithName("NhAssistantGetThreadMemory")
            .WithSummary("Read the working memory of a thread")
            .WithDescription("Returns the agent's working memory about the subject of the thread, its schema version and when it last changed. The caller needs a role on the subject.")
            .Produces<NhAssistantThreadMemoryDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("threads/{id:guid}/artifacts", ListArtifactsAsync)
            .WithName("NhAssistantListThreadArtifacts")
            .WithSummary("List the artifacts of a thread")
            .WithDescription("Returns every version of the files the agent's tools saved in the thread, newest first, without content. Artifacts of the agent's own grant that the caller may not see are marked restricted.")
            .Produces<NhAssistantArtifactDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("threads/{id:guid}/artifacts/{artifactId:guid}/content", GetArtifactContentAsync)
            .WithName("NhAssistantGetThreadArtifactContent")
            .WithSummary("Download an artifact")
            .WithDescription("Returns the content of one artifact version. The caller needs a role on the subject and, for an artifact of the agent's own grant, the tool's policies.")
            .Produces(StatusCodes.Status200OK)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("runs/{runId:guid}/cancel", CancelRunAsync)
            .WithName("NhAssistantCancelRun")
            .WithSummary("Cancel an agent run")
            .WithDescription("Cancels a queued, running or waiting run at its next safe point and withdraws its open proposals. The caller needs at least the participant role on the subject.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("runs/{runId:guid}/retry", RetryRunAsync)
            .WithName("NhAssistantRetryRun")
            .WithSummary("Retry an agent run")
            .WithDescription("Resumes a failed or cancelled run from its last checkpoint, or restarts the agent in the same thread. The caller needs the approver role on the subject.")
            .Produces<NhAssistantTriggerReceiptDto>(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("approvals/inbox", GetInboxAsync)
            .WithName("NhAssistantGetApprovalInbox")
            .WithSummary("List the proposals the caller may decide")
            .WithDescription("Returns pending agent proposals across threads that the caller may decide, sorted by expiry and paged. Each decision is checked live again.")
            .Produces<NhAssistantInboxDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
        group.MapGet("approvals/{approvalId:guid}", GetProposalAsync)
            .WithName("NhAssistantGetProposal")
            .WithSummary("Read one agent proposal")
            .WithDescription("Returns one proposal of an agent thread in any status, with the caller's live right to decide it. The caller needs a role on the subject; a proposal the caller may not see answers 404 like an unknown one. Reading does not join the thread.")
            .Produces<NhAssistantSubjectProposalDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("approvals/lookup", LookupProposalsAsync)
            .WithName("NhAssistantLookupProposals")
            .WithSummary("Read several agent proposals")
            .WithDescription("Returns the proposals among at most 100 ids that the caller may see, in the order of the request, each with the caller's live right to decide it. Ids the caller may not see, or that do not exist, are left out.")
            .Produces<NhAssistantProposalLookupDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
        group.MapPost("approvals/{approvalId:guid}/decisions", DecideAsync)
            .WithName("NhAssistantDecideProposal")
            .WithSummary("Decide an agent proposal")
            .WithDescription("Approves or rejects a pending proposal of an agent thread. The caller must satisfy the proposal's approval policy and the tool's approve policy.")
            .Produces<NhAssistantProposalDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        group.MapPost("approvals/decisions", DecideManyAsync)
            .WithName("NhAssistantDecideProposals")
            .WithSummary("Decide several agent proposals")
            .WithDescription("Approves or rejects several pending proposals; each one is checked and decided on its own.")
            .Produces<NhAssistantBulkDecisionDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
        group.MapPost("approvals/{approvalId:guid}/resolve", ResolveAsync)
            .WithName("NhAssistantResolveExecution")
            .WithSummary("Resolve an uncertain execution")
            .WithDescription("Re-executes a proposal whose execution was interrupted, or confirms its outcome without running it. The caller must be an approver of the proposal.")
            .Produces<NhAssistantProposalDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetThreadAsync(
        HttpContext httpContext,
        string? agentId,
        string? subjectType,
        string? subjectId,
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
        if (!TryReadSubject(agentId, subjectType, subjectId, registration, subjects, out var subject, out var error))
        {
            return error!;
        }

        var cancellationToken = httpContext.RequestAborted;
        var info = await subjects.DescribeAsync(subject!, cancellationToken);
        var access = info is null ? NhAiSubjectAccess.None : await subjects.ResolveAsync(subject!, caller.Data!.ActorId, cancellationToken);
        if (info is null || access.Role == NhAiSubjectRole.None)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.SubjectNotFound);
        }

        if (await NhAssistantEndpointRouteBuilderExtensions.IsHiddenVariantAsync(httpContext, agentId!))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.AgentNotFound);
        }

        var agent = await agents.FindAsync(agentId!, includeDisabled: false, cancellationToken);
        var runOptions = registration.AgentRuns[agentId!];
        var canStart = agent is not null
            && runOptions.Enabled
            && access.Has(NhAiSubjectRole.Participant)
            && await agentAccess.CanUseAsync(httpContext.User, agent.Definition)
            && await MayStartAsync(httpContext, runOptions, agent, subject!);
        // People open the live thread; a shadow variant has shadow threads only.
        var thread = await runs.FindThreadAsync(info.ScopeKey, agentId!, subject!, cancellationToken, runOptions.ShadowOfAgentId is not null);
        NhAssistantThreadDto? threadDto = null;
        if (thread is not null)
        {
            await threads.ResolveAccessAsync(thread.ConversationId, caller.Data!.ActorId, null, cancellationToken);
            threadDto = await ToThreadDtoAsync(thread, access.Role, runs, subjects, cancellationToken);
        }

        return TypedResults.Json(
            new NhAssistantThreadLookupDto(threadDto, canStart, NhAssistantAgentRunDtoMapper.RoleName(access.Role)),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantThreadLookupDto);
    }

    private static async Task<IResult> StartAgentAsync(
        HttpContext httpContext,
        NhAssistantRegistrationState registration,
        NhAssistantSubjectAccessService subjects,
        NhAssistantAgentAccess agentAccess,
        NhAssistantAgentCatalog agents,
        NhAssistantAgentTriggers triggers)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantStartAgentRequest);
        if (request is null || request.Message is { Length: > 8_000 })
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }
        if (!TryReadSubject(request.AgentId, request.SubjectType, request.SubjectId, registration, subjects, out var subject, out var error))
        {
            return error!;
        }

        var cancellationToken = httpContext.RequestAborted;
        var access = await subjects.ResolveAsync(subject!, caller.Data!.ActorId, cancellationToken);
        if (!access.Has(NhAiSubjectRole.Participant))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        }
        var agent = await agents.FindAsync(request.AgentId!, includeDisabled: false, cancellationToken);
        if (agent is null || !await agentAccess.CanUseAsync(httpContext.User, agent.Definition))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.AgentForbidden);
        }
        // An agent starts only on what it works on, by hand as from a trigger.
        if (httpContext.RequestServices.GetRequiredService<NhAssistantWorksOnRule>().RefusalOf(agent.Id, subject!) is { } notWorkedOn)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status409Conflict, notWorkedOn);
        }
        if (!await MayStartAsync(httpContext, registration.AgentRuns[request.AgentId!], agent, subject!))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.AgentForbidden);
        }

        var receipt = await triggers.StartNowAsync(request.AgentId!, subject!, caller.Data.ActorId, request.Message, cancellationToken);
        return Receipt(receipt);
    }

    /// <summary>
    /// Whether the caller may start the agent by hand on <paramref name="subject"/>: the agent works
    /// on it, an agent in shadow works in shadow threads, which only administrators see, so only they
    /// start it, and for the whole application the caller passes the agent's run policy when it names
    /// one, next to the role on the whole application.
    /// </summary>
    internal static async Task<bool> MayStartAsync(
        HttpContext httpContext,
        NhAssistantAgentRunOptions runOptions,
        NhAssistantAgent agent,
        NhAiSubjectRef subject)
    {
        if (!httpContext.RequestServices.GetRequiredService<NhAssistantWorksOnRule>().Allows(agent.Id, subject))
        {
            return false;
        }
        if (NhAiSystemSubjects.IsSystem(subject)
            && runOptions.WorksOn.RunPolicy is { } runPolicy
            && !(await httpContext.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(httpContext.User, runPolicy)).Succeeded)
        {
            return false;
        }

        return !NhAssistantShadowModes.IsAgentShadow(runOptions, agent.ShadowOverride)
            || await NhAssistantEndpointRouteBuilderExtensions.IsAdministratorAsync(httpContext);
    }

    private static async Task<IResult> ListRunsAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantRunStore runs,
        NhAssistantHandOffStore handOffs,
        int? page,
        int? itemsPerPage)
    {
        var access = await ResolveThreadAsync(httpContext, id, threads, NhAiSubjectRole.Reader);
        if (access.Error is not null)
        {
            return access.Error;
        }
        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        if (pageNumber < 1 || size is < 1 or > 100)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }

        var (items, total) = await runs.ListRunsAsync(id, pageNumber, size, httpContext.RequestAborted);
        var links = await HandOffLinksAsync(httpContext, items, access.ActorId!, threads, handOffs);
        return TypedResults.Json(
            new NhAssistantRunListDto(
                items
                    .Select(run => NhAssistantAgentRunDtoMapper.ToDto(run) with
                    {
                        RequestedByConversationId = run.RequestedByRunId is { } source && links.TryGetValue(source, out var link) ? link : null
                    })
                    .ToArray(),
                total),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantRunListDto);
    }

    private static async Task<IResult> ListConsultationsAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantRunStore runs,
        int? page,
        int? itemsPerPage)
    {
        var access = await ResolveThreadAsync(httpContext, id, threads, NhAiSubjectRole.Reader);
        if (access.Error is not null)
        {
            return access.Error;
        }
        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        if (pageNumber < 1 || size is < 1 or > 100)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }

        var (items, total) = await runs.ListConsultationsAsync(id, pageNumber, size, httpContext.RequestAborted);
        // Names come from the subject's member snapshot; someone who left the subject keeps the id.
        var thread = access.Access!.Thread;
        var names = (await runs.GetMembersAsync(thread.ScopeKey, access.Access.Subject, httpContext.RequestAborted))
            .Where(member => member.DisplayName is not null)
            .ToDictionary(member => member.ActorId, member => member.DisplayName, StringComparer.Ordinal);
        return TypedResults.Json(
            new NhAssistantConsultationListDto(
                items.Select(item => NhAssistantAgentRunDtoMapper.ToDto(item, names.GetValueOrDefault(item.AskerActorId))).ToArray(),
                total),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantConsultationListDto);
    }

    private static async Task<IResult> ListThreadProposalsAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantRunStore runs,
        NhAssistantSubjectAccessService subjects,
        NhAssistantProposalFeedbackStore feedback)
    {
        var access = await ResolveThreadAsync(httpContext, id, threads, NhAiSubjectRole.Reader);
        if (access.Error is not null)
        {
            return access.Error;
        }

        var approvals = await runs.GetThreadApprovalsAsync(id, 200, httpContext.RequestAborted);
        var thread = access.Access!.Thread;
        var actorId = access.ActorId!;
        var role = access.Access.Role;
        // Each person sees their own feedback; administrators read everyone's in the report.
        var ownFeedback = await feedback.FindByAuthorAsync(
            approvals.Select(approval => approval.Id).ToArray(),
            actorId,
            httpContext.RequestAborted);
        var proposals = await NhAssistantAgentRunDtoMapper.CompleteAsync(
            approvals
                .OrderByDescending(approval => approval.CreatedAt)
                .Select(approval => NhAssistantAgentRunDtoMapper.ToDto(approval, thread) with
                {
                    Feedback = ownFeedback.TryGetValue(approval.Id, out var given)
                        ? NhAssistantAgentRunDtoMapper.ToDto(given)
                        : null,
                    CanGiveFeedback = NhAssistantProposalFeedbackService.MayGiveFeedback(approval, role, actorId)
                })
                .ToArray(),
            runs,
            subjects,
            httpContext.RequestAborted);
        return TypedResults.Json(
            proposals.ToArray(),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalDtoArray);
    }

    private static async Task<IResult> GetMemoryAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantRegistrationState registration,
        NhAssistantMemoryService memory)
    {
        var access = await ResolveThreadAsync(httpContext, id, threads, NhAiSubjectRole.Reader);
        if (access.Error is not null)
        {
            return access.Error;
        }

        var configured = registration.AgentRuns.TryGetValue(access.Access!.Thread.AgentId, out var options)
            && options.MemoryType is not null;
        var snapshot = await memory.GetAsync(id, httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantThreadMemoryDto(
                configured,
                snapshot.SchemaVersion,
                snapshot.Json is null ? null : JsonDocument.Parse(snapshot.Json).RootElement.Clone(),
                snapshot.UpdatedAt),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantThreadMemoryDto);
    }

    private static async Task<IResult> ListArtifactsAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantArtifactStore artifacts,
        INhAssistantStore store)
    {
        var access = await ResolveThreadAsync(httpContext, id, threads, NhAiSubjectRole.Reader);
        if (access.Error is not null)
        {
            return access.Error;
        }

        var shield = NhAssistantRestrictedResults.For(httpContext, access.Access!);
        var rows = await artifacts.ListAsync(id, 200, httpContext.RequestAborted);
        var items = new List<NhAssistantArtifactDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(new NhAssistantArtifactDto(
                row.Id,
                row.Name,
                row.Version,
                row.ContentType,
                row.SizeBytes,
                row.CreatedAt,
                row.RunId,
                !await MayReadArtifactAsync(shield, store, row.ToolInvocationId, httpContext.RequestAborted)));
        }

        return TypedResults.Json(items.ToArray(), NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantArtifactDtoArray);
    }

    private static async Task<IResult> GetArtifactContentAsync(
        Guid id,
        Guid artifactId,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantArtifactStore artifacts,
        INhAssistantStore store)
    {
        var access = await ResolveThreadAsync(httpContext, id, threads, NhAiSubjectRole.Reader);
        if (access.Error is not null)
        {
            return access.Error;
        }

        var artifact = await artifacts.FindAsync(id, artifactId, httpContext.RequestAborted);
        if (artifact is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ArtifactNotFound);
        }
        if (!await MayReadArtifactAsync(
                NhAssistantRestrictedResults.For(httpContext, access.Access!),
                store,
                artifact.ToolInvocationId,
                httpContext.RequestAborted))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        }

        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(
            artifact.Content,
            artifact.ContentType,
            artifact.Name + "-v" + artifact.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<bool> MayReadArtifactAsync(
        NhAssistantRestrictedResults shield,
        INhAssistantStore store,
        Guid? toolInvocationId,
        CancellationToken cancellationToken)
    {
        if (toolInvocationId is not { } invocationId
            || await store.FindToolInvocationAsync(invocationId, cancellationToken) is not { } invocation)
        {
            return true;
        }

        return await shield.MayViewAsync(invocation.GrantSource, invocation.ToolId, invocation.ReadForActorId);
    }

    private static async Task<IResult> CancelRunAsync(
        Guid runId,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantRunStore runs,
        NhAssistantRegistrationState registration)
    {
        var run = await runs.FindRunAsync(runId, httpContext.RequestAborted);
        if (run is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.RunNotFound);
        }
        var access = await ResolveThreadAsync(httpContext, run.ConversationId, threads, NhAiSubjectRole.Participant);
        if (access.Error is not null)
        {
            return access.Error;
        }
        if (NhAssistantRunStatuses.IsTerminal(run.Status))
        {
            return TypedResults.Accepted((string?)null);
        }

        // The participant role was checked above, so the run's owner cancels its operation.
        var thread = access.Access!.Thread;
        var owner = NhAssistantAgentTriggers.OwnerActor(thread, run.AccountableOwnerId);
        await httpContext.RequestServices
            .GetRequiredService<NhAssistantRunCancellation>()
            .CancelAsync(run, access.ActorId!, owner, httpContext.RequestAborted);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<IResult> RetryRunAsync(
        Guid runId,
        HttpContext httpContext,
        NhAssistantThreadService threads,
        NhAssistantRunStore runs,
        NhAssistantAgentTriggers triggers)
    {
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantRetryRunRequest);
        var mode = request?.Mode ?? "resume";
        if (mode is not ("resume" or "restart"))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.RunNotRetryable);
        }
        var run = await runs.FindRunAsync(runId, httpContext.RequestAborted);
        if (run is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.RunNotFound);
        }
        var access = await ResolveThreadAsync(httpContext, run.ConversationId, threads, NhAiSubjectRole.Approver);
        if (access.Error is not null)
        {
            return access.Error;
        }
        if (run.Status is not (NhAssistantRunStatuses.Failed or NhAssistantRunStatuses.Cancelled))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.RunNotRetryable);
        }

        var cancellationToken = httpContext.RequestAborted;
        var thread = access.Access!.Thread;
        if (mode == "restart")
        {
            var restarted = await triggers.StartNowAsync(
                thread.AgentId,
                new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                access.ActorId!,
                "Restart: continue the work of the previous run.",
                cancellationToken);
            return Receipt(restarted);
        }

        if (run.BackgroundOperationId is not { } operationId || !Guid.TryParse(access.ActorId, out var userId))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.RunNotRetryable);
        }
        var retried = await httpContext.RequestServices
            .GetRequiredService<INhBackgroundOperationService>()
            .RetryForUserAsync(operationId, userId, cancellationToken);
        if (!retried.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.RunNotRetryable);
        }

        await runs.TrySetRunStatusAsync(
            run.Id,
            [NhAssistantRunStatuses.Failed, NhAssistantRunStatuses.Cancelled],
            NhAssistantRunStatuses.Queued,
            cancellationToken);
        return Receipt(new NhAssistantTriggerReceipt(run.TriggerId, NhAssistantTriggerOutcomes.Queued, run.Id, run.ConversationId, null));
    }

    private static async Task<IResult> GetInboxAsync(
        HttpContext httpContext,
        NhAssistantRunStore runs,
        NhAssistantSubjectAccessService subjects,
        string? subjectType,
        string? agentId,
        Guid? divisionId,
        Guid? runId,
        int? page,
        int? itemsPerPage)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        if (pageNumber < 1 || size is < 1 or > 100 || (subjectType is not null && !NhAiSubjectRef.IsValidType(subjectType)))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }

        var (items, total) = await runs.ListInboxAsync(
            caller.Data!.ActorId,
            new NhAssistantInboxFilter(subjectType, agentId, divisionId, runId),
            pageNumber,
            size,
            httpContext.RequestAborted);
        var proposals = await NhAssistantAgentRunDtoMapper.CompleteAsync(
            items.Select(row => NhAssistantAgentRunDtoMapper.ToDto(row.Approval, row.Thread)).ToArray(),
            runs,
            subjects,
            httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantInboxDto(proposals, total),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantInboxDto);
    }

    private static async Task<IResult> GetProposalAsync(
        Guid approvalId,
        HttpContext httpContext,
        NhAssistantProposalReads reads)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var read = await reads.ReadAsync(
            [approvalId],
            caller.Data!.ActorId,
            caller.Data.TenantId,
            httpContext.User,
            thread => NhAssistantEndpointRouteBuilderExtensions.IsHiddenShadowThreadAsync(httpContext, thread),
            httpContext.RequestAborted);
        // A proposal the caller may not see answers like one that does not exist.
        return read.Count == 0
            ? NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ApprovalNotFound)
            : TypedResults.Json(read[0], NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSubjectProposalDto);
    }

    private static async Task<IResult> LookupProposalsAsync(
        HttpContext httpContext,
        NhAssistantProposalReads reads)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalLookupRequest);
        var ids = request?.ApprovalIds?.Distinct().ToArray();
        if (ids is null || ids.Length is 0 or > NhAssistantProposalLookupRequest.MaxApprovalIds)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }

        var read = await reads.ReadAsync(
            ids,
            caller.Data!.ActorId,
            caller.Data.TenantId,
            httpContext.User,
            thread => NhAssistantEndpointRouteBuilderExtensions.IsHiddenShadowThreadAsync(httpContext, thread),
            httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantProposalLookupDto(read),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalLookupDto);
    }

    private static async Task<IResult> DecideAsync(
        Guid approvalId,
        HttpContext httpContext,
        NhAssistantProposalDecisions decisions,
        NhAssistantRunStore runs,
        NhAssistantSubjectAccessService subjects)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalDecisionRequest);
        if (request is null || request.Decision is not ("approve" or "reject") || request.Reason is { Length: > 500 })
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.ApprovalDecisionInvalid);
        }

        var decided = await decisions.DecideAsync(
            approvalId,
            caller.Data!.ActorId,
            httpContext.User,
            request.Decision == "approve",
            request.ExpectedProposalHash,
            request.Reason,
            httpContext.RequestAborted);
        return await ProposalResultAsync(decided, caller.Data.ActorId, runs, subjects, httpContext.RequestAborted);
    }

    private static async Task<IResult> DecideManyAsync(
        HttpContext httpContext,
        NhAssistantProposalDecisions decisions)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantBulkDecisionRequest);
        if (request?.ApprovalIds is not { Count: > 0 and <= 50 } approvalIds
            || request.Decision is not ("approve" or "reject")
            || request.Reason is { Length: > 500 })
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.ApprovalDecisionInvalid);
        }

        var results = new List<NhAssistantDecisionOutcomeDto>(approvalIds.Count);
        foreach (var approvalId in approvalIds.Distinct())
        {
            var decided = await decisions.DecideAsync(
                approvalId,
                caller.Data!.ActorId,
                httpContext.User,
                request.Decision == "approve",
                null,
                request.Reason,
                httpContext.RequestAborted);
            results.Add(new NhAssistantDecisionOutcomeDto(
                approvalId,
                decided.Success,
                decided.Success ? null : FirstCode(decided),
                decided.Data?.Status));
        }

        return TypedResults.Json(
            new NhAssistantBulkDecisionDto(results),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantBulkDecisionDto);
    }

    private static async Task<IResult> ResolveAsync(
        Guid approvalId,
        HttpContext httpContext,
        NhAssistantProposalDecisions decisions,
        NhAssistantRunStore runs,
        NhAssistantSubjectAccessService subjects)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantResolveExecutionRequest);
        if (request is null || request.Action is not ("re-execute" or "mark-resolved") || request.Reason is { Length: > 500 })
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.ApprovalDecisionInvalid);
        }

        var resolved = await decisions.ResolveExecutionAsync(
            approvalId,
            caller.Data!.ActorId,
            httpContext.User,
            request.Action == "re-execute",
            request.Reason,
            httpContext.RequestAborted);
        return await ProposalResultAsync(resolved, caller.Data.ActorId, runs, subjects, httpContext.RequestAborted);
    }

    /// <summary>
    /// The threads of the runs that handed work to these runs, by source run, that the caller may
    /// open: as a reader of their subject, and not a hidden shadow thread.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, Guid>> HandOffLinksAsync(
        HttpContext httpContext,
        IReadOnlyList<Entities.AssistantRun> items,
        string actorId,
        NhAssistantThreadService threads,
        NhAssistantHandOffStore handOffs)
    {
        var sourceRuns = items
            .Select(run => run.RequestedByRunId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var conversations = await handOffs.GetSourceConversationsAsync(sourceRuns, httpContext.RequestAborted);
        var links = new Dictionary<Guid, Guid>();
        var readable = new Dictionary<Guid, bool>();
        foreach (var (sourceRun, conversationId) in conversations)
        {
            if (!readable.TryGetValue(conversationId, out var may))
            {
                // A hidden shadow thread is checked before access, which records participants.
                var access = await IsHiddenAsync(httpContext, conversationId)
                    ? null
                    : await threads.ResolveAccessAsync(conversationId, actorId, null, httpContext.RequestAborted);
                may = access is not null && access.Role >= NhAiSubjectRole.Reader;
                readable[conversationId] = may;
            }
            if (may)
            {
                links[sourceRun] = conversationId;
            }
        }

        return links;
    }

    /// <summary>
    /// Whether the conversation is a shadow thread hidden from the caller.
    /// </summary>
    private static async Task<bool> IsHiddenAsync(HttpContext httpContext, Guid conversationId)
    {
        var thread = await httpContext.RequestServices.GetRequiredService<NhAssistantRunStore>()
            .FindThreadByConversationAsync(conversationId, httpContext.RequestAborted);
        return thread is not null && await NhAssistantEndpointRouteBuilderExtensions.IsHiddenShadowThreadAsync(httpContext, thread);
    }

    /// <summary>
    /// Resolves the caller's live role on a thread and requires <paramref name="minimum"/>.
    /// </summary>
    internal static async Task<(NhAssistantThreadAccess? Access, string? ActorId, IResult? Error)> ResolveThreadAsync(
        HttpContext httpContext,
        Guid conversationId,
        NhAssistantThreadService threads,
        NhAiSubjectRole minimum)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return (null, null, NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable());
        }

        // A hidden shadow thread is checked before access, which records participants.
        var access = await IsHiddenAsync(httpContext, conversationId)
            ? null
            : await threads.ResolveAccessAsync(conversationId, caller.Data!.ActorId, null, httpContext.RequestAborted);
        if (access is null)
        {
            return (null, null, NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ConversationNotFound));
        }
        if (access.Role < minimum)
        {
            return (null, null, NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.SubjectAccessDenied));
        }

        return (access, caller.Data.ActorId, null);
    }

    /// <summary>
    /// The thread as the caller sees it, with its active run and waiting messages.
    /// </summary>
    internal static async Task<NhAssistantThreadDto> ToThreadDtoAsync(
        Entities.AssistantThread thread,
        NhAiSubjectRole role,
        NhAssistantRunStore runs,
        NhAssistantSubjectAccessService subjects,
        CancellationToken cancellationToken)
    {
        var active = await runs.FindActiveRunAsync(thread.ConversationId, cancellationToken);
        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(subjects, thread.SubjectType, thread.SubjectId);
        return new NhAssistantThreadDto(
            thread.ConversationId,
            thread.AgentId,
            thread.SubjectType,
            thread.SubjectId,
            NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, thread.SubjectType, thread.SubjectId, thread.SubjectDisplayName),
            thread.Accountability,
            thread.AccountableOwnerId,
            NhAssistantAgentRunDtoMapper.RoleName(role),
            active is null ? null : NhAssistantAgentRunDtoMapper.ToDto(active),
            await runs.CountWaitingInputsAsync(thread.ConversationId, cancellationToken))
        {
            Shadow = thread.Shadow,
            SubjectTypeName = typeName,
            SubjectLink = link
        };
    }

    internal static bool TryReadSubject(
        string? agentId,
        string? subjectType,
        string? subjectId,
        NhAssistantRegistrationState registration,
        NhAssistantSubjectAccessService subjects,
        out NhAiSubjectRef? subject,
        out IResult? error)
    {
        subject = null;
        error = null;
        if (string.IsNullOrWhiteSpace(agentId) || !registration.AgentRuns.ContainsKey(agentId))
        {
            error = NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.AgentNotFound);
            return false;
        }
        if (!NhAiSubjectRef.IsValidType(subjectType)
            || string.IsNullOrWhiteSpace(subjectId)
            || subjectId.Length > NhAiSubjectRef.MaxIdLength
            || subjectId.Trim() != subjectId)
        {
            error = NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.SubjectNotFound);
            return false;
        }
        if (!subjects.IsKnownType(subjectType!))
        {
            error = NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.SubjectTypeUnknown);
            return false;
        }

        subject = new NhAiSubjectRef(subjectType!, subjectId);
        return true;
    }

    /// <summary>
    /// The decided proposal as its decider sees it: after a rejection they may say what was wrong.
    /// </summary>
    private static async Task<IResult> ProposalResultAsync(
        NewHeap.Platform.Common.Models.TaskResult<Entities.AssistantApproval> result,
        string actorId,
        NhAssistantRunStore runs,
        NhAssistantSubjectAccessService subjects,
        CancellationToken cancellationToken)
    {
        if (!result.Success)
        {
            var code = FirstCode(result);
            var status = code switch
            {
                NhAssistantErrorCodes.ApprovalNotFound => StatusCodes.Status404NotFound,
                NhAssistantErrorCodes.ApprovalNotPending
                    or NhAssistantErrorCodes.ProposalHashMismatch
                    or NhAssistantErrorCodes.ProposalStale
                    or NhAssistantErrorCodes.ExecutionNotUncertain => StatusCodes.Status409Conflict,
                NhAssistantErrorCodes.ProposalCheckFailed => StatusCodes.Status503ServiceUnavailable,
                NhAssistantErrorCodes.ApprovalPolicyDenied
                    or NhAssistantErrorCodes.FourEyesRequired => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest
            };
            return NhAssistantEndpointRouteBuilderExtensions.Error(status, code);
        }

        var approval = result.Data!;
        var thread = await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        var completed = await NhAssistantAgentRunDtoMapper.CompleteAsync(
            [
                NhAssistantAgentRunDtoMapper.ToDto(approval, thread!) with
                {
                    CanGiveFeedback = NhAssistantApprovalStatuses.AcceptsFeedback(approval.Status)
                        && string.Equals(approval.DecidedByActorId, actorId, StringComparison.Ordinal)
                }
            ],
            runs,
            subjects,
            cancellationToken);
        return TypedResults.Json(
            completed[0],
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalDto);
    }

    private static IResult Receipt(NhAssistantTriggerReceipt receipt)
    {
        var dto = new NhAssistantTriggerReceiptDto(receipt.TriggerId, receipt.Outcome, receipt.RunId, receipt.ConversationId, receipt.ReasonCode);
        if (receipt.Outcome == NhAssistantTriggerOutcomes.Rejected)
        {
            var status = receipt.ReasonCode == NhAssistantTriggerReasons.RateLimited
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return NhAssistantEndpointRouteBuilderExtensions.Error(status, receipt.ReasonCode ?? NhAssistantErrorCodes.TurnFailed);
        }

        return TypedResults.Json(
            dto,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantTriggerReceiptDto,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static string FirstCode(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? NhAssistantErrorCodes.TurnFailed;
    }
}
