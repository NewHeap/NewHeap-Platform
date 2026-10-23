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

    /// <summary>
    /// A message the application posted with <see cref="INhAssistantConversationMessages"/>; its role
    /// is <see cref="NhAssistantMessageRoles.Application"/>.
    /// </summary>
    public const string Application = "application";
}

/// <summary>
/// How an agent run worked.
/// </summary>
/// <summary>
/// The run states of an agent. Active agents work; paused agents start no runs and let their
/// events wait; stopped agents end their work and refuse events; agents that are off ignore their
/// triggers while people still start and chat with them. An agent disabled in code with
/// <c>Enabled(false)</c> reports <see cref="ConfiguredOff"/>.
/// </summary>
public static class NhAssistantAgentRunStates
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Stopped = "stopped";

    /// <summary>
    /// The agent or skill is switched off: events, schedules, webhooks, hand-offs and follow-ups
    /// are ignored without being queued, while running runs finish, open proposals stay decidable
    /// and people still chat with the agent and start it by hand. Switching it on replays nothing.
    /// <c>StartsOff()</c> in code makes an agent or skill start in this state the first time the
    /// application sees it; an administrator switches it on and off at runtime.
    /// </summary>
    public const string Off = "off";

    public const string ConfiguredOff = "configured-off";

    public static bool IsStored(string? state)
    {
        return state is Active or Paused or Stopped or Off;
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

/// <summary>
/// How a proposal of a consultation is decided and whose rights execute it. The route is derived
/// from the stored proposal: its consultation, its mode and its grant source.
/// </summary>
public static class NhAssistantConsultationRoutes
{
    /// <summary>
    /// The person who asked decides it in their own conversation, and it executes in their own
    /// request with their own rights (<c>AskerDecides()</c>).
    /// </summary>
    public const string AskerDecides = "asker-decides";

    /// <summary>
    /// The subject's approvers decide it, and it executes later with the asker's rights
    /// (<c>AtSubject(NhAssistantConsultationRights.Asker)</c>, the route of <c>Proposals(true)</c>).
    /// </summary>
    public const string SubjectAskerRights = "subject-asker-rights";

    /// <summary>
    /// The subject's approvers other than the asker decide it, and it executes with the consulted
    /// agent's own rights (<c>AtSubject(NhAssistantConsultationRights.Agent)</c>).
    /// </summary>
    public const string SubjectAgentRights = "subject-agent-rights";

    /// <summary>
    /// The route of a stored proposal, or <see langword="null"/> for a proposal that no consultation
    /// left.
    /// </summary>
    public static string? Of(Guid? consultationId, string mode, NhAiGrantSource grantSource)
    {
        if (consultationId is null)
        {
            return null;
        }
        if (string.Equals(mode, NhAssistantApprovalModes.Inline, StringComparison.Ordinal))
        {
            return AskerDecides;
        }

        return grantSource is NhAiGrantSource.Agent or NhAiGrantSource.Accountable
            ? SubjectAgentRights
            : SubjectAskerRights;
    }
}

/// <summary>
/// Why a call of a consulted agent that changes something was not proposed, as the consulted model
/// and the audit read it next to <see cref="NhAssistantErrorCodes.ConsultProposalRefused"/>.
/// </summary>
public static class NhAssistantConsultationRefusalReasons
{
    /// <summary>The consultation has no route that is configured and switched on.</summary>
    public const string NoRoute = "no-route";

    /// <summary>The approval policy keeps the person who caused it out (four-eyes).</summary>
    public const string FourEyes = "four-eyes";

    /// <summary>The asking person is no approver of the call under its approval policy.</summary>
    public const string AskerNotApprover = "asker-not-approver";

    /// <summary>
    /// Nobody waits for a decision: the asking turn is no person's turn in their own request, such as
    /// an MCP call, a headless agent run or a nested consultation.
    /// </summary>
    public const string AskerNotWaiting = "asker-not-waiting";

    /// <summary>A decision of the asking person already waits in the asking turn or consultation.</summary>
    public const string DecisionWaiting = "decision-waiting";

    /// <summary>The consulted agent answers to the subject's responsible person.</summary>
    public const string AgentAnswersToPerson = "agent-answers-to-person";

    /// <summary>The subject is the whole application.</summary>
    public const string WholeApplication = "whole-application";

    /// <summary>The approval policy names no approver other than the person who caused it.</summary>
    public const string NoApprover = "no-approver";

    /// <summary>The consulted agent's own rights do not allow the call.</summary>
    public const string AgentNotGranted = "agent-not-granted";

    /// <summary>
    /// The asking person already has <see cref="NhAssistantLimits.MaxOpenConsultationProposalsPerAsker"/>
    /// open proposals at the subject that execute with the agent's rights.
    /// </summary>
    public const string OpenLimit = "open-limit";
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

    /// <summary>
    /// A message the application posted into the conversation with
    /// <see cref="INhAssistantConversationMessages"/>, such as the result of a long-running export.
    /// People see it as the application's; a later turn gives it to the model as information from
    /// the application, never as a person's request or as instructions.
    /// </summary>
    public const string Application = "application";
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
/// Why a run's proposal for another object was not placed at that object, as the tool result of
/// <see cref="NhAssistantErrorCodes.ProposalNotPlaced"/> names it for the model and the audit records it.
/// </summary>
public static class NhAssistantPlacementReasons
{
    /// <summary>
    /// The agent does not work on the object's type, or not in its scope: a per-division agent places
    /// only in its own division.
    /// </summary>
    public const string DoesNotWorkOn = "does-not-work-on";

    /// <summary>
    /// The object does not exist, or the call names no object the platform can confirm.
    /// </summary>
    public const string NotFound = "not-found";

    /// <summary>
    /// The account the run answers to may not read the object.
    /// </summary>
    public const string NoAccess = "no-access";

    /// <summary>
    /// Nobody answers for the agent at the object, for example an agent that answers to the
    /// subject's responsible person at an object without one, so the proposal cannot wait there.
    /// </summary>
    public const string NoAccountableOwner = "no-accountable-owner";

    /// <summary>
    /// The run placed <see cref="NhAssistantLimits.MaxPlacedProposalsPerRun"/> proposals already.
    /// </summary>
    public const string Limit = "limit";
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

    /// <summary>
    /// Neither the turn's model profile nor any of its fallbacks permits the data classification of
    /// the agent or its skill (<c>DataClassification(...)</c>). The turn fails at once, and a run is
    /// not retried: another attempt would be refused the same way.
    /// </summary>
    public const string ClassificationNotPermitted = "assistant-classification-not-permitted";

    /// <summary>
    /// The application registered no file source (<c>UseFileSource</c>), so turns take no files.
    /// </summary>
    public const string FilesUnavailable = "assistant-files-unavailable";

    /// <summary>
    /// The agent, or the skill the turn works in, does not accept files: it was not switched on with
    /// <c>AcceptsFiles()</c> in code or in the administration.
    /// </summary>
    public const string FilesNotAccepted = "assistant-files-not-accepted";

    /// <summary>
    /// A tool result or a message names a file with an invalid reference.
    /// </summary>
    public const string FileReferenceInvalid = "assistant-file-reference-invalid";

    /// <summary>
    /// The application's file source does not know the file, or no longer has it.
    /// </summary>
    public const string FileNotFound = "assistant-file-not-found";

    /// <summary>
    /// The actor of the turn may not read the file.
    /// </summary>
    public const string FileForbidden = "assistant-file-forbidden";

    /// <summary>
    /// The application's file source could not open the file for another reason.
    /// </summary>
    public const string FileUnavailable = "assistant-file-unavailable";

    /// <summary>
    /// The reading tool that received the file, a reader of the application or an MCP server set up
    /// as reader, failed or returned no text. No other reader is asked for the same file.
    /// </summary>
    public const string FileReaderFailed = "assistant-file-reader-failed";

    /// <summary>
    /// The file is larger than <see cref="NhAssistantLimits.MaxFileBytes"/> or the lower limit of the
    /// agent or skill.
    /// </summary>
    public const string FileTooLarge = "assistant-file-too-large";

    /// <summary>
    /// The file has more pages than <see cref="NhAssistantLimits.MaxFilePages"/> or the lower limit of
    /// the agent or skill.
    /// </summary>
    public const string FileTooManyPages = "assistant-file-too-many-pages";

    /// <summary>
    /// The turn would read more files than <see cref="NhAssistantLimits.MaxFilesPerTurn"/> or the
    /// lower limit of the agent or skill.
    /// </summary>
    public const string FileCountLimit = "assistant-file-count-limit";

    /// <summary>
    /// The turn's files would exceed <see cref="NhAssistantLimits.MaxFileTokensPerTurn"/>, the lower
    /// limit of the agent or skill, or the input budget of its model profile.
    /// </summary>
    public const string FileTokenLimit = "assistant-file-token-limit";

    /// <summary>
    /// The model profile that serves the turn does not read files of this media type
    /// (<c>AcceptMediaTypes</c>). The refusal names the type.
    /// </summary>
    public const string FileTypeNotSupported = "assistant-file-type-not-supported";

    /// <summary>
    /// An upload is not one file part named <c>file</c> with a name and content, or lacks the upload
    /// request header.
    /// </summary>
    public const string FileUploadInvalid = "assistant-file-upload-invalid";

    /// <summary>
    /// People may not upload files of this media type (<see cref="NhAssistantUploadOptions.AcceptedMediaTypes"/>),
    /// or the content does not match the media type the upload declares.
    /// </summary>
    public const string FileTypeNotAccepted = "assistant-file-type-not-accepted";

    /// <summary>
    /// The application's file source refused to store an upload.
    /// </summary>
    public const string FileUploadRefused = "assistant-file-upload-refused";

    /// <summary>
    /// The application's file source could not store an upload right now, or its upload scanner
    /// (<see cref="INhAssistantUploadScanner"/>) could not scan it.
    /// </summary>
    public const string FileUploadFailed = "assistant-file-upload-failed";

    /// <summary>
    /// The application's upload scanner (<see cref="INhAssistantUploadScanner"/>) refused the upload,
    /// for example because it found malware. Nothing was stored.
    /// </summary>
    public const string FileScanRefused = "assistant-file-scan-refused";

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

    /// <summary>
    /// A request names a subject that cannot exist: an invalid subject type, or an id that is blank,
    /// too long or surrounded by white space. An unknown or unreadable subject is
    /// <see cref="SubjectNotFound"/>.
    /// </summary>
    public const string SubjectInvalid = "assistant-subject-invalid";
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
    /// The agent is switched off: it takes no hand-offs and its replays stop. Its triggers are
    /// ignored with the reason <c>agent-off</c> instead.
    /// </summary>
    public const string AgentOff = "assistant-agent-off";

    /// <summary>
    /// The skill is switched off: a run that rules route to it ends without working in it. Triggers
    /// that name it are ignored with the reason <c>skill-off</c> instead.
    /// </summary>
    public const string SkillOff = "assistant-skill-off";

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
    /// The agent does not work on subjects of this type: a trigger, a manual start, a hand-off, a
    /// consultation or a request named a type its <c>WorksOn</c>, or what an administrator stored,
    /// does not list. The refused start is audited with this code.
    /// </summary>
    public const string AgentDoesNotWorkOn = "assistant-agent-does-not-work-on";

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
    /// A route of the agent's consultations was switched on that no consultation registration in code
    /// allows, or the agent allows no consultation at all: the administration switches routes only
    /// within the ceiling in code.
    /// </summary>
    public const string ConsultRouteNotAllowed = "assistant-consult-route-not-allowed";

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
    /// A data trigger selects a filter that its trigger event does not register with <c>Filter</c>;
    /// the field error <c>filters: not-found</c> goes with it. Choose from the filters the run
    /// catalog lists for the event.
    /// </summary>
    public const string TriggerFilterNotFound = "assistant-trigger-filter-not-found";

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
    /// Skills, triggers or consultations defined as data still belong to the agent; remove them first.
    /// </summary>
    public const string RunSettingsInUse = "assistant-run-settings-in-use";

    /// <summary>
    /// The agent allows consultation about this subject type in code (<c>AllowConsultation</c>); a
    /// consultation in code does not change as data.
    /// </summary>
    public const string ConsultationInCode = "assistant-consultation-in-code";

    /// <summary>
    /// The agent has no consultation defined as data about this subject type.
    /// </summary>
    public const string ConsultationNotFound = "assistant-consultation-not-found";

    /// <summary>
    /// The agent may not get consultations defined as data: the application did not call
    /// <c>UseConsultationsAsData</c>, or the agent is registered in code without
    /// <c>ConsultableAsData</c>.
    /// </summary>
    public const string ConsultationsNotAllowed = "assistant-consultations-not-allowed";

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

    /// <summary>
    /// The run's background operation was cancelled before the run ended, for example by an operator
    /// in the background-operation administration or by a superseding trigger. The maintenance job
    /// ends such a run as cancelled.
    /// </summary>
    public const string RunOperationCancelled = "assistant-run-operation-cancelled";

    /// <summary>
    /// No worker started the run's background operation, which failed as <c>dispatch-stalled</c>
    /// before the run ended. The maintenance job fails such a run.
    /// </summary>
    public const string RunOperationStalled = "assistant-run-operation-stalled";

    /// <summary>
    /// The run's background operation ended without ending the run in any other way, for example a
    /// payload schema mismatch, a redacted payload, a timeout or retries that ran out. The maintenance
    /// job fails such a run.
    /// </summary>
    public const string RunOperationEnded = "assistant-run-operation-ended";

    /// <summary>
    /// A run history was asked with an invalid filter: an unknown status, an invalid subject type, a
    /// subject id without its type, a period that ends before it starts, or a page out of range.
    /// </summary>
    public const string RunFilterInvalid = "assistant-run-filter-invalid";

    /// <summary>
    /// The board of a group of agents was not found: the group is not registered, or the person sees
    /// none of its agents in the asked scope, or may not work in the asked division.
    /// </summary>
    public const string BoardNotFound = "assistant-board-not-found";

    /// <summary>
    /// A board was asked for a division id that is not a division id.
    /// </summary>
    public const string BoardScopeInvalid = "assistant-board-scope-invalid";
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

    /// <summary>
    /// The result of a run's tool call whose proposal concerns another object than the run's own: the
    /// proposal waits in the agent's thread at that object, where the object's approvers decide. The
    /// call itself did not execute.
    /// </summary>
    public const string ProposalPlaced = "assistant-proposal-placed";

    /// <summary>
    /// A run's tool call concerns another object, and the proposal was not placed there; the result
    /// names the reason, one of <see cref="NhAssistantPlacementReasons"/>. The run continues.
    /// </summary>
    public const string ProposalNotPlaced = "assistant-proposal-not-placed";

    /// <summary>
    /// A proposal guard that checks at proposal time found the proposal not needed: the work is
    /// already done, no longer applies or what it is based on changed. Nothing was proposed; the
    /// result carries the guard's reason and explanation.
    /// </summary>
    public const string ProposalNotNeeded = "assistant-proposal-not-needed";
    public const string WebhookSignatureInvalid = "assistant-webhook-signature-invalid";
    public const string MemoryNotConfigured = "assistant-memory-not-configured";
    public const string MemoryInvalid = "assistant-memory-invalid";
    public const string MemoryConflict = "assistant-memory-conflict";
    public const string ArtifactInvalid = "assistant-artifact-invalid";
    public const string ArtifactOutsideTurn = "assistant-artifact-outside-turn";
    public const string ArtifactNotFound = "assistant-artifact-not-found";
    public const string WebhookPayloadInvalid = "assistant-webhook-payload-invalid";

    /// <summary>
    /// The body of a webhook call is larger than the webhook accepts.
    /// </summary>
    public const string WebhookPayloadTooLarge = "assistant-webhook-payload-too-large";
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
    /// No route of the consultation applies to a call of the consulted agent that changes something,
    /// so nothing was proposed; the result's <c>reason</c> is one of
    /// <see cref="NhAssistantConsultationRefusalReasons"/>. A request to leave a proposal for the
    /// subject's approvers fails with it when no route at the subject applies.
    /// </summary>
    public const string ConsultProposalRefused = "assistant-consult-proposal-refused";

    /// <summary>
    /// The person who asked decides this consultation proposal in their own conversation; nobody
    /// else decides it, and it is not decided through <c>approvals/{id}/decisions</c>.
    /// </summary>
    public const string ConsultAskerDecides = "assistant-consult-asker-decides";

    /// <summary>
    /// The outcome of a proposal the person who asked left for the subject's approvers: it closed
    /// and a new proposal at the subject replaced it.
    /// </summary>
    public const string ConsultProposalMoved = "assistant-consult-proposal-moved";

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

    /// <summary>
    /// HTTP basic authentication with a user name and a password (the server's secret).
    /// </summary>
    public const string Basic = "basic";

    /// <summary>
    /// OAuth 2.1 authorization code with PKCE and refresh tokens, against the authorization server
    /// that an administrator confirmed. The connection belongs to the organization or to each person
    /// (<see cref="NhAssistantMcpConnectionScopes"/>).
    /// </summary>
    public const string OAuth = "oauth";

    /// <summary>
    /// OAuth client credentials of a pre-registered client: one token of the organization for all
    /// callers, without a browser.
    /// </summary>
    public const string OAuthClientCredentials = "oauth-client-credentials";

    public static bool IsValid(string? value)
    {
        return value is None or Bearer or ApiKeyHeader or ForwardUserToken or Basic or OAuth or OAuthClientCredentials;
    }

    /// <summary>
    /// Whether the mode obtains its tokens from an OAuth authorization server.
    /// </summary>
    public static bool IsOAuth(string? value)
    {
        return value is OAuth or OAuthClientCredentials;
    }

    /// <summary>
    /// Whether the mode sends a secret: the stored secret or its reference.
    /// </summary>
    public static bool UsesSecret(string? value)
    {
        return value is Bearer or ApiKeyHeader or Basic;
    }

    /// <summary>
    /// Whether every caller of the server shares one credential that belongs to the organization,
    /// rather than each person bringing their own. For <see cref="OAuth"/> this depends on the
    /// connection scope; use the overload with it.
    /// </summary>
    public static bool IsOrganizationCredential(string? value)
    {
        return value is None or Bearer or ApiKeyHeader or Basic or OAuthClientCredentials;
    }

    /// <summary>
    /// Whether every caller of a server with this mode and connection scope shares one credential
    /// of the organization.
    /// </summary>
    public static bool IsOrganizationCredential(string? value, string? connectionScope)
    {
        return IsOrganizationCredential(value)
            || (value == OAuth && connectionScope != NhAssistantMcpConnectionScopes.User);
    }
}

/// <summary>
/// Whether one OAuth connection serves everyone or each person connects their own account.
/// </summary>
public static class NhAssistantMcpConnectionScopes
{
    /// <summary>One connection of the organization, made by an administrator. The default.</summary>
    public const string Organization = "organization";

    /// <summary>Each person connects their own account.</summary>
    public const string User = "user";

    public static bool IsValid(string? value)
    {
        return value is Organization or User;
    }
}

/// <summary>
/// The state of an MCP connection with OAuth tokens.
/// </summary>
public static class NhAssistantMcpConnectionStatuses
{
    public const string Active = "active";

    /// <summary>The tokens can no longer be renewed; the owner connects again.</summary>
    public const string ReauthorizationRequired = "reauthorization-required";

    /// <summary>The server named another authorization server; an administrator confirms it first.</summary>
    public const string IssuerChanged = "issuer-changed";
}

/// <summary>
/// Why a turn asks the person to connect a server that each person connects to with OAuth: the
/// <c>reason</c> of the <c>connection-required</c> event and message part.
/// </summary>
public static class NhAssistantMcpConnectionReasons
{
    /// <summary>The person has not connected the server.</summary>
    public const string NotConnected = "not-connected";

    /// <summary>The person's connection ended; connecting again restores it.</summary>
    public const string ReauthorizationRequired = "reauthorization-required";

    /// <summary>The server asked for permissions the connection lacks; connecting again adds them.</summary>
    public const string InsufficientScope = "insufficient-scope";
}

/// <summary>
/// Whether the tools of an MCP server are offered in turns that no person's request carries:
/// headless agent runs and the execution of their deferred proposals. A person's chat turn and a
/// person typing in an agent thread are not affected.
/// </summary>
public static class NhAssistantMcpHeadlessUses
{
    /// <summary>
    /// Never in headless turns. The default for every server.
    /// </summary>
    public const string Never = "never";

    /// <summary>
    /// In headless turns with the server's organization credential. Only for servers whose
    /// credential belongs to the organization (<c>none</c>, <c>bearer</c>, <c>api-key</c> and
    /// <c>basic</c>). The tools then also accept a code grant of the agent registration
    /// (<c>mcp.&lt;server&gt;.*</c>).
    /// </summary>
    public const string Organization = "organization";

    /// <summary>
    /// In headless turns with the credential of the person the run acts for, only when that person
    /// consented to background use of the server. For <c>forward-user-token</c>: a short-lived
    /// delegated NewHeap token for the person, sent only to hosts in <c>ForwardUserTokenHosts</c>.
    /// </summary>
    public const string Personal = "personal";

    public static bool IsValid(string? value)
    {
        return value is Never or Organization or Personal;
    }

    /// <summary>
    /// Whether the headless use fits the authentication mode of the server.
    /// </summary>
    public static bool FitsAuthMode(string headlessUse, string authMode)
    {
        return FitsAuthMode(headlessUse, authMode, NhAssistantMcpConnectionScopes.Organization);
    }

    /// <summary>
    /// Whether the headless use fits the authentication mode and connection scope of the server:
    /// <see cref="Organization"/> needs a credential of the organization, <see cref="Personal"/> a
    /// forwarded token or a personal OAuth connection.
    /// </summary>
    public static bool FitsAuthMode(string headlessUse, string authMode, string? connectionScope)
    {
        if (headlessUse == Never)
        {
            return true;
        }
        if (headlessUse == Organization)
        {
            return NhAssistantMcpAuthModes.IsOrganizationCredential(authMode, connectionScope);
        }
        return headlessUse == Personal
            && (authMode == NhAssistantMcpAuthModes.ForwardUserToken
                || (authMode == NhAssistantMcpAuthModes.OAuth && connectionScope == NhAssistantMcpConnectionScopes.User));
    }
}

/// <summary>
/// How the assistant talks to an MCP server.
/// </summary>
public static class NhAssistantMcpTransports
{
    /// <summary>
    /// Streamable HTTP only. The default.
    /// </summary>
    public const string StreamableHttp = "streamable-http";

    /// <summary>
    /// Streamable HTTP first, then the deprecated HTTP+SSE transport for older servers. Every request,
    /// including the one to the SSE <c>endpoint</c> event, stays on the configured origin.
    /// </summary>
    public const string Auto = "auto";

    public static bool IsValid(string? value)
    {
        return value is StreamableHttp or Auto;
    }
}

/// <summary>
/// Who an MCP connection record belongs to.
/// </summary>
public static class NhAssistantMcpConnectionOwners
{
    public const string Organization = "organization";

    public const string User = "user";

    /// <summary>
    /// <see cref="NhAssistantAuditEvent.McpConnectionOwner"/> of a call to a server without a
    /// credential.
    /// </summary>
    public const string None = "none";
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

    /// <summary>
    /// The MCP server answered <c>403</c> with <c>error="insufficient_scope"</c>: the credential is
    /// valid but lacks a permission the call needs.
    /// </summary>
    public const string McpInsufficientScope = "assistant-mcp-insufficient-scope";

    /// <summary>
    /// The MCP server's tools are not offered in headless turns (its headless use is <c>never</c>,
    /// or a person's credential is needed).
    /// </summary>
    public const string McpHeadlessNotAllowed = "assistant-mcp-headless-not-allowed";

    /// <summary>
    /// The call was authorized for someone other than the person whose credential the connection
    /// carries, so the request was not sent.
    /// </summary>
    public const string McpCredentialMismatch = "assistant-mcp-credential-mismatch";

    /// <summary>
    /// A headless run needs the person's consent to background use of the server, and there is none
    /// for the server as it is configured now.
    /// </summary>
    public const string McpConsentRequired = "assistant-mcp-consent-required";

    /// <summary>
    /// A person asked for the run's turn (started it by hand, asked through another assistant, or
    /// typed a message the run answers) and the turn acts for someone else, such as the account the
    /// agent answers to; it may use only the asking person's own connection to a server with personal
    /// background use, never another person's.
    /// </summary>
    public const string McpInitiatorOnly = "assistant-mcp-initiator-only";

    /// <summary>
    /// No delegated token could be issued for the person, for example because the account may not
    /// sign in, or because the host does not restrict delegated tokens in every bearer scheme.
    /// </summary>
    public const string McpDelegatedTokenUnavailable = "assistant-mcp-delegated-token-unavailable";

    /// <summary>
    /// The secret reference does not name a registered source, or the source has no value for it.
    /// </summary>
    public const string McpSecretReferenceInvalid = "assistant-mcp-secret-reference-invalid";

    /// <summary>
    /// The client certificate reference names no configured certificate with a private key.
    /// </summary>
    public const string McpCertificateReferenceInvalid = "assistant-mcp-certificate-reference-invalid";

    /// <summary>The caller has no active connection to a server that needs one.</summary>
    public const string McpConnectionRequired = "assistant-mcp-connection-required";

    /// <summary>The connection's tokens can no longer be renewed; its owner connects again.</summary>
    public const string McpReauthorizationRequired = "assistant-mcp-reauthorization-required";

    /// <summary>The server asked for a scope that the administrator did not allow.</summary>
    public const string McpScopeNotAllowed = "assistant-mcp-scope-not-allowed";

    /// <summary>The authorization server ended the authorization with an error, for example a denial.</summary>
    public const string McpAuthorizationFailed = "assistant-mcp-authorization-failed";

    /// <summary>The authorization is unknown, already used or expired.</summary>
    public const string McpAuthorizationExpired = "assistant-mcp-authorization-expired";

    /// <summary>The authorization response or its completion does not belong to this flow.</summary>
    public const string McpAuthorizationInvalid = "assistant-mcp-authorization-invalid";

    /// <summary>No usable protected resource or authorization server metadata was found.</summary>
    public const string McpDiscoveryFailed = "assistant-mcp-discovery-failed";

    /// <summary>The authorization server does not support PKCE with <c>S256</c>.</summary>
    public const string McpPkceUnsupported = "assistant-mcp-pkce-unsupported";

    /// <summary>The authorization server offers no registration the host can use; enter a client id.</summary>
    public const string McpClientRegistrationRequired = "assistant-mcp-client-registration-required";

    /// <summary>The token endpoint could not be reached or answered with an unexpected error.</summary>
    public const string McpTokenRequestFailed = "assistant-mcp-token-request-failed";

    /// <summary>The server names another authorization server than the confirmed one.</summary>
    public const string McpIssuerChanged = "assistant-mcp-issuer-changed";

    /// <summary>An OAuth server is used before an administrator confirmed its authorization server.</summary>
    public const string McpAuthorizationNotConfirmed = "assistant-mcp-authorization-not-confirmed";

    /// <summary>The host has no OAuth redirect URI (<c>NewHeap:AI:Assistant:Mcp:OAuth:RedirectUri</c>).</summary>
    public const string McpOAuthNotConfigured = "assistant-mcp-oauth-not-configured";

    /// <summary>The caller has too many unfinished authorizations.</summary>
    public const string McpTooManyAuthorizations = "assistant-mcp-too-many-authorizations";

    /// <summary>
    /// The server has no OAuth connection of the kind asked for: authorizing needs a server with the
    /// <c>oauth</c> authorization code flow, the organization's connection exists for <c>oauth</c> and
    /// <c>oauth-client-credentials</c>, and personal connections only for <c>oauth</c> servers that
    /// each person connects to.
    /// </summary>
    public const string McpConnectionNotSupported = "assistant-mcp-connection-not-supported";

    /// <summary>
    /// A stored secret of the server can no longer be read, for example after the data protection
    /// keys changed; an administrator enters it again.
    /// </summary>
    public const string McpSecretUnreadable = "assistant-mcp-secret-unreadable";

    /// <summary>
    /// The application's secret resolver failed while reading a secret reference; it may work again
    /// later.
    /// </summary>
    public const string McpSecretUnavailable = "assistant-mcp-secret-unavailable";

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

    /// <summary>
    /// The value is valid but the ceiling in code does not allow it, for example a consultation route
    /// that <c>UseConsultationsAsData</c> does not name.
    /// </summary>
    public const string NotAllowed = "not-allowed";
}

/// <summary>
/// Collects field errors (camelCase field names) and turns them into one failure, by default
/// <c>assistant-validation</c>. Field errors travel as result items named <c>field:&lt;name&gt;</c>
/// after the code item, and the endpoints return them in the <c>errors</c> object of the error body.
/// A field of a nested input object is named with its path in dots, for example
/// <c>oauth.clientSecretReference</c>, exactly as the client names the field.
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
        return ToResult(NhAssistantAdminErrorCodes.ValidationFailed, "The input is invalid.");
    }

    /// <summary>
    /// The field errors as a failure with its own code, such as
    /// <see cref="NhAssistantErrorCodes.RunFilterInvalid"/> for the filter of a run history.
    /// </summary>
    public NewHeap.Platform.Common.Models.TaskResult ToResult(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var result = NewHeap.Platform.Common.Models.TaskResult.Failed(code, message);
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
