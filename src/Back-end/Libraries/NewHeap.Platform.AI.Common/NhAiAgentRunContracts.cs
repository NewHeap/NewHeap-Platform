using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

/// <summary>
/// Why a tool invocation was allowed for an agent.
/// </summary>
public enum NhAiGrantSource
{
    /// <summary>
    /// The invocation did not run for an agent, or the source is unknown.
    /// </summary>
    None = 0,

    /// <summary>
    /// The agent registration granted the tool, independent of the person the agent works for.
    /// </summary>
    Agent = 1,

    /// <summary>
    /// The initiator's own rights allowed the tool.
    /// </summary>
    Delegated = 2,

    /// <summary>
    /// A read of the API bridge gateway in a headless run, through a short-lived delegated read
    /// credential of the person who started the run, or of its accountable person when nobody did.
    /// </summary>
    DelegatedRead = 3
}

/// <summary>
/// Who answers for an agent run.
/// </summary>
public enum NhAiAgentAccountability
{
    /// <summary>
    /// The agent itself. Every effect other than a read then needs a person's approval.
    /// </summary>
    Agent = 0,

    /// <summary>
    /// The responsible user that the subject access resolver names for the subject.
    /// </summary>
    SubjectResponsible = 1,

    /// <summary>
    /// A technical user account the application configures for the agent.
    /// </summary>
    ServiceAccount = 2
}

/// <summary>
/// A typed reference to the domain object an agent works on, for example
/// <c>project/7f3c…</c>.
/// </summary>
public sealed partial record NhAiSubjectRef
{
    public const int MaxTypeLength = 100;
    public const int MaxIdLength = 200;

    public NhAiSubjectRef(string type, string id)
    {
        if (!IsValidType(type))
        {
            throw new ArgumentException(
                "A subject type is a dash-case identifier of at most 100 characters.",
                nameof(type));
        }

        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength || id.Trim() != id)
        {
            throw new ArgumentException(
                "A subject id is a non-empty value of at most 200 characters without surrounding whitespace.",
                nameof(id));
        }

        Type = type;
        Id = id;
    }

    public string Type { get; }

    public string Id { get; }

    public static bool IsValidType(string? type)
    {
        return !string.IsNullOrEmpty(type)
               && type.Length <= MaxTypeLength
               && SubjectTypePattern().IsMatch(type);
    }

    public override string ToString()
    {
        return $"{Type}/{Id}";
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectTypePattern();
}

/// <summary>
/// The role a person has on a subject. Roles are ordered: each role includes the rights of
/// the roles below it.
/// </summary>
public enum NhAiSubjectRole
{
    None = 0,

    /// <summary>
    /// Sees the subject's agent threads.
    /// </summary>
    Reader = 10,

    /// <summary>
    /// May also type in a thread, which starts a turn with that person as initiator, and
    /// cancel runs.
    /// </summary>
    Participant = 20,

    /// <summary>
    /// May also decide proposals, within the approval policy.
    /// </summary>
    Approver = 30
}

/// <summary>
/// What the application knows about a subject.
/// </summary>
/// <param name="Subject">The subject.</param>
/// <param name="ScopeKey">
/// The non-null isolation key of the subject, see <see cref="CreateScopeKey"/>. Threads,
/// budgets and concurrency are unique per scope key.
/// </param>
public sealed record NhAiSubjectInfo(NhAiSubjectRef Subject, string ScopeKey)
{
    public Guid? DivisionId { get; init; }
    public string? TenantId { get; init; }

    /// <summary>
    /// The current subject version, such as a row version or ETag. Proposals record it, and
    /// a policy that requires an unchanged subject compares it before execution.
    /// </summary>
    public string? Version { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>
    /// The actor id of the person responsible for the subject, used for
    /// <see cref="NhAiAgentAccountability.SubjectResponsible"/>.
    /// </summary>
    public string? ResponsibleActorId { get; init; }

    /// <summary>
    /// Creates the scope key for a tenant or division: <c>tenant:{id}</c> when the tenant
    /// is known, otherwise <c>division:{id}</c>, otherwise <c>global</c>.
    /// </summary>
    public static string CreateScopeKey(string? tenantId, Guid? divisionId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            return $"tenant:{tenantId.Trim()}";
        }

        if (divisionId.HasValue)
        {
            return $"division:{divisionId.Value:N}";
        }

        return "global";
    }
}

