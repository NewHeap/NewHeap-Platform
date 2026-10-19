namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Conversation status values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantConversationStatuses
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string WaitingForApproval = "waiting-for-approval";
    public const string Failed = "failed";
    public const string Archived = "archived";
}

/// <summary>
/// Conversation kinds: an interactive chat, or the durable thread of one agent about one subject.
/// </summary>
public static class NhAssistantConversationKinds
{
    public const string Chat = "chat";
    public const string Thread = "thread";
}

/// <summary>
/// Who wrote a user message: a person, or a trigger that started an agent on its subject.
/// </summary>
public static class NhAssistantMessageAuthorKinds
{
    public const string Person = "person";
    public const string Trigger = "trigger";

    /// <summary>
    /// The agent's message about what its rules did, written by a rule run without a model.
    /// </summary>
    public const string Rule = "rule";
}

/// <summary>
/// How an agent run worked.
/// </summary>
/// <summary>
/// The run states of an agent. Active agents work; paused agents start no runs and let their
/// events wait; stopped agents end their work and refuse events. An agent disabled in code with
/// <c>Enabled(false)</c> reports <see cref="ConfiguredOff"/>.
/// </summary>
public static class NhAssistantAgentRunStates
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Stopped = "stopped";
    public const string ConfiguredOff = "configured-off";

    public static bool IsStored(string? state)
    {
        return state is Active or Paused or Stopped;
    }
}

public static class NhAssistantRunKinds
{
    /// <summary>The agent's model ran the turn.</summary>
    public const string Model = "model";

    /// <summary>The agent's rule handler ran the turn without a model.</summary>
    public const string Rule = "rule";

    /// <summary>The rule handler handed the run over to the model.</summary>
    public const string RuleThenModel = "rule-then-model";
}

/// <summary>
/// Who answers for the runs of a thread.
/// </summary>
public static class NhAssistantAccountabilities
{
    public const string Agent = "agent";
    public const string SubjectResponsible = "subject-responsible";
    public const string ServiceAccount = "service-account";

    public static string From(NhAiAgentAccountability accountability)
    {
        return accountability switch
        {
            NhAiAgentAccountability.SubjectResponsible => SubjectResponsible,
            NhAiAgentAccountability.ServiceAccount => ServiceAccount,
            _ => Agent
        };
    }

    public static bool TryParse(string? name, out NhAiAgentAccountability accountability)
    {
        accountability = name switch
        {
            SubjectResponsible => NhAiAgentAccountability.SubjectResponsible,
            ServiceAccount => NhAiAgentAccountability.ServiceAccount,
            _ => NhAiAgentAccountability.Agent
        };
        return name is Agent or SubjectResponsible or ServiceAccount;
    }
}

/// <summary>
/// Run status values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string WaitingForApproval = "waiting-for-approval";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status)
    {
        return status is Completed or Failed or Cancelled;
    }
}

/// <summary>
/// How a run failed. Transient failures are retried; the others are permanent.
/// </summary>
public static class NhAssistantRunFailureClasses
{
    public const string Transient = "transient";
    public const string Budget = "budget";
    public const string Policy = "policy";
    public const string Validation = "validation";
    public const string Stale = "stale";
    public const string Cancelled = "cancelled";
    public const string Internal = "internal";
}

/// <summary>
/// Status values of a consultation record.
/// </summary>
public static class NhAssistantConsultationStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class NhAssistantQueuedInputKinds
{
    public const string Message = "message";
    public const string Event = "event";
}

public static class NhAssistantApprovalModes
{
    public const string Inline = "inline";
    public const string Deferred = "deferred";

    public static string From(NhAiApprovalMode mode)
    {
        return mode == NhAiApprovalMode.Deferred ? Deferred : Inline;
    }
}

/// <summary>
/// Message role values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantMessageRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>
/// The caller's relation to a conversation, exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantParticipantRoles
{
    public const string Owner = "owner";
    public const string Participant = "participant";
}

/// <summary>
/// How a participant joined a shared conversation.
/// </summary>
public static class NhAssistantParticipantSources
{
    /// <summary>
    /// The participant opened the owner's invitation link.
    /// </summary>
    public const string Link = "link";

