using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Who may approve proposals under an approval policy (FB20): the people an application asks to decide,
/// counts before it chooses a policy (<see cref="INhAssistantApprovalPolicySelector"/>), or tells that a
/// proposal waits for them. It is a server API for the application's code; there is no endpoint, because it
/// lists people.
/// </summary>
/// <remarks>
/// <para>
/// The candidates are the subject's members, as its access resolver lists them now
/// (<see cref="INhAiSubjectAccessResolver.ListMembersAsync"/>, never the member snapshot), and, for a
/// proposal, its initiator. Each candidate is checked as a decision checks the person, without a request:
/// the subject role and named role through <see cref="INhAiSubjectAccessResolver.ResolveAsync"/>, a policy
/// rule (<see cref="NhAiApprovers.Policy"/>) against the principal <see cref="INhAiPrincipalFactory"/>
/// rebuilds, and, for a proposal, the tool's approve policies and the agent's view policy of a thread of
/// the whole application. An agent is never listed.
/// </para>
/// <para>
/// <see cref="NhAssistantApproverList.Complete"/> is <see langword="false"/> when the list stopped at its
/// limit while candidates remained, when the subject has more than
/// <see cref="NhAssistantApproverList.MaxCandidates"/> members, or when people outside the members may
/// approve: a policy rule admits anyone who passes it, and the whole application lists no members. A
/// resolver that gives a role to people it does not list, for example through a permission, makes the list
/// incomplete without the directory knowing. An application that must not decide on an incomplete list
/// keeps its strictest choice then.
/// </para>
/// <para>
/// An application that knows who those people outside the members are names them with an
/// <see cref="INhAssistantApproverCandidates"/> (<c>UseApproverCandidates</c>, FB50). The directory then
/// asks it for the policy, the subject and the subject's tenant, checks each person it names exactly as it
/// checks a member, lists those who pass after the members, and answers
/// <see cref="NhAssistantApproverList.Complete"/> <see langword="true"/> when the application declared its
/// list complete and neither the limit nor <see cref="NhAssistantApproverList.MaxCandidates"/> stopped the
/// listing. Without one nothing changes.
/// </para>
/// <para>
/// A subject or proposal of another tenant reads as unknown, as for a decision
/// (<see cref="NhAssistantDecidingPerson.TenantId"/>): pass the tenant the caller works for, and
/// <see langword="null"/> only in a host without tenants or for its operators.
/// </para>
/// </remarks>
public interface INhAssistantApproverDirectory
{
    /// <summary>
    /// The people who may approve a proposal at <paramref name="subject"/> under the policy
    /// <paramref name="policyId"/>, the people the application asks, without
    /// <paramref name="excludeActorId"/>, such as the initiator of a proposal under four eyes. No proposal
    /// is known here, so the initiator rule admits nobody.
    /// Fails with <c>assistant-approval-policy-unknown</c> for a policy that is neither registered nor one
    /// of the defaults (<c>trigger-default</c>, <c>chat-default</c>), and with
    /// <c>assistant-subject-not-found</c> for a subject that is unknown or of another tenant.
    /// </summary>
    /// <param name="policyId">A policy registered with <c>AddApprovalPolicy</c>, or a default.</param>
    /// <param name="subject">The subject the proposal would wait at.</param>
    /// <param name="tenantId">The tenant the caller works for.</param>
    /// <param name="excludeActorId">A person never listed, such as the one who would start the work.</param>
    /// <param name="limit">The most approvers listed: 1 to <see cref="NhAssistantApproverList.MaxLimit"/>.</param>
    /// <param name="cancellationToken">Ends the listing.</param>
    Task<TaskResult<NhAssistantApproverList>> ListAsync(
        string policyId,
        NhAiSubjectRef subject,
        string? tenantId,
        string? excludeActorId = null,
        int limit = NhAssistantApproverList.DefaultLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many people <see cref="ListAsync"/> lists, at most <paramref name="limit"/>, with the same
    /// <see cref="NhAssistantApproverCount.Complete"/> and failures. A count of one with
    /// <paramref name="limit"/> one answers "does anyone else approve" with the fewest checks.
    /// </summary>
    Task<TaskResult<NhAssistantApproverCount>> CountAsync(
        string policyId,
        NhAiSubjectRef subject,
        string? tenantId,
        string? excludeActorId = null,
        int limit = NhAssistantApproverList.DefaultLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The people who may decide the proposal under its own policy copy, the policy it was recorded with,
    /// also one an approval policy selector chose: its subject's members and its initiator, without the
    /// initiator under four eyes and without anyone who fails the tool's approve policies. A proposal the
    /// person who asked decides in their own conversation (a consultation's) lists only that person. Fails
    /// with <c>assistant-approval-not-found</c> for an unknown proposal or one of another tenant, and with
    /// <c>assistant-subject-not-found</c> when its subject no longer exists.
    /// </summary>
    /// <param name="approvalId">The proposal.</param>
    /// <param name="tenantId">The tenant the caller works for.</param>
    /// <param name="limit">The most approvers listed: 1 to <see cref="NhAssistantApproverList.MaxLimit"/>.</param>
    /// <param name="cancellationToken">Ends the listing.</param>
    Task<TaskResult<NhAssistantApproverList>> ListForProposalAsync(
        Guid approvalId,
        string? tenantId,
        int limit = NhAssistantApproverList.DefaultLimit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One person who may approve.
/// </summary>
/// <param name="ActorId">The person's actor id.</param>
/// <param name="Role">The person's role on the subject now; <see cref="NhAiSubjectRole.None"/> for an
/// initiator who approves only as the initiator.</param>
public sealed record NhAssistantApprover(string ActorId, NhAiSubjectRole Role)
{
    /// <summary>
    /// The name the subject's resolver lists the person with, or <see langword="null"/>.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Whether the person is the proposal's initiator.
    /// </summary>
    public bool Initiator { get; init; }
}

/// <summary>
/// The people who may approve, the initiator first and then in the order the subject's resolver lists its
/// members, and whether that is everyone.
/// </summary>
/// <param name="Approvers">The people, at most the asked limit.</param>
/// <param name="Complete">Whether nobody else may approve: <see langword="false"/> at the limit with
/// candidates left, for a subject with more than <see cref="MaxCandidates"/> members, and when people
/// outside the members may approve, unless the application named them all
/// (<see cref="INhAssistantApproverCandidates"/>).</param>
public sealed record NhAssistantApproverList(IReadOnlyList<NhAssistantApprover> Approvers, bool Complete)
{
    /// <summary>The limit of a listing that names none.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The highest limit of one listing.</summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// The most members of one subject that one listing checks, and the most people an
    /// <see cref="INhAssistantApproverCandidates"/> names that it checks.
    /// </summary>
    public const int MaxCandidates = 1_000;
}

/// <summary>
/// How many people may approve, at most the asked limit, and whether that is everyone
/// (<see cref="NhAssistantApproverList.Complete"/>).
/// </summary>
public sealed record NhAssistantApproverCount(int Count, bool Complete);

/// <summary>
/// The approver directory over the decision's own checks (<see cref="NhAssistantApproverCheck"/>).
/// </summary>
internal sealed class NhAssistantApproverDirectory(
    NhAssistantApprovalPolicies policies,
    NhAssistantSubjectAccessService subjects,
    NhAssistantApproverCheck approvers,
    NhAssistantTenancy tenancy,
    INhAssistantStore store,
    NhAssistantRunStore runs,
    IServiceProvider services,
    ILogger<NhAssistantApproverDirectory> logger) : INhAssistantApproverDirectory
{
    public async Task<TaskResult<NhAssistantApproverList>> ListAsync(
        string policyId,
        NhAiSubjectRef subject,
        string? tenantId,
        string? excludeActorId = null,
        int limit = NhAssistantApproverList.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(subject);
        ValidateLimit(limit);

        var policy = policies.FindKnown(policyId);
        if (policy is null)
        {
            return TaskResult<NhAssistantApproverList>.Failed(NhAssistantErrorCodes.ApprovalPolicyUnknown, "The approval policy is not registered.");
        }

        var info = await subjects.DescribeForAsync(subject, tenantId, cancellationToken);
        if (info is null)
        {
            return TaskResult<NhAssistantApproverList>.Failed(NhAssistantErrorCodes.SubjectNotFound, "The subject was not found.");
        }

        var found = await FindAsync(
            new Candidates(policy, info, Thread: null, InitiatorActorId: null, Placed: false, ToolId: null, excludeActorId),
            limit,
            cancellationToken);
        return TaskResult<NhAssistantApproverList>.Succeeded(found);
    }

    public async Task<TaskResult<NhAssistantApproverCount>> CountAsync(
        string policyId,
        NhAiSubjectRef subject,
        string? tenantId,
        string? excludeActorId = null,
        int limit = NhAssistantApproverList.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var listed = await ListAsync(policyId, subject, tenantId, excludeActorId, limit, cancellationToken);
        if (!listed.Success)
        {
            return TaskResult<NhAssistantApproverCount>.Failed(listed);
        }

        return TaskResult<NhAssistantApproverCount>.Succeeded(
            new NhAssistantApproverCount(listed.Data!.Approvers.Count, listed.Data.Complete));
    }

    public async Task<TaskResult<NhAssistantApproverList>> ListForProposalAsync(
        Guid approvalId,
        string? tenantId,
        int limit = NhAssistantApproverList.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);

        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        if (approval is null || thread is null || !tenancy.Reaches(thread, tenantId))
        {
            return TaskResult<NhAssistantApproverList>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }

        var info = await subjects.DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), cancellationToken);
        if (info is null)
        {
            return TaskResult<NhAssistantApproverList>.Failed(NhAssistantErrorCodes.SubjectNotFound, "The subject was not found.");
        }

        // The decision checks the policy copy the proposal was recorded with, also a policy a selector chose.
        var policy = approval.PolicyJson is null
            ? NhAiApprovalPolicy.TriggerDefault
            : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        var candidates = new Candidates(
            policy,
            info,
            thread,
            approval.InitiatorActorId,
            approval.PlacedFromConversationId is not null,
            approval.ToolId,
            ExcludeActorId: null);
        if (IsAskerDecision(approval))
        {
            // Only the person who asked decides a consultation's proposal in their own conversation.
            return TaskResult<NhAssistantApproverList>.Succeeded(await AskerAsync(candidates, cancellationToken));
        }

        return TaskResult<NhAssistantApproverList>.Succeeded(await FindAsync(candidates, limit, cancellationToken));
    }

    /// <summary>
    /// A consultation's proposal that the person who asked decides in their own conversation.
    /// </summary>
    private static bool IsAskerDecision(AssistantApproval approval)
    {
        return approval.ConsultationId is not null
            && approval.Mode == NhAssistantApprovalModes.Inline
            && approval.RunId is null;
    }

    private async Task<NhAssistantApproverList> AskerAsync(Candidates candidates, CancellationToken cancellationToken)
    {
        if (candidates.InitiatorActorId is not { } asker)
        {
            return new NhAssistantApproverList([], Complete: true);
        }

        var approver = await CheckAsync(candidates, asker, displayName: null, cancellationToken);
        return new NhAssistantApproverList(approver is null ? [] : [approver], Complete: true);
    }

    private async Task<NhAssistantApproverList> FindAsync(
        Candidates candidates,
        int limit,
        CancellationToken cancellationToken)
    {
        var policy = candidates.Policy;
        var subject = candidates.Subject.Subject;
        // People outside the members may approve: anyone who passes a policy rule, and anyone with a role
        // on a subject whose resolver lists no members, such as the whole application. Only the
        // application can name them (FB50).
        var policyNames = policy.Approvers
            .OfType<NhAiPolicyApproverRule>()
            .Select(rule => rule.PolicyName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var roleHolders = !subjects.ListsMembers(subject.Type)
            && !policy.Approvers.All(rule => rule is NhAiInitiatorApproverRule or NhAiPolicyApproverRule);
        var outsiders = policyNames.Length > 0 || roleHolders;
        var complete = !outsiders;

        var found = new List<NhAssistantApprover>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.InitiatorActorId is { } initiator && seen.Add(initiator))
        {
            if (await CheckAsync(candidates, initiator, displayName: null, cancellationToken) is { } approver)
            {
                found.Add(approver);
            }
        }

        var checkedMembers = 0;
        var stopped = false;
        await foreach (var member in subjects.ListMembersAsync(subject, cancellationToken))
        {
            if (!seen.Add(member.ActorId))
            {
                if (string.Equals(member.ActorId, candidates.InitiatorActorId, StringComparison.Ordinal))
                {
                    // The initiator is a member too: keep the name the resolver lists.
                    var index = found.FindIndex(item => item.Initiator);
                    if (index >= 0 && found[index].DisplayName is null && member.DisplayName is not null)
                    {
                        found[index] = found[index] with { DisplayName = member.DisplayName };
                    }
                }

                continue;
            }
            if (found.Count >= limit || checkedMembers >= NhAssistantApproverList.MaxCandidates)
            {
                // Someone is left unchecked.
                complete = false;
                stopped = true;
                break;
            }

            checkedMembers++;
            if (await CheckAsync(candidates, member.ActorId, member.DisplayName, cancellationToken) is { } approver)
            {
                found.Add(approver);
            }
        }

        if (outsiders && !stopped && services.GetService<INhAssistantApproverCandidates>() is { } application)
        {
            var info = candidates.Subject;
            var query = new NhAssistantApproverCandidateQuery(policy.Id, subject, info.TenantId)
            {
                DivisionId = info.DivisionId,
                PolicyNames = policyNames,
                SubjectRoleHolders = roleHolders,
                MaxCandidates = NhAssistantApproverList.MaxCandidates
            };
            var named = await AskAsync(application, query, cancellationToken);
            // The application's word decides whether the people outside the members are everyone; the
            // limits below can still make the list incomplete.
            complete = named.Complete;

            var checkedNamed = 0;
            foreach (var candidate in named.Candidates ?? [])
            {
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.ActorId) || !seen.Add(candidate.ActorId))
                {
                    continue;
                }
                if (found.Count >= limit || checkedNamed >= NhAssistantApproverList.MaxCandidates)
                {
                    // Someone the application named is left unchecked.
                    complete = false;
                    break;
                }

                checkedNamed++;
                if (await CheckAsync(candidates, candidate.ActorId, candidate.DisplayName, cancellationToken) is { } approver)
                {
                    found.Add(approver);
                }
            }
        }

