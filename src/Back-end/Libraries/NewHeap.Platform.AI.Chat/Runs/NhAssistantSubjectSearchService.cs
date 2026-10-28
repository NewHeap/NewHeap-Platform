using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Searches the subjects of one type for a person through the type's <see cref="INhAiSubjectSearch"/>
/// and keeps only what that person may see: a live role of at least reader from the resolver and
/// the person's own tenant, as for threads and proposals. The whole application's divisions belong to
/// no tenant; with the single-tenant mode its tenant's people find them as well.
/// </summary>
internal sealed class NhAssistantSubjectSearchService(
    NhAssistantSubjectAccessService subjects,
    NhAssistantTenancy tenancy)
{
    /// <summary>
    /// The search reads at most this many results per result it may show, so a search that returns
    /// many subjects the person cannot read ends early instead of asking the resolver for each.
    /// </summary>
    public const int MaxExaminedPerResult = 4;

    public async Task<TaskResult<IReadOnlyList<NhAiSubjectInfo>>> SearchAsync(
        string subjectType,
        string? query,
        int? limit,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (!NhAiSubjectRef.IsValidType(subjectType) || subjects.FindType(subjectType) is null)
        {
            return TaskResult<IReadOnlyList<NhAiSubjectInfo>>.Failed(
                NhAssistantErrorCodes.SubjectTypeUnknown,
                "The subject type is not registered.");
        }

        var text = query?.Trim() ?? string.Empty;
        if (text.Length is < NhAiSubjectSearchRequest.MinQueryLength or > NhAiSubjectSearchRequest.MaxQueryLength)
        {
            return TaskResult<IReadOnlyList<NhAiSubjectInfo>>.Failed(
                NhAssistantValidationErrors.Failed("query", NhAssistantFieldErrors.Invalid));
        }

        var search = subjects.FindSearch(subjectType);
        if (search is null)
        {
            return TaskResult<IReadOnlyList<NhAiSubjectInfo>>.Failed(
                NhAssistantErrorCodes.SubjectSearchUnavailable,
                "The subject type offers no search.");
        }

        var size = Math.Clamp(limit ?? NhAiSubjectSearchRequest.DefaultLimit, 1, NhAiSubjectSearchRequest.MaxLimit);
        var maxExamined = size * MaxExaminedPerResult;
        var request = new NhAiSubjectSearchRequest(subjectType, text, actorId, size);
        var found = new List<NhAiSubjectInfo>(size);
        var seen = new HashSet<NhAiSubjectRef>();
        var examined = 0;
        await foreach (var info in search.SearchAsync(request, cancellationToken))
        {
            examined++;
            if (info is not null
                && seen.Add(Validated(info, subjectType).Subject)
                && await IsVisibleAsync(info, actorId, tenantId, cancellationToken))
            {
                found.Add(info);
            }

            if (found.Count == size || examined == maxExamined)
            {
                break;
            }
        }

        IReadOnlyList<NhAiSubjectInfo> visible = found;
        return TaskResult<IReadOnlyList<NhAiSubjectInfo>>.Succeeded(visible);
    }

    /// <summary>
    /// A subject the caller may see: in the caller's tenant, as threads and proposals are, or of the
    /// whole application for a caller who takes part in it, and with at least the reader role from the
    /// live resolver.
    /// </summary>
    private async Task<bool> IsVisibleAsync(
        NhAiSubjectInfo info,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        // Search results leave out other tenants' subjects without a warning.
        if (!tenancy.Includes(info, tenantId))
        {
            return false;
        }

        var access = await subjects.ResolveAsync(info.Subject, actorId, cancellationToken);
        return access.Has(NhAiSubjectRole.Reader);
    }

    /// <summary>
    /// A search result describes a subject of the searched type as <c>DescribeAsync</c> would;
    /// anything else is an error in the application's search.
    /// </summary>
    private static NhAiSubjectInfo Validated(NhAiSubjectInfo info, string subjectType)
    {
        if (info.Subject.Type != subjectType || string.IsNullOrWhiteSpace(info.ScopeKey) || info.ScopeKey.Length > 128)
        {
            throw new InvalidOperationException(
                $"The subject search for '{subjectType}' returned an invalid description.");
        }

        return info;
    }
}