    /// <summary>
    /// The owner invited the participant through the application's participant directory.
    /// </summary>
    public const string Invitation = "invitation";

    /// <summary>
    /// The person has a role on the subject of an agent thread. The row follows the subject access
    /// resolver: it is created on first use and removed when the role ends.
    /// </summary>
    public const string Subject = "subject";
}

/// <summary>
/// Tool-call status values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantToolCallStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string AwaitingApproval = "awaiting-approval";
    public const string Rejected = "rejected";

    /// <summary>
    /// The deferred proposal of the call closed without running it: it expired, was withdrawn or
    /// became stale. Its approval names the reason.
    /// </summary>
    public const string NotExecuted = "not-executed";
}

/// <summary>
/// Why a stale proposal no longer fits, as exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantStaleKinds
{
    public const string Changed = "changed";

    public const string AlreadyDone = "already-done";

    public const string NotApplicable = "not-applicable";

    public static string From(NhAiStaleKind kind)
    {
        return kind switch
        {
            NhAiStaleKind.AlreadyDone => AlreadyDone,
            NhAiStaleKind.NotApplicable => NotApplicable,
            _ => Changed
        };
    }
}

/// <summary>
/// The moments at which a proposal guard closed a proposal, as recorded in audit events.
/// </summary>
public static class NhAssistantInvalidationStages
{
    public const string Revalidation = "revalidation";

    public const string Decision = "decision";

    public const string Execution = "execution";
}

/// <summary>
/// Approval status values exposed by the assistant HTTP contract.
/// </summary>
public static class NhAssistantApprovalStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";

    /// <summary>
    /// The run was cancelled or the policy withdrew the proposal on expiry.
    /// </summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>
    /// The subject changed after the proposal, so it was closed without executing.
    /// </summary>
    public const string Stale = "stale";

    /// <summary>
    /// An approved deferred proposal is executing.
    /// </summary>
    public const string Executing = "executing";

    public const string Executed = "executed";

    public const string ExecutionFailed = "execution-failed";

    /// <summary>
    /// The worker stopped during a non-idempotent call, so it is unknown whether the effect
    /// happened. An approver re-executes or resolves it.
    /// </summary>
    public const string ExecutionUnknown = "execution-unknown";

    /// <summary>
    /// An approver confirmed the outcome of an uncertain execution.
    /// </summary>
    public const string Resolved = "resolved";

    /// <summary>
    /// Recorded by a shadow run; never notified or executed.
    /// </summary>
    public const string Shadow = "shadow";

    public static bool IsClosed(string status)
    {
        return status is Rejected or Expired or Withdrawn or Stale or Executed or ExecutionFailed or Resolved or Shadow;
    }

    /// <summary>
    /// Whether people may say what was wrong with a proposal in this status: it was rejected, or it
    /// closed without anyone acting on it.
    /// </summary>
    public static bool AcceptsFeedback(string status)
    {
        return status is Rejected or Expired or Withdrawn or Stale;
    }
}

/// <summary>
/// What a feedback record is about. Only agent proposals take feedback today.
/// </summary>
public static class NhAssistantFeedbackTargetKinds
{
    public const string AgentProposal = "agent-proposal";
}

/// <summary>
/// Why a person turned down an agent proposal. Each category belongs to one
/// <see cref="NhAssistantProposalFeedbackGroups">group</see>; only the <c>wrong</c> group says the
/// agent was wrong.
/// </summary>
public static class NhAssistantProposalFeedbackCategories
{
    /// <summary>
    /// The proposal is factually wrong.
    /// </summary>
    public const string Incorrect = "incorrect";

    /// <summary>
    /// The right action with the wrong values.
    /// </summary>
    public const string WrongValue = "wrong-value";

    /// <summary>
    /// The change is unnecessary.
    /// </summary>
    public const string NotNeeded = "not-needed";

    /// <summary>
    /// Right, but not at this moment.
    /// </summary>
    public const string NotNow = "not-now";