        return new NhAssistantApproverList(found.ToArray(), complete);
    }

    /// <summary>
    /// The people the application names outside the members (FB50). A candidates service that throws
    /// leaves the list incomplete; the log names its type and the exception's type only.
    /// </summary>
    private async Task<NhAssistantApproverCandidateList> AskAsync(
        INhAssistantApproverCandidates application,
        NhAssistantApproverCandidateQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await application.ListAsync(query, cancellationToken) ?? NhAssistantApproverCandidateList.Unknown;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Approver candidates {CandidatesType} failed to name the approvers of policy {PolicyId} with {ExceptionType}; the list is not complete.",
                application.GetType().FullName,
                query.PolicyId,
                exception.GetType().Name);
            return NhAssistantApproverCandidateList.Unknown;
        }
    }

    /// <summary>
    /// The person as an approver, or <see langword="null"/>: the checks of a decision, without a request.
    /// </summary>
    private async Task<NhAssistantApprover?> CheckAsync(
        Candidates candidates,
        string actorId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId)
            || actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal)
            || string.Equals(actorId, candidates.ExcludeActorId, StringComparison.Ordinal))
        {
            return null;
        }

        var initiator = string.Equals(actorId, candidates.InitiatorActorId, StringComparison.Ordinal);
        if (initiator && candidates.Policy.FourEyes)
        {
            return null;
        }
        if (candidates.Thread is { } thread && !await approvers.MaySeeAsync(thread, actorId, cancellationToken))
        {
            return null;
        }

        var subject = candidates.Subject;
        var access = await subjects.ResolveAsync(subject.Subject, actorId, cancellationToken);
        var resource = candidates.Thread is { } home
            ? NhAssistantApproverCheck.ResourceOf(home, subject)
            : new NhAiSubjectAuthorizationResource(subject.Subject, subject.ScopeKey, subject.DivisionId, "approval");
        ClaimsPrincipal? built = null;
        var asked = false;
        async ValueTask<ClaimsPrincipal?> PrincipalAsync()
        {
            // Rebuilt once per person, only when a rule needs it, as a headless run rebuilds it.
            if (!asked)
            {
                asked = true;
                built = services.GetService<INhAiPrincipalFactory>() is { } factory
                    ? await factory.CreateAsync(actorId, subject, cancellationToken)
                    : null;
            }

            return built;
        }

        if (!await approvers.QualifiesAsync(candidates.Policy, actorId, candidates.InitiatorActorId, candidates.Placed, access, PrincipalAsync, resource))
        {
            return null;
        }
        if (candidates.ToolId is { } toolId && !await approvers.SatisfiesToolPoliciesAsync(toolId, PrincipalAsync, resource))
        {
            return null;
        }

        return new NhAssistantApprover(actorId, access.Role)
        {
            DisplayName = displayName,
            Initiator = initiator
        };
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > NhAssistantApproverList.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"An approver listing names 1 to {NhAssistantApproverList.MaxLimit} people.");
        }
    }

    /// <summary>
    /// Whom a listing checks, and against what: the policy, the subject as its resolver described it, the
    /// thread of a proposal, its initiator, whether it was placed at another object, its tool and the
    /// person left out.
    /// </summary>
    private sealed record Candidates(
        NhAiApprovalPolicy Policy,
        NhAiSubjectInfo Subject,
        AssistantThread? Thread,
        string? InitiatorActorId,
        bool Placed,
        string? ToolId,
        string? ExcludeActorId);
}