/// <summary>
/// The live access of one actor to a subject.
/// </summary>
public sealed record NhAiSubjectAccess(NhAiSubjectRole Role)
{
    public static NhAiSubjectAccess None { get; } = new(NhAiSubjectRole.None);

    /// <summary>
    /// Application-specific role names that approval policies can require, for example
    /// <c>work-planner</c>.
    /// </summary>
    public IReadOnlySet<string> NamedRoles { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool Has(NhAiSubjectRole minimum)
    {
        return Role >= minimum;
    }
}

/// <summary>
/// One member of a subject, for the member snapshot that lists, notifies and pushes live
/// updates.
/// </summary>
public sealed record NhAiSubjectMember(string ActorId, NhAiSubjectRole Role)
{
    public string? DisplayName { get; init; }
    public IReadOnlySet<string> NamedRoles { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Tells the platform who may see and act on the agent threads of one subject type. The
/// platform calls <see cref="ResolveAsync"/> on every request, so access follows the
/// subject without invitations. <see cref="ListMembersAsync"/> feeds the member snapshot
/// that listings, notifications and live updates use.
/// </summary>
public interface INhAiSubjectAccessResolver
{
    /// <summary>
    /// The subject type this resolver handles, for example <c>project</c>.
    /// </summary>
    string SubjectType { get; }

    /// <summary>
    /// Returns the subject's scope, version and responsible person, or
    /// <see langword="null"/> when the subject does not exist.
    /// </summary>
    ValueTask<NhAiSubjectInfo?> DescribeAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current role of <paramref name="actorId"/> on the subject.
    /// </summary>
    ValueTask<NhAiSubjectAccess> ResolveAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists everyone with a role on the subject.
    /// </summary>
    IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Application entry point for subject access changes. Call
/// <see cref="RefreshAsync"/> after the people on a subject change, for example when a
/// project member is added or removed, so listings and notifications follow at once.
/// Authorization never depends on the snapshot.
/// </summary>
/// <summary>
/// A stored version of a file a tool produced in an agent thread.
/// </summary>
public sealed record NhAiArtifactInfo(
    Guid Id,
    string Name,
    int Version,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt);

/// <summary>
/// An artifact with its content.
/// </summary>
public sealed record NhAiArtifact(NhAiArtifactInfo Info, ReadOnlyMemory<byte> Content);

/// <summary>
/// Files tools produce for the agent thread they run in, such as a drafted note or a report.
/// Every save of a name adds a version; the model sees the metadata and tools read the content
/// by id. Use it inside a tool call of an assistant turn; outside one, saving fails.
/// </summary>
public interface INhAiArtifacts
{
    Task<TaskResult<NhAiArtifactInfo>> SaveAsync(
        string name,
        string contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an artifact of the thread of the running tool call, or <see langword="null"/>.
    /// </summary>
    Task<NhAiArtifact?> GetAsync(Guid artifactId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Removes what the AI packages keep about a subject when the application deletes or anonymizes
/// it: agent threads with their messages, proposals, runs, working memory and member snapshot.
/// Call it after the subject's own deletion committed.
/// </summary>
public interface INhAiSubjectRetention
{
    /// <summary>
    /// Cancels the subject's active agent runs and deletes its threads. Returns the number of
    /// deleted threads.
    /// </summary>
    Task<TaskResult<int>> ForgetSubjectAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default);
}

public interface INhAiSubjectAccess
{
    Task<TaskResult> RefreshAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default);
}

public enum NhAiPreconditionOutcome
{
    Satisfied = 0,
    Stale = 1
}

/// <summary>Why a proposal no longer fits. People see a label per kind next to the explanation.</summary>
public enum NhAiStaleKind
{
    /// <summary>What the proposal was based on changed, such as the subject.</summary>
    Changed = 0,

    /// <summary>The proposed work was already done another way, typically by a person.</summary>
    AlreadyDone = 1,

    /// <summary>The proposal no longer applies for another reason.</summary>
    NotApplicable = 2
}

public sealed record NhAiPreconditionResult(NhAiPreconditionOutcome Outcome, string? ReasonCode = null)
{
    public static NhAiPreconditionResult Satisfied { get; } = new(NhAiPreconditionOutcome.Satisfied);

    /// <summary>Why a stale proposal no longer fits.</summary>
    public NhAiStaleKind Kind { get; init; } = NhAiStaleKind.Changed;

    /// <summary>
    /// Explains a stale result to people in a culture, for example "Office task completed by Henk on
    /// 14 Oct". The platform calls it once per supported UI culture, with that culture also set as
    /// the current UI culture, and stores the texts. It must not read or change data.
    /// </summary>
    public Func<CultureInfo, string?>? Explain { get; init; }

    /// <summary>
    /// The actor id of who made the proposal stale, such as the person who completed the task, when
    /// the guard knows it.
    /// </summary>
    public string? ByActorId { get; init; }

    public static NhAiPreconditionResult Stale(string reasonCode)
    {
        return Stale(reasonCode, explain: null);
    }

    /// <summary>What the proposal was based on changed.</summary>
    public static NhAiPreconditionResult Stale(string reasonCode, Func<CultureInfo, string?>? explain)
    {
        return Create(NhAiStaleKind.Changed, reasonCode, explain);
    }

    /// <summary>The proposed work was already done another way.</summary>
    public static NhAiPreconditionResult AlreadyDone(string reasonCode, Func<CultureInfo, string?>? explain = null)
    {
        return Create(NhAiStaleKind.AlreadyDone, reasonCode, explain);
    }

    /// <summary>The proposal no longer applies.</summary>
    public static NhAiPreconditionResult NotApplicable(string reasonCode, Func<CultureInfo, string?>? explain = null)
    {
        return Create(NhAiStaleKind.NotApplicable, reasonCode, explain);
    }

    private static NhAiPreconditionResult Create(NhAiStaleKind kind, string reasonCode, Func<CultureInfo, string?>? explain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new NhAiPreconditionResult(NhAiPreconditionOutcome.Stale, reasonCode)
        {
            Kind = kind,
            Explain = explain
        };
    }
}

/// <summary>
/// The facts a precondition can check before an approved proposal executes.
/// </summary>
public sealed record NhAiProposalPreconditionContext(
    Guid ProposalId,
    string ToolId,
    JsonElement Arguments,
    NhAiSubjectRef Subject,
    string? RecordedSubjectVersion,
    string? CurrentSubjectVersion);

/// <summary>
/// Checks, right before execution, that an approved proposal still fits the subject. A
/// <see cref="NhAiPreconditionOutcome.Stale"/> result closes the proposal as stale without
/// executing it. The default precondition compares the recorded and current subject
/// versions when the approval policy requires an unchanged subject.
/// </summary>
public interface INhAiProposalPrecondition
{
    ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalPreconditionContext context,
        CancellationToken cancellationToken = default);
}

public enum NhAiApprovalMode
{
    /// <summary>
    /// The turn or run waits for the decision and continues with the tool result.
    /// </summary>
    Inline = 0,

    /// <summary>
    /// The model receives a synthetic "proposal recorded" result and continues; the run
    /// ends with a proposal set that people decide later.
    /// </summary>
    Deferred = 1
}

public enum NhAiApprovalExpiryAction
{
    Withdraw = 0,
    Escalate = 1,
    FailRun = 2
}

/// <summary>
/// What an approval stays bound to while it waits.
/// </summary>
public enum NhAiApprovalBinding
{
    /// <summary>
    /// The tool contract plus model profile, prompt, catalog and context hashes. Any agent
    /// update invalidates open proposals.
    /// </summary>
    ToolContractAndPrompt = 0,

    /// <summary>
    /// Only the tool contract. A prompt update keeps open proposals valid.
    /// </summary>
    ToolContract = 1
}

/// <summary>
/// One way to qualify as an approver. A policy accepts an approver who matches any rule.
/// </summary>
public abstract record NhAiApproverRule
{
    private protected NhAiApproverRule()
    {
    }
}

/// <summary>
/// The person who caused the run or turn.
/// </summary>
public sealed record NhAiInitiatorApproverRule : NhAiApproverRule;

/// <summary>
/// Anyone with at least <paramref name="Role"/> on the subject, and the named role when
/// one is given.
/// </summary>
public sealed record NhAiSubjectRoleApproverRule(NhAiSubjectRole Role, string? NamedRole = null) : NhAiApproverRule;

/// <summary>
/// Anyone who satisfies an ASP.NET authorization policy.
/// </summary>
public sealed record NhAiPolicyApproverRule(string PolicyName) : NhAiApproverRule;

public static class NhAiApprovers
{
    public static NhAiApproverRule Initiator { get; } = new NhAiInitiatorApproverRule();

    public static NhAiApproverRule SubjectRole(NhAiSubjectRole role)
    {
        if (role == NhAiSubjectRole.None)
        {
            throw new ArgumentOutOfRangeException(nameof(role), "An approver needs a subject role.");
        }

        return new NhAiSubjectRoleApproverRule(role);
    }

    public static NhAiApproverRule SubjectRole(string namedRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namedRole);
        return new NhAiSubjectRoleApproverRule(NhAiSubjectRole.Reader, namedRole.Trim());
    }

    public static NhAiApproverRule Policy(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        return new NhAiPolicyApproverRule(policyName.Trim());
    }
}

/// <summary>
/// A named approval policy: how long a proposal waits, who decides it, and what happens
/// on expiry. Policies are referenced from an agent, from a tool within an agent and from
/// a trigger; the most specific reference wins.
/// </summary>
/// <summary>
/// What a new proposal does when an open proposal of the same tool changes the same objects with
/// other arguments. An identical open proposal is never recorded twice.
/// </summary>
public enum NhAiDuplicateProposals
{
    /// <summary>The new proposal replaces the pending one, which is withdrawn as superseded.</summary>
    Supersede = 0,

    /// <summary>The open proposal stays and the new one is not recorded.</summary>
    Skip = 1,

    /// <summary>Both proposals stay open.</summary>
    Allow = 2
}

public sealed record NhAiApprovalPolicy
{
    /// <summary>
    /// The longest <see cref="RejectionCooldown"/> a policy may set.
    /// </summary>
    public static readonly TimeSpan MaxRejectionCooldown = TimeSpan.FromDays(90);

    public NhAiApprovalPolicy(string id)
    {
        if (!NhAiSubjectRef.IsValidType(id))
        {
            throw new ArgumentException("An approval policy id is a dash-case identifier.", nameof(id));
        }

        Id = id;
    }

    public string Id { get; }
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(15);
    public IReadOnlyList<NhAiApproverRule> Approvers { get; init; } = [NhAiApprovers.Initiator];

    /// <summary>
    /// The approver must differ from the initiator.
    /// </summary>
    public bool FourEyes { get; init; }

    public NhAiApprovalMode Mode { get; init; } = NhAiApprovalMode.Inline;
    public NhAiApprovalExpiryAction OnExpiry { get; init; } = NhAiApprovalExpiryAction.Withdraw;

    /// <summary>
    /// The policy that applies after <see cref="NhAiApprovalExpiryAction.Escalate"/>.
    /// </summary>
    public string? EscalationPolicyId { get; init; }

    /// <summary>
    /// Offsets after creation at which approvers are reminded.
    /// </summary>
    public IReadOnlyList<TimeSpan> Reminders { get; init; } = [];

    public bool RequireUnchangedSubject { get; init; }
    public NhAiApprovalBinding BindTo { get; init; } = NhAiApprovalBinding.ToolContractAndPrompt;

    /// <summary>
    /// Starts a follow-up run of the agent when a proposal is decided, so it can continue
    /// with the outcome.
    /// </summary>
    public bool StartFollowUpRun { get; init; }

    /// <summary>
    /// What a new proposal of an agent run or consultation does when an open proposal of the same
    /// agent and tool changes the same objects with other arguments. The objects are the keys the
    /// tool's proposal guards name; a tool without keys only knows identical proposals.
    /// </summary>
    public NhAiDuplicateProposals Duplicates { get; init; } = NhAiDuplicateProposals.Supersede;

    /// <summary>
    /// How long an agent does not propose again what people rejected: the same proposal, or one for
    /// the same objects when the tool has keys. <see cref="TimeSpan.Zero"/> turns it off.
    /// </summary>
    public TimeSpan RejectionCooldown { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The policy chat turns use when nothing more specific applies.
    /// </summary>
    public static NhAiApprovalPolicy ChatDefault(TimeSpan lifetime)
    {
        return new NhAiApprovalPolicy("chat-default")
        {
            Lifetime = lifetime
        };
    }

    /// <summary>
    /// The policy trigger runs use when nothing more specific applies.
    /// </summary>
    public static NhAiApprovalPolicy TriggerDefault { get; } = new("trigger-default")
    {
        Lifetime = TimeSpan.FromDays(3),
        Approvers = [NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)],
        Mode = NhAiApprovalMode.Deferred,
        Reminders = [TimeSpan.FromDays(1)],
        RequireUnchangedSubject = true,
        BindTo = NhAiApprovalBinding.ToolContract
    };

    /// <summary>
    /// Validates the policy and returns a failed result that names the first problem.
    /// </summary>
    public TaskResult Validate(TimeSpan maximumLifetime)
    {
        if (Lifetime <= TimeSpan.Zero || Lifetime > maximumLifetime)
        {
            return TaskResult.Failed(nameof(Lifetime), "The approval lifetime must be positive and within the platform maximum.");
        }

        if (Approvers.Count == 0)
        {
            return TaskResult.Failed(nameof(Approvers), "An approval policy needs at least one approver rule.");
        }

        if (OnExpiry == NhAiApprovalExpiryAction.Escalate && string.IsNullOrWhiteSpace(EscalationPolicyId))
        {
            return TaskResult.Failed(nameof(EscalationPolicyId), "Escalation needs an escalation policy.");
        }

        if (Reminders.Any(reminder => reminder <= TimeSpan.Zero || reminder >= Lifetime))
        {
            return TaskResult.Failed(nameof(Reminders), "Reminders must fall within the approval lifetime.");
        }

        if (RejectionCooldown < TimeSpan.Zero || RejectionCooldown > MaxRejectionCooldown)
        {
            return TaskResult.Failed(nameof(RejectionCooldown), "The rejection cooldown must be between zero and 90 days.");
        }

        return TaskResult.Succeeded();
    }
}

/// <summary>
/// Fluent builder for <see cref="NhAiApprovalPolicy"/>.
/// </summary>
public sealed class NhAiApprovalPolicyBuilder
{
    private NhAiApprovalPolicy _policy;

    public NhAiApprovalPolicyBuilder(string id)
    {
        _policy = new NhAiApprovalPolicy(id);
    }

    public NhAiApprovalPolicyBuilder Lifetime(TimeSpan lifetime)
    {
        _policy = _policy with { Lifetime = lifetime };
        return this;
    }

    public NhAiApprovalPolicyBuilder Approvers(params NhAiApproverRule[] approvers)
    {
        ArgumentNullException.ThrowIfNull(approvers);
        _policy = _policy with { Approvers = approvers.ToArray() };
        return this;
    }

    public NhAiApprovalPolicyBuilder FourEyes(bool enabled = true)
    {
        _policy = _policy with { FourEyes = enabled };
        return this;
    }

    public NhAiApprovalPolicyBuilder Mode(NhAiApprovalMode mode)
    {
        _policy = _policy with { Mode = mode };
        return this;
    }

    public NhAiApprovalPolicyBuilder OnExpiry(NhAiApprovalExpiryAction action, string? escalationPolicyId = null)
    {
        _policy = _policy with { OnExpiry = action, EscalationPolicyId = escalationPolicyId };
        return this;
    }

    public NhAiApprovalPolicyBuilder Reminders(params TimeSpan[] offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        _policy = _policy with { Reminders = offsets.Order().ToArray() };
        return this;
    }

    public NhAiApprovalPolicyBuilder RequireUnchangedSubject(bool required = true)
    {
        _policy = _policy with { RequireUnchangedSubject = required };
        return this;
    }

    public NhAiApprovalPolicyBuilder BindTo(NhAiApprovalBinding binding)
    {
        _policy = _policy with { BindTo = binding };
        return this;
    }

    public NhAiApprovalPolicyBuilder StartFollowUpRun(bool enabled = true)
    {
        _policy = _policy with { StartFollowUpRun = enabled };
        return this;
    }

    /// <summary>
    /// What a new proposal for the same objects as an open one does; see
    /// <see cref="NhAiApprovalPolicy.Duplicates"/>.
    /// </summary>
    public NhAiApprovalPolicyBuilder Duplicates(NhAiDuplicateProposals duplicates)
    {
        _policy = _policy with { Duplicates = duplicates };
        return this;
    }

    /// <summary>
    /// How long a rejected proposal is not proposed again; see
    /// <see cref="NhAiApprovalPolicy.RejectionCooldown"/>.
    /// </summary>
    public NhAiApprovalPolicyBuilder RejectionCooldown(TimeSpan cooldown)
    {
        _policy = _policy with { RejectionCooldown = cooldown };
        return this;
    }

    public NhAiApprovalPolicy Build()
    {
        return _policy;
    }
}

/// <summary>
/// Whether an agent grant may cover read tools.
/// </summary>
public enum NhAiElevatedReads
{
    /// <summary>
    /// Agent grants cover reads; results beyond a viewer's own rights are redacted for that
    /// viewer.
    /// </summary>
    Restricted = 0,

    /// <summary>
    /// Agent grants cover only non-read tools. Reads need the initiator's own rights, so an
    /// agent without an initiator cannot read through a grant.
    /// </summary>
    Deny = 1
}

/// <summary>
/// A tool grant in an agent registration: a tool id or a <c>prefix.*</c> selector.
/// </summary>
public sealed record NhAiAgentGrant
{
    public NhAiAgentGrant(string toolSelector, bool triggerOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolSelector);
        if (toolSelector.Length > 128 || toolSelector.Trim() != toolSelector)
        {
            throw new ArgumentException("A tool grant is a tool id or prefix selector of at most 128 characters.", nameof(toolSelector));
        }

        ToolSelector = toolSelector;
        TriggerOnly = triggerOnly;
    }

    public string ToolSelector { get; }

    /// <summary>
    /// The grant applies to runs without a person chatting, never to a person's request
    /// turn.
    /// </summary>
    public bool TriggerOnly { get; }

    public bool Matches(string toolId)
    {
        ArgumentNullException.ThrowIfNull(toolId);
        return string.Equals(ToolSelector, toolId, StringComparison.Ordinal)
               || (ToolSelector.EndsWith(".*", StringComparison.Ordinal)
                   && toolId.StartsWith(ToolSelector[..^1], StringComparison.Ordinal));
    }
}

/// <summary>
/// Describes the agent turn a tool invocation belongs to: which agent, on which subject, for
/// whom and with which grants. The run engine sets it for every agent turn; the agent
/// invocation gate uses it to decide the grant source and to build the invocation context.
/// </summary>
public sealed record NhAiAgentInvocationScope(
    string AgentId,
    string AgentActorId,
    int AgentVersion,
    NhAiSubjectInfo Subject,
    string AccountableOwnerId,
    string RunId,
    string Purpose)
{
    public NhAiAgentAccountability Accountability { get; init; } = NhAiAgentAccountability.Agent;

    /// <summary>
    /// The person who caused the turn, or <see langword="null"/> for a trigger without one.
    /// </summary>
    public string? InitiatorActorId { get; init; }

    public bool DelegateFromInitiator { get; init; }

    /// <summary>
    /// The turn runs inside the initiator's own HTTP request.
    /// </summary>
    public bool IsRequestTurn { get; init; }

    public IReadOnlyList<NhAiAgentGrant> Grants { get; init; } = [];
    public NhAiElevatedReads ElevatedReads { get; init; } = NhAiElevatedReads.Restricted;

    /// <summary>
    /// Every tool effect other than a read needs an approval in this scope, whatever the effect
    /// policy decides; tools that issue or authorize approvals themselves are denied. A consulted
    /// agent runs in such a scope, so it can only propose.
    /// </summary>
    public bool RequireApprovalForEffects { get; init; }

    /// <summary>
    /// The API bridge gateway's reads may run in this headless turn with the rights of
    /// <see cref="GatewayReadActorId"/>, through a short-lived delegated read credential. Set only
    /// for agents that allow gateway reads and answer to a person or technical account.
    /// </summary>
    public bool AllowGatewayReads { get; init; }

    /// <summary>
    /// Whose rights a gateway read of this turn uses: the person who started the run, so a run that
    /// someone asks for never reads more than they may, or the accountable person when nobody did.
    /// <see langword="null"/> when the turn does not allow gateway reads.
    /// </summary>
    public string? GatewayReadActorId => AllowGatewayReads
        ? InitiatorActorId ?? AccountableOwnerId
        : null;

    public int? RunAttemptNumber { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public string? ModelProfileName { get; init; }
    public string? PromptVersion { get; init; }
    public string? PromptHash { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? FencingToken { get; init; }
    public string? ProposalId { get; init; }
    public string? ApprovalId { get; init; }
}

/// <summary>
/// Creates the invocation context of what an agent reads for itself outside a tool call, such as
/// retrieved context: the agent actor, its accountable party, the run and the subject's execution
/// scopes. The ASP.NET integration adds the subject's division as the host maps the active division.
/// </summary>
public interface INhAiAgentInvocationContextFactory
{
    NhAiInvocationContext Create(NhAiAgentInvocationScope scope);
}

/// <summary>
/// The invocation context of an agent's own reads with the subject as its only execution scope.
/// </summary>
internal sealed class NhAiAgentInvocationContextFactory : INhAiAgentInvocationContextFactory
{
    public NhAiInvocationContext Create(NhAiAgentInvocationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new NhAiInvocationContext(scope.AgentActorId, scope.Purpose, new Dictionary<string, string>(StringComparer.Ordinal))
        {
            ActorKind = NhAiActorKind.Agent,
            AgentId = scope.AgentId,
            AccountableOwnerId = scope.AccountableOwnerId,
            TenantId = scope.Subject.TenantId,
            RunId = scope.RunId,
            CorrelationId = scope.CorrelationId ?? scope.RunId,
            ExecutionScopes = [new NhAiExecutionScopeEntry(scope.Subject.Subject.Type, scope.Subject.Subject.Id, scope.Subject.DisplayName)],
            ModelProfileName = scope.ModelProfileName,
            PromptVersion = scope.PromptVersion,
            PromptHash = scope.PromptHash,
            Deadline = scope.Deadline
        };
    }
}

/// <summary>
/// The ambient <see cref="NhAiAgentInvocationScope"/> of the current asynchronous flow.
/// </summary>
public static class NhAiAgentInvocationScopes
{
    private static readonly AsyncLocal<NhAiAgentInvocationScope?> CurrentScope = new();

    public static NhAiAgentInvocationScope? Current => CurrentScope.Value;

    /// <summary>
    /// Makes <paramref name="scope"/> current until the returned handle is disposed.
    /// </summary>
    public static IDisposable Begin(NhAiAgentInvocationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = CurrentScope.Value;
        CurrentScope.Value = scope;
        return new Restore(previous);
    }

    private sealed class Restore(NhAiAgentInvocationScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentScope.Value = previous;
        }
    }
}

/// <summary>
/// Stable failure codes of agent tool authorization.
/// </summary>
public static class NhAiAgentFailureCodes
{
    public const string NotAgentExposed = "ai-tool-not-agent-exposed";
    public const string NotGranted = "ai-tool-agent-not-granted";
    public const string ElevatedReadDenied = "ai-tool-agent-elevated-read-denied";
    public const string InitiatorUnavailable = "ai-tool-initiator-unavailable";
}

/// <summary>
/// Decides which tools an agent turn may use and why. Tool discovery consults it inside an
/// <see cref="NhAiAgentInvocationScope"/>, so an agent is offered exactly the tools its grants
/// and its initiator's rights allow, with or without an HTTP request. The agent invocation gate
/// applies the same decision when the tool is called.
/// </summary>
public interface INhAiAgentToolAuthorizer
{
    /// <summary>
    /// Returns why the agent turn may use <paramref name="descriptor"/>, or
    /// <see cref="NhAiGrantSource.None"/> when it may not.
    /// </summary>
    ValueTask<NhAiGrantSource> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        NhAiAgentInvocationScope scope,
        CancellationToken cancellationToken = default);
}