    /// <summary>
    /// Already done or in progress elsewhere.
    /// </summary>
    public const string AlreadyDone = "already-done";

    /// <summary>
    /// Against policy, or beyond what the agent should do.
    /// </summary>
    public const string NotAllowed = "not-allowed";

    /// <summary>
    /// Only the text explains.
    /// </summary>
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } =
    [
        Incorrect,
        WrongValue,
        NotNeeded,
        NotNow,
        AlreadyDone,
        NotAllowed,
        Other
    ];

    public static bool IsKnown(string? category)
    {
        return category is not null && All.Contains(category, StringComparer.Ordinal);
    }

    /// <summary>
    /// The group of a known category.
    /// </summary>
    public static string GroupOf(string category)
    {
        return category switch
        {
            Incorrect or WrongValue or NotNeeded => NhAssistantProposalFeedbackGroups.Wrong,
            NotNow or AlreadyDone => NhAssistantProposalFeedbackGroups.RightButNotNow,
            NotAllowed => NhAssistantProposalFeedbackGroups.NotAllowed,
            Other => NhAssistantProposalFeedbackGroups.Other,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown feedback category.")
        };
    }
}

/// <summary>
/// The groups of <see cref="NhAssistantProposalFeedbackCategories"/>. Analysis never counts
/// <see cref="RightButNotNow"/> as a mistake of the agent.
/// </summary>
public static class NhAssistantProposalFeedbackGroups
{
    public const string Wrong = "wrong";
    public const string RightButNotNow = "right-but-not-now";
    public const string NotAllowed = "not-allowed";
    public const string Other = "other";
}

/// <summary>
/// Turn completion status values used by the <c>turn.completed</c> event.
/// </summary>
public static class NhAssistantTurnStatuses
{
    public const string Completed = "completed";
    public const string WaitingForApproval = "waiting-for-approval";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>
/// Idempotency lease status values.
/// </summary>
public static class NhAssistantLeaseStatuses
{
    public const string InProgress = "in-progress";
    public const string Completed = "completed";
}

/// <summary>
/// Stable, content-free assistant failure codes. Front-ends localize them through
/// <c>nh-assistant.errors.&lt;code&gt;</c>.
/// </summary>
public static class NhAssistantErrorCodes
{
    public const string Disabled = "assistant-disabled";
    public const string ConversationNotFound = "assistant-conversation-not-found";
    public const string ConversationBusy = "assistant-conversation-busy";
    public const string AgentNotFound = "assistant-agent-not-found";
    public const string AgentForbidden = "assistant-agent-forbidden";
    public const string MessageInvalid = "assistant-message-invalid";
    public const string MessageTooLong = "assistant-message-too-long";
    public const string MessageDuplicate = "assistant-message-duplicate";
    public const string TitleInvalid = "assistant-title-invalid";
    public const string ApprovalNotFound = "assistant-approval-not-found";
    public const string ApprovalNotPending = "assistant-approval-not-pending";
    public const string ApprovalDecisionInvalid = "assistant-approval-decision-invalid";
    public const string ProposalHashMismatch = "assistant-proposal-hash-mismatch";
    public const string ApprovalExpired = "assistant-approval-expired";
    public const string ApprovalInvalid = "assistant-approval-invalid";
    public const string BudgetExhausted = "assistant-budget-exhausted";
    public const string ToolCallLimitReached = "assistant-tool-call-limit-reached";
    public const string TurnTimeout = "assistant-turn-timeout";
    public const string ModelUnavailable = "assistant-model-unavailable";
    public const string TurnFailed = "assistant-turn-failed";
    public const string ContextUnavailable = "assistant-context-unavailable";

    /// <summary>
    /// Only the owner may share, invite, remove participants or delete the conversation.
    /// </summary>
    public const string OwnerRequired = "assistant-owner-required";

    /// <summary>
    /// The pending approval belongs to the turn of another participant.
    /// </summary>
    public const string ApprovalForbidden = "assistant-approval-forbidden";

    /// <summary>
    /// Only the owner or the participant who started the turn may stop it.
    /// </summary>
    public const string TurnForbidden = "assistant-turn-forbidden";

