using System.Globalization;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Lists the proposals of agent runs and consultations that wait for a decision, without their content,
/// for the application's back-end: which subjects have open proposals, such as for a check of the
/// application's own records or a page of its own. It is a server API without a person and has no
/// endpoint: the application decides who may learn about these proposals, as for
/// <see cref="INhAssistantProposalRevalidation"/>. Shadow proposals and proposals of a person's own
/// turn are not listed. A proposal past its expiry is listed until the run maintenance or a decision
/// closes it.
/// </summary>
public interface INhAssistantOpenProposals
{
    /// <summary>
    /// One page of open proposals in the order they were made, filtered as <paramref name="query"/>
    /// says. Pass the page's <see cref="NhAssistantOpenProposalPage.ContinuationToken"/> to read the next
    /// one; a proposal that closes in between is not listed again, and one made in between is listed
    /// on a later page.
    /// </summary>
    /// <exception cref="ArgumentException">The query names an invalid subject type, subject, scope key,
    /// limit or continuation token.</exception>
    Task<NhAssistantOpenProposalPage> ListAsync(
        NhAssistantOpenProposalQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Which open proposals <see cref="INhAssistantOpenProposals.ListAsync"/> lists.
/// </summary>
public sealed record NhAssistantOpenProposalQuery
{
    /// <summary>
    /// The most proposals one page lists.
    /// </summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// Only proposals about subjects of this type, such as <c>project</c>; <see langword="null"/> for
    /// every type. Ignored when <see cref="Subject"/> is set.
    /// </summary>
    public string? SubjectType { get; init; }

    /// <summary>
    /// Only proposals about this subject; <see langword="null"/> for every subject.
    /// </summary>
    public NhAiSubjectRef? Subject { get; init; }

    /// <summary>
    /// Only proposals in threads of this scope (<see cref="NhAiSubjectInfo.CreateScopeKey"/>), such as
    /// one tenant's or division's; <see langword="null"/> for every scope. An equal subject id in
    /// another scope is another subject.
    /// </summary>
    public string? ScopeKey { get; init; }

    /// <summary>
    /// The most proposals the page lists, from 1 to <see cref="MaxLimit"/>; 100 by default.
    /// </summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// Where the page continues: the <see cref="NhAssistantOpenProposalPage.ContinuationToken"/> of the
    /// page before it, or <see langword="null"/> for the first page.
    /// </summary>
    public string? ContinuationToken { get; init; }
}

/// <summary>
/// One page of open proposals.
/// </summary>
/// <param name="Items">The proposals, in the order they were made.</param>
/// <param name="ContinuationToken">Where the next page continues, or <see langword="null"/> when this
/// page is the last.</param>
public sealed record NhAssistantOpenProposalPage(
    IReadOnlyList<NhAssistantOpenProposal> Items,
    string? ContinuationToken);

/// <summary>
/// A proposal that waits for a decision, without its content.
/// </summary>
/// <param name="ApprovalId">The proposal, as the decision endpoints and
/// <c>INhAssistantProposalDecisionService</c> name it.</param>
/// <param name="Subject">The subject whose thread it waits in.</param>
/// <param name="ScopeKey">The scope of that thread.</param>
/// <param name="AgentId">The agent that proposed it.</param>
/// <param name="ToolId">The tool it would run.</param>
/// <param name="CreatedAt">When it was made, on the system clock.</param>
/// <param name="ExpiresAt">When it expires, on the application's clock.</param>
public sealed record NhAssistantOpenProposal(
    Guid ApprovalId,
    NhAiSubjectRef Subject,
    string ScopeKey,
    string AgentId,
    string ToolId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Lists open proposals from the run store; see <see cref="INhAssistantOpenProposals"/>.
/// </summary>
internal sealed class NhAssistantOpenProposals(NhAssistantRunStore runs) : INhAssistantOpenProposals
{
    public async Task<NhAssistantOpenProposalPage> ListAsync(
        NhAssistantOpenProposalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > NhAssistantOpenProposalQuery.MaxLimit)
        {
            throw new ArgumentException($"A page lists 1 to {NhAssistantOpenProposalQuery.MaxLimit} proposals.", nameof(query));
        }
        if (query.Subject is null && query.SubjectType is not null && !NhAiSubjectRef.IsValidType(query.SubjectType))
        {
            throw new ArgumentException("The subject type is not a valid subject type.", nameof(query));
        }
        if (query.ScopeKey is not null && (string.IsNullOrWhiteSpace(query.ScopeKey) || query.ScopeKey.Length > 128))
        {
            throw new ArgumentException("A scope key has 1 to 128 characters.", nameof(query));
        }

        var after = ReadToken(query.ContinuationToken);
        var rows = await runs.GetOpenProposalPageAsync(
            query.Subject?.Type ?? query.SubjectType,
            query.Subject?.Id,
            query.ScopeKey,
            after,
            query.Limit,
            cancellationToken);
        var items = rows
            .Select(row => new NhAssistantOpenProposal(
                row.Approval.Id,
                new NhAiSubjectRef(row.Thread.SubjectType, row.Thread.SubjectId),
                row.Thread.ScopeKey,
                row.Thread.AgentId,
                row.Approval.ToolId,
                row.Approval.CreatedAt,
                row.Approval.ExpiresAt))
            .ToArray();
        var last = rows.Count < query.Limit ? null : rows[^1].Approval;
        return new NhAssistantOpenProposalPage(items, last is null ? null : WriteToken(last.CreatedAt, last.Id));
    }

    /// <summary>
    /// The token of the proposal a page ended with: its creation time in UTC ticks and its id.
    /// </summary>
    private static string WriteToken(DateTimeOffset createdAt, Guid id)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{createdAt.UtcTicks}.{id:N}");
    }

    private static (DateTimeOffset CreatedAt, Guid Id)? ReadToken(string? token)
    {
        if (token is null)
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            throw new ArgumentException("The continuation token is not one a page of open proposals returned.", nameof(token));
        }

        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}
