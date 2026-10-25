using System.Text.Json;
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
            .WithDescription("Starts a run of the agent with the caller as initiator, by hand. On an object the caller needs at least the participant role and the agent's required policy. On the whole application (subjectType nh-system, subjectId global or division-{id:N}) the agent's run policy decides, by default the administration policy, evaluated for the caller as headless runs see them, and the caller reads it; the approve policy of the whole application gives no start. The agent must work on the subject and, for the whole application, on its scope: app-wide on global, per division on a division (409 assistant-agent-does-not-work-on otherwise). 202 names the outcome: started, queued, coalesced, superseded, ignored, deduplicated, waiting (the agent or skill is paused; reasonCode assistant-agent-paused or assistant-skill-paused) or deferred (until the caller's transaction commits). A refused start answers its code with that code's status, for example 409 assistant-trigger-rate-limited, 409 assistant-agent-stopped or 503 assistant-trigger-rolled-back; a malformed subject answers 400 assistant-subject-invalid.")
            .Produces<NhAssistantTriggerReceiptDto>(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
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
            .WithDescription("Resumes a failed or cancelled run from its last checkpoint, or restarts the agent in the same thread. The caller needs the approver role on the subject. A restart answers like POST threads/start: a refused start with its code and that code's status.")
            .Produces<NhAssistantTriggerReceiptDto>(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("approvals/inbox", GetInboxAsync)
            .WithName("NhAssistantGetApprovalInbox")
            .WithSummary("List the proposals the caller may decide")
            .WithDescription("Returns agent proposals across threads that the caller may decide, by default the pending ones sorted by expiry, paged. status=awaiting-application lists approved proposals of tools the application executes that wait for its report, sorted by report deadline, and status=execution-unknown those whose execution is uncertain until an approver resolves it, sorted by decision; both with the same visibility as pending ones. Visibility: proposals on subjects the caller approves according to the member snapshot, proposals of runs the caller started made in the run's own thread, and proposals of the whole application (nh-system) where the caller's live role is approver or the caller started the run, both only when the caller passes the agent's view policy (by default its required policy). A proposal a run placed at another object waits for that object's approvers only; its placedBy names the run. Each decision is checked live again; a proposal of the whole application the caller may not see answers 404 like an unknown one. subjectType, agentId, divisionId and runId filter the proposals. grouped=true groups them by the parent each proposal recorded from the subject access resolver when it was made, or by its own subject without one: groups come by their first expiry, a group's own subject first, then its other subjects by their first expiry, a run's proposals together; groups lists per group on the page its subjects, how many proposals it has on the page and in total, and whether it continues on the page before or after. newApprovalIds names the proposals on the page made after the caller's last visit of the inbox in its scope (global, or the division of divisionId), lastVisitedAt; before the first visit every proposal is new.")
            .Produces<NhAssistantInboxDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
        group.MapPost("approvals/inbox/visit", VisitInboxAsync)
            .WithName("NhAssistantVisitApprovalInbox")
            .WithSummary("Record a visit of the inbox")
            .WithDescription("Records that the caller looked at the approval inbox in its scope now, so the inbox marks the proposals made after this visit as new. divisionId (optional) records the visit of the inbox of one division the caller may work in; without it the visit of the whole inbox (global). Only the person, the scope and the time are stored. A division the caller may not work in, or an unknown one, answers 404 assistant-subject-not-found.")
            .Produces<NhAssistantInboxVisitDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
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
            .WithDescription("Resolves a proposal whose execution has an unknown outcome (execution-unknown): action re-execute runs it again, hand-to-application hands a proposal the application executes (one with executionReportDueAt) to the application again, and mark-resolved confirms its outcome without running it. A proposal the application executes takes hand-to-application or mark-resolved, every other one re-execute or mark-resolved; another action answers 400 assistant-approval-decision-invalid. The caller must be an approver of the proposal; a proposal in another status answers 409 assistant-execution-not-uncertain.")
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
        NhAssistantManualStarts starts,
        NhAssistantAgentCatalog agents)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        if (!TryReadSubject(agentId, subjectType, subjectId, caller.Data!.TenantId, registration, subjects, out var subject, out var error))
        {
            return error!;
        }

        var cancellationToken = httpContext.RequestAborted;
        var info = await subjects.DescribeAsync(subject!, cancellationToken);
        // The agent's thread of the whole application is only for people who pass its view policy.
        var access = info is null
            ? NhAiSubjectAccess.None
            : await subjects.ResolveForAgentAsync(subject!, agentId!, caller.Data!.ActorId, cancellationToken);
        if (info is null || access.Role == NhAiSubjectRole.None)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.SubjectNotFound);
        }

        if (await NhAssistantEndpointRouteBuilderExtensions.IsHiddenVariantAsync(httpContext, agentId!))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound);
        }

        var agent = await agents.FindAsync(agentId!, includeDisabled: false, cancellationToken);
        var runOptions = registration.AgentRuns[agentId!];
        var canStart = await starts.CanStartAsync(agent, subject!, access, caller.Data!.ActorId, httpContext.User, cancellationToken);
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
        NhAssistantManualStarts starts,
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }
        if (!TryReadSubject(request.AgentId, request.SubjectType, request.SubjectId, caller.Data!.TenantId, registration, subjects, out var subject, out var error))
        {
            return error!;
        }

        var cancellationToken = httpContext.RequestAborted;
        // A start leads to the agent's thread: on the whole application only its view policy's people read it.
        var access = await subjects.ResolveForAgentAsync(subject!, request.AgentId!, caller.Data!.ActorId, cancellationToken);
        var agent = await agents.FindAsync(request.AgentId!, includeDisabled: false, cancellationToken);
        // K9: on an object the participant role, on the whole application the agent's run policy.
        var allowed = await starts.CheckStartAsync(agent, subject!, access, caller.Data.ActorId, httpContext.User, cancellationToken);
        if (!allowed.Success)
        {
            return NhAssistantHttpErrors.Failure(allowed);
        }

        var receipt = await triggers.StartNowAsync(request.AgentId!, subject!, caller.Data.ActorId, request.Message, cancellationToken);
        return Receipt(receipt);
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        var (items, total) = await runs.ListConsultationsAsync(id, pageNumber, size, httpContext.RequestAborted);
        var routes = await runs.CountConsultationProposalsByRouteAsync(items.Select(item => item.Id).ToArray(), httpContext.RequestAborted);
        // Names come from the subject's member snapshot; someone who left the subject keeps the id.
        var thread = access.Access!.Thread;
        var names = (await runs.GetMembersAsync(thread.ScopeKey, access.Access.Subject, httpContext.RequestAborted))
            .Where(member => member.DisplayName is not null)
            .ToDictionary(member => member.ActorId, member => member.DisplayName, StringComparer.Ordinal);
        return TypedResults.Json(
            new NhAssistantConsultationListDto(
                items.Select(item => NhAssistantAgentRunDtoMapper.ToDto(item, names.GetValueOrDefault(item.AskerActorId)) with
                    {
                        ProposalsByRoute = routes.TryGetValue(item.Id, out var byRoute) ? byRoute : new Dictionary<string, int>()
                    })
                    .ToArray(),
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.ArtifactNotFound);
        }
        if (!await MayReadArtifactAsync(
                NhAssistantRestrictedResults.For(httpContext, access.Access!),
                store,
                artifact.ToolInvocationId,
                httpContext.RequestAborted))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.SubjectAccessDenied);
        }

        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(
            artifact.Content,
            artifact.ContentType,
            artifact.Name + "-v" + artifact.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static async Task<bool> MayReadArtifactAsync(
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.RunNotFound);
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.RunNotRetryable);
        }
        var run = await runs.FindRunAsync(runId, httpContext.RequestAborted);
        if (run is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.RunNotFound);
        }
        var access = await ResolveThreadAsync(httpContext, run.ConversationId, threads, NhAiSubjectRole.Approver);
        if (access.Error is not null)
        {
            return access.Error;
        }
        if (run.Status is not (NhAssistantRunStatuses.Failed or NhAssistantRunStatuses.Cancelled))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.RunNotRetryable);
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.RunNotRetryable);
        }
        var retried = await httpContext.RequestServices
            .GetRequiredService<INhBackgroundOperationService>()
            .RetryForUserAsync(operationId, userId, cancellationToken);
        if (!retried.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.RunNotRetryable);
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
        NhAssistantSystemApprovers systemApprovers,
        NhAssistantInboxVisits visits,
        string? subjectType,
        string? agentId,
        Guid? divisionId,
        Guid? runId,
        int? page,
        int? itemsPerPage,
        bool? grouped,
        string? status)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        var inboxStatus = string.IsNullOrEmpty(status) ? NhAssistantInboxStatuses.Pending : status;
        if (pageNumber < 1
            || size is < 1 or > 100
            || (subjectType is not null && !NhAiSubjectRef.IsValidType(subjectType))
            || !NhAssistantInboxStatuses.IsValid(inboxStatus))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        // The whole application lists no members: its approvers, and who passes each agent's view
        // policy, are evaluated now.
        var system = subjectType is null || subjectType == NhAiSubjectRef.SystemType
            ? await systemApprovers.InboxAsync(caller.Data!.ActorId, caller.Data.TenantId, httpContext.RequestAborted, inboxStatus)
            : NhAssistantSystemInbox.None;
        var inbox = await runs.ListInboxAsync(
            caller.Data!.ActorId,
            new NhAssistantInboxFilter(subjectType, agentId, divisionId, runId) { Status = inboxStatus },
            pageNumber,
            size,
            httpContext.RequestAborted,
            system,
            grouped ?? false);
        var proposals = await NhAssistantAgentRunDtoMapper.CompleteAsync(
            inbox.Items.Select(row => NhAssistantAgentRunDtoMapper.ToDto(row.Approval, row.Thread)).ToArray(),
            runs,
            subjects,
            httpContext.RequestAborted);

        // What is new follows the caller's last visit of the inbox in its scope, as on boards.
        var scopeKey = NhAssistantInboxVisits.ScopeKeyOf(divisionId);
        var lastVisit = await visits.LastVisitAsync(caller.Data.ActorId, scopeKey, httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantInboxDto(proposals, inbox.Total)
            {
                Status = inboxStatus,
                ScopeKey = scopeKey,
                LastVisitedAt = lastVisit,
                NewApprovalIds = inbox.Items
                    .Where(row => NhAssistantInboxVisits.IsNew(row.Approval.CreatedAt, lastVisit))
                    .Select(row => row.Approval.Id)
                    .ToArray(),
                Groups = inbox.Groups?.Select(group => ToGroupDto(group, subjects)).ToArray()
            },
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantInboxDto);
    }

    private static NhAssistantInboxGroupDto ToGroupDto(NhAssistantInboxGroupRow group, NhAssistantSubjectAccessService subjects)
    {
        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(subjects, group.SubjectType, group.SubjectId);
        return new NhAssistantInboxGroupDto(
            group.SubjectType,
            group.SubjectId,
            NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, group.SubjectType, group.SubjectId, group.DisplayName),
            group.Count,
            group.Total,
            group.ContinuesFromPreviousPage,
            group.ContinuesOnNextPage,
            group.Subjects
                .Select(subject => new NhAssistantInboxSubjectGroupDto(
                    subject.SubjectType,
                    subject.SubjectId,
                    subject.Count,
                    subject.Total,
                    subject.ContinuesFromPreviousPage,
                    subject.ContinuesOnNextPage))
                .ToArray())
        {
            SubjectTypeName = typeName,
            SubjectLink = link
        };
    }

    private static async Task<IResult> VisitInboxAsync(
        HttpContext httpContext,
        NhAssistantInboxVisits visits,
        Guid? divisionId)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var visit = await visits.VisitAsync(caller.Data!.ActorId, caller.Data.TenantId, divisionId, httpContext.RequestAborted);
        return visit.Success
            ? TypedResults.Json(visit.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantInboxVisitDto)
            : NhAssistantHttpErrors.Failure(visit);
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
            ? NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.ApprovalNotFound)
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.ApprovalDecisionInvalid);
        }

        var decided = await decisions.DecideAsync(
            approvalId,
            caller.Data!.ActorId,
            httpContext.User,
            request.Decision == "approve",
            request.ExpectedProposalHash,
            request.Reason,
            httpContext.RequestAborted,
            caller.Data.TenantId);
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
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.ApprovalDecisionInvalid);
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
                httpContext.RequestAborted,
                caller.Data.TenantId);
            results.Add(new NhAssistantDecisionOutcomeDto(
                approvalId,
                decided.Success,
                decided.Success ? null : NhAssistantResults.CodeOf(decided, NhAssistantErrorCodes.TurnFailed),
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
        if (request is null
            || !NhAssistantExecutionResolutions.TryParse(request.Action, out var resolution)
            || request.Reason is { Length: > 500 })
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.ApprovalDecisionInvalid);
        }

        var resolved = await decisions.ResolveExecutionAsync(
            approvalId,
            caller.Data!.ActorId,
            httpContext.User,
            resolution,
            request.Reason,
            httpContext.RequestAborted,
            caller.Data.TenantId);
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
                // A hidden shadow thread is checked before access, which records participants. A link
                // only names the source's thread; opening it checks the caller's tenant.
                var access = await IsHiddenAsync(httpContext, conversationId, tenantId: null)
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
    /// Whether the conversation is a shadow thread hidden from the caller, or a thread of the whole
    /// application kept from a caller bound to <paramref name="tenantId"/>, a tenant the single-tenant
    /// mode does not name.
    /// </summary>
    private static async Task<bool> IsHiddenAsync(HttpContext httpContext, Guid conversationId, string? tenantId)
    {
        var thread = await httpContext.RequestServices.GetRequiredService<NhAssistantRunStore>()
            .FindThreadByConversationAsync(conversationId, httpContext.RequestAborted);
        if (thread is null)
        {
            return false;
        }
        if (NhAssistantWholeApplicationTenancy.KeepsOut(
                httpContext.RequestServices.GetRequiredService<NhAssistantRegistrationState>(),
                thread,
                tenantId))
        {
            return true;
        }

        return await NhAssistantEndpointRouteBuilderExtensions.IsHiddenShadowThreadAsync(httpContext, thread);
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

        // A hidden shadow thread, and a thread of the whole application kept from a caller bound to a
        // tenant, is checked before access, which records participants.
        var access = await IsHiddenAsync(httpContext, conversationId, caller.Data!.TenantId)
            ? null
            : await threads.ResolveAccessAsync(conversationId, caller.Data!.ActorId, null, httpContext.RequestAborted);
        if (access is null)
        {
            return (null, null, NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.ConversationNotFound));
        }
        if (access.Role < minimum)
        {
            return (null, null, NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.SubjectAccessDenied));
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

    /// <summary>
    /// Reads the agent and subject of a request about an agent's thread. The whole application reads
    /// as unknown to a caller bound to a tenant that the single-tenant mode does not name
    /// (<see cref="NhAssistantWholeApplicationTenancy"/>).
    /// </summary>
    internal static bool TryReadSubject(
        string? agentId,
        string? subjectType,
        string? subjectId,
        string? tenantId,
        NhAssistantRegistrationState registration,
        NhAssistantSubjectAccessService subjects,
        out NhAiSubjectRef? subject,
        out IResult? error)
    {
        subject = null;
        error = null;
        if (string.IsNullOrWhiteSpace(agentId) || !registration.AgentRuns.ContainsKey(agentId))
        {
            error = NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound);
            return false;
        }
        if (!NhAiSubjectRef.IsValidType(subjectType)
            || string.IsNullOrWhiteSpace(subjectId)
            || subjectId.Length > NhAiSubjectRef.MaxIdLength
            || subjectId.Trim() != subjectId)
        {
            error = NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.SubjectInvalid);
            return false;
        }
        if (!subjects.IsKnownType(subjectType!))
        {
            error = NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.SubjectTypeUnknown);
            return false;
        }

        var read = new NhAiSubjectRef(subjectType!, subjectId);
        if (NhAssistantWholeApplicationTenancy.KeepsOut(registration, read, tenantId))
        {
            error = NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.SubjectNotFound);
            return false;
        }

        subject = read;
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
            return NhAssistantHttpErrors.Failure(result);
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

    /// <summary>
    /// The answer to a start by hand: <c>202</c> with the receipt for every outcome that leads to work
    /// (started, queued, coalesced, superseded, ignored, deduplicated, waiting while the agent or skill
    /// is paused, or deferred until the transaction commits), and a refusal as the error of its reason
    /// code with that code's status.
    /// </summary>
    internal static IResult Receipt(NhAssistantTriggerReceipt receipt)
    {
        if (receipt.Outcome == NhAssistantTriggerOutcomes.Rejected)
        {
            return NhAssistantHttpErrors.Error(receipt.ReasonCode ?? NhAssistantErrorCodes.TurnFailed);
        }

        return TypedResults.Json(
            new NhAssistantTriggerReceiptDto(receipt.TriggerId, receipt.Outcome, receipt.RunId, receipt.ConversationId, receipt.ReasonCode),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantTriggerReceiptDto,
            statusCode: StatusCodes.Status202Accepted);
    }
}