    /// <summary>
    /// The invitation link is unknown, revoked or replaced.
    /// </summary>
    public const string ShareLinkInvalid = "assistant-share-link-invalid";

    public const string ParticipantNotFound = "assistant-participant-not-found";

    public const string ParticipantLimitReached = "assistant-participant-limit-reached";

    /// <summary>
    /// The application registered no participant directory, so direct invitations are unavailable.
    /// </summary>
    public const string DirectoryUnavailable = "assistant-directory-unavailable";

    /// <summary>
    /// Web Push is not configured on the server.
    /// </summary>
    public const string PushUnavailable = "assistant-push-unavailable";

    /// <summary>
    /// The push subscription has an invalid shape or an endpoint outside the allowed push services.
    /// </summary>
    public const string PushSubscriptionInvalid = "assistant-push-subscription-invalid";

    public const string SubjectNotFound = "assistant-subject-not-found";
    public const string SubjectTypeUnknown = "assistant-subject-type-unknown";
    public const string SubjectAccessDenied = "assistant-subject-access-denied";
    public const string NoAccountableOwner = "assistant-no-accountable-owner";
    public const string AgentDisabled = "assistant-agent-disabled";

    /// <summary>
    /// The agent is stopped: it refuses events, starts and requests until it is resumed.
    /// </summary>
    public const string AgentStopped = "assistant-agent-stopped";

    /// <summary>
    /// The agent is disabled in the application's code; it cannot be resumed at runtime.
    /// </summary>
    public const string AgentConfiguredOff = "assistant-agent-configured-off";

    /// <summary>
    /// A queued run did not start because its agent is paused; its events wait for the resume.
    /// </summary>
    public const string AgentPaused = "assistant-agent-paused";

    /// <summary>
    /// The run used its token ceiling (<c>MaxTokensPerRun</c>) and stopped before the next model
    /// call. Its proposals stay; an approver may resume or restart it.
    /// </summary>
    public const string RunTokenLimit = "assistant-run-token-limit";

    /// <summary>
    /// The skill is stopped: triggers that name it are refused and runs routed to it end.
    /// </summary>
    public const string SkillStopped = "assistant-skill-stopped";

    /// <summary>
    /// The skill is paused: the run's events wait for it to resume.
    /// </summary>
    public const string SkillPaused = "assistant-skill-paused";

    /// <summary>
    /// The agent declares no skill with this id.
    /// </summary>
    public const string SkillNotFound = "assistant-skill-not-found";

    /// <summary>
    /// The application registered no <c>INhActorSearch</c>, so accounts cannot be searched by name.
    /// </summary>
    public const string AccountSearchUnavailable = "assistant-account-search-unavailable";

    /// <summary>
    /// The subject type's resolver does not implement <c>INhAiSubjectSearch</c>, so its subjects are
    /// chosen by id.
    /// </summary>
    public const string SubjectSearchUnavailable = "assistant-subject-search-unavailable";

    /// <summary>
    /// The work went to the agent's shadow thread of the subject: the agent or the skill the run
    /// was routed to works in shadow. A run in the shadow thread takes the run's events.
    /// </summary>
    public const string MovedToShadow = "assistant-moved-to-shadow";

    /// <summary>
    /// A shadow variant always works in shadow; its shadow mode cannot change.
    /// </summary>
    public const string ShadowVariant = "assistant-shadow-variant";

    /// <summary>
    /// The agent does not answer to a technical account, so it has none to change.
    /// </summary>
    public const string ServiceAccountNotUsed = "assistant-service-account-not-used";

    /// <summary>
    /// No account exists with the id given as the agent's technical account.
    /// </summary>
    public const string ServiceAccountNotFound = "assistant-service-account-not-found";

    /// <summary>
    /// The skill is declared in code; only skills defined as data change at runtime.
    /// </summary>
    public const string SkillInCode = "assistant-skill-in-code";

    /// <summary>
    /// The trigger is registered in code; only triggers defined as data change at runtime.
    /// </summary>
    public const string TriggerInCode = "assistant-trigger-in-code";

