namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The people outside a subject's members who may approve under an approval policy, as the application
/// knows them (FB50), so the approver directory (<c>INhAssistantApproverDirectory</c> of
/// <c>NewHeap.Platform.AI.Chat.AspNet</c>) can say that its list is complete. A subject's resolver lists
/// its members, but a policy rule (<see cref="NhAiApprovers.Policy"/>) admits anyone who passes the
/// authorization policy, and a subject whose resolver lists no members, such as the whole application,
/// admits whoever its roles admit; only the application knows who those people are. Without candidates
/// the directory therefore never calls such a list complete, and an application that asks "does exactly
/// one person approve" before it chooses four eyes always keeps its strictest choice.
/// </summary>
/// <remarks>
/// <para>
/// Register one with <see cref="NhAssistantBuilder.UseApproverCandidates{TCandidates}"/>; it is a scoped
/// service. The directory asks it only when people outside the members may approve
/// (<see cref="NhAssistantApproverCandidateQuery.PolicyNames"/> is not empty, or
/// <see cref="NhAssistantApproverCandidateQuery.SubjectRoleHolders"/> is <see langword="true"/>), and only
/// once the members did not fill the listing's limit. It lists the candidates after the initiator and the
/// members, in the order given, without the people already listed.
/// </para>
/// <para>
/// A candidate is a name, never a right: the directory checks each one exactly as it checks a member and as
/// a decision checks the person, against the principal <c>INhAiPrincipalFactory</c> rebuilds (the subject
/// role, the policy rules, the tool's approve policies and four eyes for a proposal), so naming too many
/// people lists nobody who may not approve. Naming too few is what the application must avoid: declare the
/// list complete (<see cref="NhAssistantApproverCandidateList.Complete"/>) only when nobody outside the
/// list and the members may pass the policy's rules at the subject, in the subject's tenant
/// (<see cref="NhAssistantApproverCandidateQuery.TenantId"/>). The directory answers
/// <c>Complete: true</c> only then and when no limit of its own applied: the listing's limit, and at most
/// <see cref="NhAssistantApproverCandidateQuery.MaxCandidates"/> candidates.
/// </para>
/// <para>
/// It reads and changes nothing, and answers quickly: an approval policy selector asks the directory within
/// its own time limit. A candidates service that throws is logged with its type and the exception's type,
/// and the list is then not complete.
/// </para>
/// </remarks>
public interface INhAssistantApproverCandidates
{
    /// <summary>
    /// The people outside the members of <see cref="NhAssistantApproverCandidateQuery.Subject"/> who may
    /// pass the rules of the policy that <paramref name="query"/> names, and whether that is everyone.
    /// </summary>
    /// <param name="query">The policy, the subject and what the application should name.</param>
    /// <param name="cancellationToken">Ends the listing.</param>
    ValueTask<NhAssistantApproverCandidateList> ListAsync(
        NhAssistantApproverCandidateQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// What the approver directory asks an <see cref="INhAssistantApproverCandidates"/>: the people who may
/// approve under <paramref name="PolicyId"/> at <paramref name="Subject"/> without being one of its listed
/// members.
/// </summary>
/// <param name="PolicyId">The approval policy: a registered one, a default, or the policy copy of a
/// proposal, which may be a policy a selector chose.</param>
/// <param name="Subject">The subject the proposal waits or would wait at, such as a project or the whole
/// application (<see cref="NhAiSystemSubjects.Global"/>).</param>
/// <param name="TenantId">The subject's tenant, as its resolver described it; <see langword="null"/> in a host
/// without tenants and for the whole application. Name only people of this tenant.</param>
public sealed record NhAssistantApproverCandidateQuery(
    string PolicyId,
    NhAiSubjectRef Subject,
    string? TenantId)
{
    /// <summary>
    /// The subject's division, as its resolver described it, or <see langword="null"/>.
    /// </summary>
    public Guid? DivisionId { get; init; }

    /// <summary>
    /// The authorization policies of the policy's rules (<see cref="NhAiApprovers.Policy"/>): name the
    /// people who may pass one of them at the subject.
    /// </summary>
    public IReadOnlyList<string> PolicyNames { get; init; } = [];

    /// <summary>
    /// Whether to also name the people who hold a role at the subject: <see langword="true"/> when the
    /// subject's resolver lists no members, such as the whole application, whose roles follow its read and
    /// approve policies (<c>UseSystemSubject</c>), and a rule of the policy admits by subject role.
    /// </summary>
    public bool SubjectRoleHolders { get; init; }

    /// <summary>
    /// The most candidates the directory checks. A list with more people is never complete; an application
    /// with more may name the first ones and declare its list incomplete.
    /// </summary>
    public int MaxCandidates { get; init; }
}

/// <summary>
/// The people an <see cref="INhAssistantApproverCandidates"/> names, and whether they are everyone outside
/// the subject's members who may approve.
/// </summary>
/// <param name="Candidates">The people, each checked by the directory before it lists them.</param>
/// <param name="Complete">Whether nobody else outside the members may pass the policy's rules at the
/// subject. <see langword="false"/> keeps the directory's list incomplete.</param>
public sealed record NhAssistantApproverCandidateList(
    IReadOnlyList<NhAssistantApproverCandidate> Candidates,
    bool Complete)
{
    /// <summary>
    /// An answer that names nobody and leaves the list incomplete: the application cannot tell who may
    /// approve under this policy.
    /// </summary>
    public static NhAssistantApproverCandidateList Unknown { get; } = new([], Complete: false);
}

/// <summary>
/// One person an <see cref="INhAssistantApproverCandidates"/> names.
/// </summary>
/// <param name="ActorId">The person's actor id, as the principal factory and the subject's resolver know
/// it.</param>
public sealed record NhAssistantApproverCandidate(string ActorId)
{
    /// <summary>
    /// The name the directory lists the person with, or <see langword="null"/>.
    /// </summary>
    public string? DisplayName { get; init; }
}
