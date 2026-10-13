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
    Delegated = 2
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

public sealed record NhAiPreconditionResult(NhAiPreconditionOutcome Outcome, string? ReasonCode = null)
{
    public static NhAiPreconditionResult Satisfied { get; } = new(NhAiPreconditionOutcome.Satisfied);

    public static NhAiPreconditionResult Stale(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new NhAiPreconditionResult(NhAiPreconditionOutcome.Stale, reasonCode);
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
public sealed record NhAiApprovalPolicy
{
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