    /// <summary>
    /// A trigger defined as data names the skill; change or remove the trigger first.
    /// </summary>
    public const string SkillInUse = "assistant-skill-in-use";

    /// <summary>
    /// The agent already has as many skills or triggers defined as data as allowed.
    /// </summary>
    public const string ConfigurationLimit = "assistant-configuration-limit";

    /// <summary>
    /// The agent is registered in code; its runs are configured there.
    /// </summary>
    public const string RunSettingsInCode = "assistant-run-settings-in-code";

    /// <summary>
    /// Skills or triggers defined as data still belong to the agent; remove them first.
    /// </summary>
    public const string RunSettingsInUse = "assistant-run-settings-in-use";

    /// <summary>
    /// The skill has no candidate version to promote, discard or compare.
    /// </summary>
    public const string SkillCandidateNotFound = "assistant-skill-candidate-not-found";

    /// <summary>
    /// The candidate version the run was for was discarded or replaced before it started.
    /// </summary>
    public const string SkillVersionGone = "assistant-skill-version-gone";

    /// <summary>
    /// <c>use_skill</c> named a skill that is not active or is the current one.
    /// </summary>
    public const string SkillNotAvailable = "assistant-skill-not-available";

    /// <summary>
    /// A run or turn already switched skills as often as allowed.
    /// </summary>
    public const string SkillSwitchLimit = "assistant-skill-switch-limit";
    public const string AgentNotConfigured = "assistant-agent-not-configured";
    public const string RunNotFound = "assistant-run-not-found";
    public const string RunNotRetryable = "assistant-run-not-retryable";
    public const string ApprovalPolicyDenied = "assistant-approval-policy-denied";
    public const string FourEyesRequired = "assistant-four-eyes-required";
    public const string ExecutionNotUncertain = "assistant-execution-not-uncertain";
    public const string ProposalStale = "assistant-proposal-stale";

    /// <summary>
    /// A proposal guard failed, so whether the proposal still fits is unknown. The proposal stays
    /// open; deciding it again later runs the check again.
    /// </summary>
    public const string ProposalCheckFailed = "assistant-proposal-check-failed";

    /// <summary>
    /// The same proposal, or a newer one for the same objects, already waits for a decision or its
    /// execution; nothing new was recorded.
    /// </summary>
    public const string ProposalAlreadyOpen = "assistant-proposal-already-open";

    /// <summary>
    /// People rejected the same proposal within the policy's rejection cooldown; nothing was recorded.
    /// </summary>
    public const string ProposalRecentlyRejected = "assistant-proposal-recently-rejected";

    /// <summary>
    /// The result code of a proposal a newer proposal for the same objects replaced.
    /// </summary>
    public const string ProposalSuperseded = "assistant-proposal-superseded";

    /// <summary>
    /// A rule asked to propose with a tool the agent may not use, that does not need approval or
    /// with arguments the tool does not accept.
    /// </summary>
    public const string ProposalRefused = "assistant-proposal-refused";
    public const string WebhookSignatureInvalid = "assistant-webhook-signature-invalid";
    public const string MemoryNotConfigured = "assistant-memory-not-configured";
    public const string MemoryInvalid = "assistant-memory-invalid";
    public const string MemoryConflict = "assistant-memory-conflict";
    public const string ArtifactInvalid = "assistant-artifact-invalid";
    public const string ArtifactOutsideTurn = "assistant-artifact-outside-turn";
    public const string ArtifactNotFound = "assistant-artifact-not-found";
    public const string WebhookPayloadInvalid = "assistant-webhook-payload-invalid";
    public const string TriggerNotFound = "assistant-trigger-not-found";

    /// <summary>
    /// The subject of a consultation does not exist or the asking person has too little access to
    /// it. Both cases share the code, so a consultation cannot probe for subjects.
    /// </summary>
    public const string ConsultSubjectUnavailable = "assistant-consult-subject-unavailable";

    /// <summary>
    /// The consulted agent is disabled, in shadow mode or cannot hold a thread for the subject.
    /// </summary>
    public const string ConsultAgentUnavailable = "assistant-consult-agent-unavailable";

