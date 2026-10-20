using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The person who reads a run history: their actor, their tenant, the request's principal and
/// whether they administer the assistant.
/// </summary>
internal sealed record NhAssistantRunReader(string ActorId, string? TenantId, ClaimsPrincipal User, bool Administrator);

/// <summary>
/// A page of a run history: the filter, the page from 1 and the runs per page.
/// </summary>
internal sealed record NhAssistantRunQuery(NhAssistantRunFilter Filter, int Page, int ItemsPerPage);

/// <summary>
/// The results and history of agent runs (K13, K14). People see the runs they may see: runs on an
/// object whoever may read the object (its resolver's reader role, or an invitation to the agent's
/// thread there, as the thread itself), runs for the whole application whoever passes the agent's view
/// policy, by default its required policy, evaluated as the roles of the whole application are. Shadow
/// work is for administrators only, and a thread of another tenant stays invisible. Administrators see
/// every run of an agent in its runs view, and open any run.
/// </summary>
/// <remarks>
/// Visibility is decided per thread, by the application's resolver, after the query. People's history
/// therefore considers the <see cref="MaxThreads"/> threads with the most recent matching runs, decides
/// each once, and pages the runs of the visible ones in the database; a narrower filter reaches older
/// threads. Each subject is resolved once per request.
/// </remarks>
internal sealed class NhAssistantRunHistory(
    NhAssistantRegistrationState registration,
    NhAssistantRunStore runs,
    NhAssistantThreadService threads,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentCatalog agents,
    NhAssistantArtifactStore artifacts,
    INhAssistantStore store,
    IEnumerable<INhAiSubjectAccessResolver> resolvers)
{
    // The reader's visibility decides with these per request.
    private readonly NhAssistantRegistrationState _registration = registration;
    private readonly NhAssistantThreadService _threads = threads;
    private readonly NhAssistantSubjectAccessService _subjects = subjects;
    private readonly NhAssistantAgentCatalog _agents = agents;
    private readonly IEnumerable<INhAiSubjectAccessResolver> _resolvers = resolvers;

    /// <summary>
    /// The most agent threads one page of people's history considers.
    /// </summary>
    internal const int MaxThreads = 100;

    /// <summary>
    /// The most runs one page lists.
    /// </summary>
    internal const int MaxItemsPerPage = 50;

    /// <summary>
    /// The most files a run's result lists.
    /// </summary>
    private const int MaxArtifacts = 50;

    private static readonly string[] Statuses =
    [
        NhAssistantRunStatuses.Queued,
        NhAssistantRunStatuses.Running,
        NhAssistantRunStatuses.WaitingForApproval,
        NhAssistantRunStatuses.Completed,
        NhAssistantRunStatuses.Failed,
        NhAssistantRunStatuses.Cancelled
    ];

    /// <summary>
    /// Reads the filter and the page of a run history. An invalid part fails with
    /// <see cref="NhAssistantErrorCodes.RunFilterInvalid"/> and a field error per parameter:
    /// <c>page</c>, <c>itemsPerPage</c>, <c>agentId</c>, <c>subjectType</c> (also <c>required</c> for a
    /// subject id without its type), <c>subjectId</c>, <c>status</c>, and <c>to</c> for a period that
    /// ends before it starts.
    /// </summary>
    public static TaskResult<NhAssistantRunQuery> ReadQuery(
        string? agentId,
        string? subjectType,
        string? subjectId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? itemsPerPage)
    {
        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        var errors = new NhAssistantValidationErrors()
            .Require(pageNumber >= 1, "page", NhAssistantFieldErrors.Invalid)
            .Require(size is >= 1 and <= MaxItemsPerPage, "itemsPerPage", NhAssistantFieldErrors.Invalid)
            .Require(agentId is null || (!string.IsNullOrWhiteSpace(agentId) && agentId.Length <= 128), "agentId", NhAssistantFieldErrors.Invalid)
            .Require(subjectType is null || NhAiSubjectRef.IsValidType(subjectType), "subjectType", NhAssistantFieldErrors.Invalid)
            .Require(subjectId is null || subjectType is not null, "subjectType", NhAssistantFieldErrors.Required)
            .Require(
                subjectId is null
                    || (!string.IsNullOrWhiteSpace(subjectId)
                        && subjectId.Length <= NhAiSubjectRef.MaxIdLength
                        && subjectId.Trim() == subjectId),
                "subjectId",
                NhAssistantFieldErrors.Invalid)
            .Require(status is null || Statuses.Contains(status, StringComparer.Ordinal), "status", NhAssistantFieldErrors.Invalid)
            .Require(from is not { } start || to is not { } end || end > start, "to", NhAssistantFieldErrors.Invalid);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantRunQuery>.Failed(errors.ToResult(
                NhAssistantErrorCodes.RunFilterInvalid,
                "The filter of the run history is invalid."));
        }

        return TaskResult<NhAssistantRunQuery>.Succeeded(new NhAssistantRunQuery(
            new NhAssistantRunFilter(agentId, subjectType, subjectId, status, from?.ToUniversalTime(), to?.ToUniversalTime()),
            pageNumber,
            size));
    }

    /// <summary>
    /// A page of the runs the reader may see that match the filter, newest first.
    /// </summary>
    public async Task<NhAssistantRunHistoryListDto> ListAsync(
        NhAssistantRunQuery query,
        NhAssistantRunReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(reader);
        var (filter, page, itemsPerPage) = query;
        var candidates = await runs.FindThreadsWithRunsAsync(
            filter,
            reader.TenantId,
            reader.Administrator,
            MaxThreads + 1,
            cancellationToken);
        var visibility = new Visibility(this, reader);
        var visible = new List<Guid>(Math.Min(candidates.Count, MaxThreads));
        foreach (var thread in candidates.Take(MaxThreads))
        {
            if (await visibility.MayViewAsync(thread, cancellationToken))
            {
                visible.Add(thread.ConversationId);
            }
        }

        var (items, total) = await runs.ListRunsOfThreadsAsync(visible, filter, page, itemsPerPage, cancellationToken);
        return new NhAssistantRunHistoryListDto(await ToDtosAsync(items, cancellationToken), total)
        {
            Limited = candidates.Count > MaxThreads
        };
    }

    /// <summary>
    /// A page of every run of the agent that matches the filter, live and shadow, for its runs view in
    /// the administration. Fails with <see cref="NhAssistantErrorCodes.AgentNotFound"/> for an unknown
    /// agent.
    /// </summary>
    public async Task<TaskResult<NhAssistantRunHistoryListDto>> ListForAgentAsync(
        string agentId,
        NhAssistantRunQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(query);
        var (filter, page, itemsPerPage) = query;
        if (!_registration.AgentRuns.ContainsKey(agentId)
            && await _agents.FindAsync(agentId, includeDisabled: true, cancellationToken) is null)
        {
            return TaskResult<NhAssistantRunHistoryListDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var (items, total) = await runs.ListAgentRunsAsync(agentId, filter, page, itemsPerPage, cancellationToken);
        return TaskResult<NhAssistantRunHistoryListDto>.Succeeded(
            new NhAssistantRunHistoryListDto(await ToDtosAsync(items, cancellationToken), total));
    }

    /// <summary>
    /// One run with its whole result, its proposals and its files, when the reader may see it; a run
    /// the reader may not see fails like an unknown one, with <see cref="NhAssistantErrorCodes.RunNotFound"/>.
    /// </summary>
    public async Task<TaskResult<NhAssistantRunResultDto>> ReadAsync(
        Guid runId,
        NhAssistantRunReader reader,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(httpContext);
        var visibility = new Visibility(this, reader);
        var row = await FindVisibleAsync(runId, visibility, cancellationToken);
        if (row is null)
        {
            return TaskResult<NhAssistantRunResultDto>.Failed(NhAssistantErrorCodes.RunNotFound, "The run was not found.");
        }

        var (run, thread) = (row.Run, row.Thread);
        var (proposals, hidden) = await ProposalsAsync(run, visibility, cancellationToken);
        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        var role = await visibility.RoleAsync(subject, cancellationToken);
        var shield = NhAssistantRestrictedResults.For(httpContext, new NhAssistantThreadAccess(thread, subject, role, false));
        var files = new List<NhAssistantArtifactDto>();
        foreach (var artifact in await artifacts.ListOfRunAsync(thread.ConversationId, run.Id, MaxArtifacts, cancellationToken))
        {
            files.Add(new NhAssistantArtifactDto(
                artifact.Id,
                artifact.Name,
                artifact.Version,
                artifact.ContentType,
                artifact.SizeBytes,
                artifact.CreatedAt,
                artifact.RunId,
                !await NhAssistantAgentRunEndpoints.MayReadArtifactAsync(
                    shield,
                    store,
                    artifact.ToolInvocationId,
                    cancellationToken)));
        }

        // A result the run's rules wrote per culture reads in the reader's culture.
        var (result, truncated) = NhAssistantRunResults.ResultOf(run, CultureInfo.CurrentUICulture);
        return TaskResult<NhAssistantRunResultDto>.Succeeded(new NhAssistantRunResultDto(
            (await ToDtosAsync([row], cancellationToken))[0],
            result,
            truncated,
            run.Summary,
            proposals,
            hidden,
            files,
            await visibility.MayOpenThreadAsync(thread, cancellationToken)));
    }

    /// <summary>
    /// One file a run saved, when the reader may see the run and read the file: the agent's own grant
    /// shields a file from people who could not have read it themselves. Fails with
    /// <see cref="NhAssistantErrorCodes.RunNotFound"/> for a run the reader may not see,
    /// <see cref="NhAssistantErrorCodes.ArtifactNotFound"/> for a file the run did not save and
    /// <see cref="NhAssistantErrorCodes.SubjectAccessDenied"/> for a shielded file.
    /// </summary>
    public async Task<TaskResult<AssistantArtifact>> FindArtifactAsync(
        Guid runId,
        Guid artifactId,
        NhAssistantRunReader reader,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(httpContext);
        var visibility = new Visibility(this, reader);
        var row = await FindVisibleAsync(runId, visibility, cancellationToken);
        if (row is null)
        {
            return TaskResult<AssistantArtifact>.Failed(NhAssistantErrorCodes.RunNotFound, "The run was not found.");
        }

        var artifact = await artifacts.FindAsync(row.Thread.ConversationId, artifactId, cancellationToken);
        if (artifact is null || artifact.RunId != runId)
        {
            return TaskResult<AssistantArtifact>.Failed(NhAssistantErrorCodes.ArtifactNotFound, "The file was not found.");
        }

        var subject = new NhAiSubjectRef(row.Thread.SubjectType, row.Thread.SubjectId);
        var role = await visibility.RoleAsync(subject, cancellationToken);
        var shield = NhAssistantRestrictedResults.For(httpContext, new NhAssistantThreadAccess(row.Thread, subject, role, false));
        if (!await NhAssistantAgentRunEndpoints.MayReadArtifactAsync(
                shield,
                store,
                artifact.ToolInvocationId,
                cancellationToken))
        {
            return TaskResult<AssistantArtifact>.Failed(NhAssistantErrorCodes.SubjectAccessDenied, "The caller may not read this file.");
        }

        return TaskResult<AssistantArtifact>.Succeeded(artifact);
    }

    /// <summary>
    /// The run with its thread when the reader may see it: administrators every run, people by the
    /// rules of people's history.
    /// </summary>
    private async Task<NhAssistantRunRow?> FindVisibleAsync(Guid runId, Visibility visibility, CancellationToken cancellationToken)
    {
        var row = await runs.FindRunWithThreadAsync(runId, cancellationToken);
        if (row is null)
        {
            return null;
        }
        if (visibility.Reader.Administrator)
        {
            return row;
        }

        var thread = row.Thread;
        if (!string.Equals(thread.TenantId, visibility.Reader.TenantId, StringComparison.Ordinal)
            || !await visibility.MayViewAsync(thread, cancellationToken))
        {
            return null;
        }

        return row;
    }

    /// <summary>
    /// The run's proposals the reader may see, oldest first, and how many it placed at objects the
    /// reader may not read. Those in its own thread go with the run; a placed one with its object.
    /// </summary>
    private async Task<(IReadOnlyList<NhAssistantProposalDto> Visible, int Hidden)> ProposalsAsync(
        AssistantRun run,
        Visibility visibility,
        CancellationToken cancellationToken)
    {
        var approvals = await runs.GetRunApprovalsAsync(run.Id, cancellationToken);
        if (approvals.Count == 0)
        {
            return ([], 0);
        }

        var rows = await runs.GetThreadApprovalsByIdAsync(approvals.Select(approval => approval.Id).ToArray(), cancellationToken);
        var visible = new List<NhAssistantProposalDto>(rows.Count);
        var hidden = 0;
        foreach (var row in rows.OrderBy(item => item.Approval.CreatedAt).ThenBy(item => item.Approval.Id))
        {
            var own = row.Thread.ConversationId == run.ConversationId;
            if (!own && !visibility.Reader.Administrator && !await visibility.MayReadObjectAsync(row.Thread, cancellationToken))
            {
                hidden++;
                continue;
            }

            visible.Add(NhAssistantAgentRunDtoMapper.ToDto(row.Approval, row.Thread));
        }

        return (await NhAssistantAgentRunDtoMapper.CompleteAsync(visible, runs, _subjects, cancellationToken), hidden);
    }

    /// <summary>
    /// The runs as people read them, each agent named once.
    /// </summary>
    private async Task<IReadOnlyList<NhAssistantRunHistoryDto>> ToDtosAsync(
        IReadOnlyList<NhAssistantRunRow> rows,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var agentId in rows.Select(row => row.Thread.AgentId).Distinct(StringComparer.Ordinal))
        {
            names[agentId] = (await _agents.FindAsync(agentId, includeDisabled: true, cancellationToken))?.Definition.DisplayNameKey;
        }

        return rows
            .Select(row => ToDto(row) with { AgentDisplayNameKey = names[row.Thread.AgentId] })
            .ToArray();
    }

    private NhAssistantRunHistoryDto ToDto(NhAssistantRunRow row)
    {
        var (run, thread) = (row.Run, row.Thread);
        var (result, truncated) = NhAssistantRunResults.PreviewOf(run, CultureInfo.CurrentUICulture);
        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(_subjects, thread.SubjectType, thread.SubjectId);
        return new NhAssistantRunHistoryDto(
            run.Id,
            run.ConversationId,
            thread.AgentId,
            thread.SubjectType,
            thread.SubjectId,
            NhAssistantAgentRunDtoMapper.DisplayNameOf(_subjects, thread.SubjectType, thread.SubjectId, thread.SubjectDisplayName),
            run.TriggerId,
            run.Status,
            run.FailureClass,
            run.FailureCode,
            run.Kind,
            run.SkillId,
            run.InitiatorActorId,
            run.ToolCalls,
            run.ModelCalls,
            run.InputTokens,
            run.OutputTokens,
            run.ProposalCount,
            result,
            truncated,
            run.Shadow || thread.Shadow,
            run.CreatedAt,
            run.StartedAt,
            run.EndedAt)
        {
            SubjectTypeName = typeName,
            SubjectLink = link
        };
    }

    /// <summary>
    /// What one reader may see, decided once per subject and agent within a request.
    /// </summary>
    private sealed class Visibility(NhAssistantRunHistory history, NhAssistantRunReader reader)
    {
        private readonly Dictionary<NhAiSubjectRef, NhAiSubjectAccess> _roles = new();
        private readonly Dictionary<(string AgentId, string SubjectId), bool> _views = new();

        public NhAssistantRunReader Reader => reader;

        /// <summary>
        /// Whether the reader may see the runs of <paramref name="thread"/>: shadow work for
        /// administrators only, the whole application by the agent's view policy, an object for its
        /// readers and the people invited to the thread.
        /// </summary>
        public async Task<bool> MayViewAsync(AssistantThread thread, CancellationToken cancellationToken)
        {
            if (IsShadow(thread) && !reader.Administrator)
            {
                return false;
            }

            return string.Equals(thread.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal)
                ? await MayViewApplicationAsync(thread, cancellationToken)
                : await MayReadObjectAsync(thread, cancellationToken);
        }

        /// <summary>
        /// Whether the reader reads the thread's object, or was invited to the thread; reading never
        /// joins it.
        /// </summary>
        public async Task<bool> MayReadObjectAsync(AssistantThread thread, CancellationToken cancellationToken)
        {
            var role = await RoleAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), cancellationToken);
            var access = await history._threads.ReadAccessAsync(thread, reader.ActorId, role, cancellationToken);
            return access is not null && access.Role >= NhAiSubjectRole.Reader;
        }

        /// <summary>
        /// Whether the reader may open the thread's conversation as the thread endpoints decide.
        /// </summary>
        public async Task<bool> MayOpenThreadAsync(AssistantThread thread, CancellationToken cancellationToken)
        {
            if (IsShadow(thread) && !reader.Administrator)
            {
                return false;
            }

            return await MayReadObjectAsync(thread, cancellationToken);
        }

        public async Task<NhAiSubjectAccess> RoleAsync(NhAiSubjectRef subject, CancellationToken cancellationToken)
        {
            if (!_roles.TryGetValue(subject, out var role))
            {
                role = await history._subjects.ResolveAsync(subject, reader.ActorId, cancellationToken);
                _roles[subject] = role;
            }

            return role;
        }

        private async Task<bool> MayViewApplicationAsync(AssistantThread thread, CancellationToken cancellationToken)
        {
            var key = (thread.AgentId, thread.SubjectId);
            if (_views.TryGetValue(key, out var may))
            {
                return may;
            }

            // An agent that no longer exists has no view policy left to pass.
            var resolver = history._resolvers.OfType<NhAssistantSystemSubjectResolver>().FirstOrDefault();
            may = resolver is not null
                && await resolver.MayViewAgentAsync(
                    new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                    thread.AgentId,
                    reader.ActorId,
                    cancellationToken);
            _views[key] = may;
            return may;
        }

        private bool IsShadow(AssistantThread thread)
        {
            return thread.Shadow
                || (history._registration.AgentRuns.TryGetValue(thread.AgentId, out var runOptions) && runOptions.ShadowOfAgentId is not null);
        }
    }
}