    /// <summary>
    /// The consultation would go deeper than <see cref="NhAssistantLimits.MaxConsultationDepth"/> or
    /// ask an agent that is already part of the chain.
    /// </summary>
    public const string ConsultDepthExceeded = "assistant-consult-depth-exceeded";

    /// <summary>
    /// The consulted agent's daily budget is spent.
    /// </summary>
    public const string ConsultBudgetExhausted = "assistant-consult-budget-exhausted";

    /// <summary>
    /// The asking person reached <see cref="NhAssistantLimits.MaxConsultationsPerActorPerDay"/>.
    /// </summary>
    public const string ConsultLimitReached = "assistant-consult-limit-reached";

    public const string ConsultTimeout = "assistant-consult-timeout";

    public const string ConsultFailed = "assistant-consult-failed";

    /// <summary>
    /// The consulted agent tried a tool that changes something while it may only read.
    /// </summary>
    public const string ConsultReadOnly = "assistant-consult-read-only";

    /// <summary>
    /// A replay targets an agent that is not in shadow mode, or replays an agent into itself.
    /// </summary>
    public const string ReplayNotShadow = "assistant-replay-not-shadow";

    /// <summary>
    /// The variant already has a replay that runs.
    /// </summary>
    public const string ReplayActive = "assistant-replay-active";

    /// <summary>
    /// The evaluation compares an agent with an agent that is not its shadow variant.
    /// </summary>
    public const string EvaluationNotVariant = "assistant-evaluation-not-variant";

    /// <summary>
    /// A hand-off was asked for outside an agent run, such as in a person's chat.
    /// </summary>
    public const string HandOffUnavailable = "assistant-handoff-unavailable";

    /// <summary>
    /// The target agent does not accept hand-offs about the subject type from the source agent.
    /// </summary>
    public const string HandOffNotAccepted = "assistant-handoff-not-accepted";

    /// <summary>
    /// The subject does not exist or belongs to another scope key (tenant or division) than the
    /// source's subject. Both cases share the code, so a hand-off cannot probe for subjects.
    /// </summary>
    public const string HandOffSubjectUnavailable = "assistant-handoff-subject-unavailable";

    /// <summary>
    /// A shadow run tried to hand work to an agent that is not in shadow mode.
    /// </summary>
    public const string HandOffShadowToLive = "assistant-handoff-shadow-to-live";

    /// <summary>
    /// The chain of hand-offs would grow beyond <see cref="NhAssistantLimits.MaxHandOffDepth"/> or
    /// return to an agent that is already in it.
    /// </summary>
    public const string HandOffChainLimit = "assistant-handoff-chain-limit";

    /// <summary>
    /// The source reached the target's daily limit of hand-offs about the subject.
    /// </summary>
    public const string HandOffLimitReached = "assistant-handoff-limit-reached";

    /// <summary>
    /// The feedback has an unknown category or a text longer than
    /// <see cref="NhAssistantLimits.MaxFeedbackCharacters"/>.
    /// </summary>
    public const string FeedbackInvalid = "assistant-feedback-invalid";

    /// <summary>
    /// The proposal takes no feedback in its status: it is still pending, or it was approved.
    /// </summary>
    public const string FeedbackNotAccepted = "assistant-feedback-not-accepted";

    /// <summary>
    /// Creates the localization key for a code.
    /// </summary>
    public static string MessageKey(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return "nh-assistant.errors." + code;
    }
}

/// <summary>
/// Agent source values: from code (<c>AddAgent</c>) or created by an administrator.
/// </summary>
public static class NhAssistantAgentSources
{
    public const string Code = "code";
    public const string Admin = "admin";
}

public static class NhAssistantApplicationContexts
{
    public const string DefaultId = "default";
    public const int MaxTextLength = 20_000;
}

public static class NhAssistantMcpAuthModes
{
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string ApiKeyHeader = "api-key";
    public const string ForwardUserToken = "forward-user-token";

    public static bool IsValid(string? value)
    {
        return value is None or Bearer or ApiKeyHeader or ForwardUserToken;
    }
}

public static class NhAssistantMcpToolEffects
{
    public const string ReadOnly = "read-only";
    public const string Mutation = "mutation";
}

public static class NhAssistantMcpToolStatuses
{
    public const string Available = "available";
    public const string SchemaChanged = "schema-changed";
    public const string Missing = "missing";
}

public static class NhAssistantStyles
{
    public const string Default = "default";
    public const string Direct = "direct";
    public const string Personal = "personal";
    public const string Detailed = "detailed";

    public static bool IsValid(string? value)
    {
        return value is Default or Direct or Personal or Detailed;
    }
}

public static class NhAssistantAddressForms
{
    public const string Informal = "informal";
    public const string Formal = "formal";

    public static bool IsValid(string? value)
    {
        return value is Informal or Formal;
    }
}

public static class NhAssistantResponseLengths
{
    public const string Short = "short";
    public const string Normal = "normal";
    public const string Long = "long";

    public static bool IsValid(string? value)
    {
        return value is Short or Normal or Long;
    }
}

/// <summary>
/// Stable, content-free failure codes of the administration and MCP surface.
/// </summary>
public static class NhAssistantAdminErrorCodes
{
    public const string VersionConflict = "assistant-version-conflict";
    /// <summary>
    /// Invalid input; the error body lists field errors in <c>errors</c> with camelCase keys.
    /// </summary>
    public const string ValidationFailed = "assistant-validation";
    public const string Forbidden = "assistant-forbidden";
    public const string NotFound = "assistant-not-found";
    public const string AgentExists = "assistant-agent-exists";
    public const string CodeAgentNotDeletable = "assistant-code-agent-not-deletable";
    public const string NotCodeAgent = "assistant-agent-not-code";
    public const string McpServerNotFound = "assistant-mcp-server-not-found";
    public const string McpServerExists = "assistant-mcp-server-exists";
    public const string McpToolNotFound = "assistant-mcp-tool-not-found";
    public const string McpUnreachable = "assistant-mcp-unreachable";
    public const string McpUnauthorized = "assistant-mcp-unauthorized";
    public const string McpHostBlocked = "assistant-mcp-host-blocked";
    public const string McpSchemaChanged = "assistant-mcp-schema-changed";
    public const string McpToolDisabled = "assistant-mcp-tool-disabled";
    public const string InstructionsTooLong = "assistant-instructions-too-long";
    public const string ContextNotFound = "assistant-context-not-found";
}

/// <summary>
/// Stable, content-free field error values in the <c>errors</c> object of an
/// <c>assistant-validation</c> response.
/// </summary>
public static class NhAssistantFieldErrors
{
    public const string Required = "required";
    public const string Invalid = "invalid";
    public const string TooLong = "too-long";
    public const string NotFound = "not-found";
}

/// <summary>
/// Collects field errors (camelCase field names) and turns them into one
/// <c>assistant-validation</c> failure. Field errors travel as result items named
/// <c>field:&lt;name&gt;</c> after the code item.
/// </summary>
internal sealed class NhAssistantValidationErrors
{
    public const string FieldPrefix = "field:";

    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsEmpty => _errors.Count == 0;

    public NhAssistantValidationErrors Add(string field, string error)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }
        if (!list.Contains(error, StringComparer.Ordinal))
        {
            list.Add(error);
        }
        return this;
    }

    public NhAssistantValidationErrors Require(bool valid, string field, string error)
    {
        return valid ? this : Add(field, error);
    }

    public NewHeap.Platform.Common.Models.TaskResult ToResult()
    {
        var result = NewHeap.Platform.Common.Models.TaskResult.Failed(
            NhAssistantAdminErrorCodes.ValidationFailed,
            "The input is invalid.");
        foreach (var (field, errors) in _errors)
        {
            result.AddError(FieldPrefix + field, errors.ToArray());
        }
        return result;
    }

    public static NewHeap.Platform.Common.Models.TaskResult Failed(string field, string error)
    {
        return new NhAssistantValidationErrors().Add(field, error).ToResult();
    }
}
