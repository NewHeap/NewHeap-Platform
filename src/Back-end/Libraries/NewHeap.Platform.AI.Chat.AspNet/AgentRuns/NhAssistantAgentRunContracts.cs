using System.Text.Json;
using System.Text.Json.Serialization;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <param name="ConversationId">The thread conversation; read it with <c>GET conversations/{id}</c>.</param>
/// <param name="Role">The caller's role on the subject: reader, participant or approver.</param>
public sealed record NhAssistantThreadDto(
    Guid ConversationId,
    string AgentId,
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string Accountability,
    string AccountableOwnerId,
    string Role,
    NhAssistantRunDto? ActiveRun,
    int WaitingInputs)
{
    /// <summary>
    /// The thread holds the agent's shadow work about the subject: only administrators see it,
    /// nobody is notified and nothing proposed in it executes.
    /// </summary>
    public bool Shadow { get; init; }

    /// <summary>
    /// The name of the subject type in the caller's language, such as "Project", when the application
    /// named the type with <c>AddSubjectType</c>; <see langword="null"/> otherwise.
    /// </summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>
    /// Where the application shows the subject, from the type's link template; <see langword="null"/>
    /// without one.
    /// </summary>
    public string? SubjectLink { get; init; }
}

/// <param name="Thread">The agent's thread about the subject, or <see langword="null"/> before its first run.</param>
/// <param name="CanStart">Whether the caller may start the agent on the subject.</param>
public sealed record NhAssistantThreadLookupDto(
    NhAssistantThreadDto? Thread,
    bool CanStart,
    string Role);

/// <param name="Configured">Whether the agent keeps a working memory at all.</param>
/// <param name="Memory">The memory as JSON, or <see langword="null"/> while it is empty.</param>
public sealed record NhAssistantThreadMemoryDto(
    bool Configured,
    int SchemaVersion,
    JsonElement? Memory,
    DateTimeOffset? UpdatedAt);

/// <param name="Restricted">The artifact came from the agent's own grant and the caller may not read it.</param>
public sealed record NhAssistantArtifactDto(
    Guid Id,
    string Name,
    int Version,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    Guid? RunId,
    bool Restricted);

public sealed record NhAssistantStartAgentRequest(
    string? AgentId,
    string? SubjectType,
    string? SubjectId,
    string? Message);

public sealed record NhAssistantTriggerReceiptDto(
    string TriggerId,
    string Outcome,
    Guid? RunId,
    Guid? ConversationId,
    string? ReasonCode);

public sealed record NhAssistantRunDto(
    Guid Id,
    Guid ConversationId,
    string TriggerId,
    string Status,
    string? FailureClass,
    string? FailureCode,
    string? InitiatorActorId,
    string AccountableOwnerId,
    int Attempt,
    int AgentVersion,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    int ProposalCount,
    string? Summary,
    bool Shadow,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    Guid? BackgroundOperationId)
{
    /// <summary>
    /// How the run worked: <c>model</c>, <c>rule</c> without a model, or <c>rule-then-model</c> when
    /// the agent's rules handed it over (see <see cref="NhAssistantRunKinds"/>).
    /// </summary>
    public string Kind { get; init; } = NhAssistantRunKinds.Model;

    /// <summary>
    /// The skill of the agent the run worked in; <see langword="null"/> for the agent's own tools.
    /// </summary>
    public string? Skill { get; init; }

    /// <summary>
    /// Who chose <see cref="Skill"/>: <c>trigger</c>, <c>rules</c>, <c>triage</c> or <c>model</c>.
    /// </summary>
    public string? SkillChosenBy { get; init; }

    /// <summary>
    /// The version of a skill defined as data the run worked in; <see langword="null"/> for a skill
    /// in code.
    /// </summary>
    public int? SkillVersion { get; init; }

    /// <summary>
    /// The run evaluated a candidate version of its skill in the shadow thread.
    /// </summary>
    public bool SkillCandidate { get; init; }

    /// <summary>
    /// The agent that handed the run's work off; <see langword="null"/> for a run no agent asked for.
    /// </summary>
    public string? RequestedByAgentId { get; init; }

    /// <summary>
    /// The thread of the run that handed the work off, only in a thread's run list and only when the
    /// reader may open that thread.
    /// </summary>
    public Guid? RequestedByConversationId { get; init; }

    /// <summary>
    /// How many hand-offs led to the run: 0 for a run no agent asked for.
    /// </summary>
    public int HandOffDepth { get; init; }
}

public sealed record NhAssistantRunListDto(
    IReadOnlyList<NhAssistantRunDto> Items,
    int Total);

/// <param name="Mode"><c>resume</c> continues from the last checkpoint; <c>restart</c> starts a new run in the same thread.</param>
public sealed record NhAssistantRetryRunRequest(string? Mode);

/// <param name="Presentation">
/// The application's localized explanation of the proposal from its
/// <see cref="INhAssistantToolPresenter"/>, written when the proposal was recorded; show it instead
/// of <paramref name="ArgumentsPreview"/>. <see langword="null"/> when no presenter handled the tool.
/// </param>
public sealed record NhAssistantProposalDto(
    Guid ApprovalId,
    Guid ProposalId,
    string ProposalHash,
    Guid ConversationId,
    Guid? RunId,
    string AgentId,
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string ToolId,
    string Summary,
    NhAssistantApprovalPresentationDto? Presentation,
    string ArgumentsPreview,
    IReadOnlyList<string> Targets,
    string Status,
    string Mode,
    string? PolicyId,
    string GrantSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? InitiatorActorId,
    string? DecidedByActorId,
    DateTimeOffset? DecidedAt,
    string? ExecutionResultCode,
    DateTimeOffset? ExecutedAt)
{
    /// <summary>
    /// Why a stale proposal no longer fits: <c>already-done</c>, <c>changed</c> or
    /// <c>not-applicable</c>; <see langword="null"/> for other statuses. The reason code is
    /// <see cref="ExecutionResultCode"/>.
    /// </summary>
    public string? StaleKind { get; init; }

    /// <summary>
    /// The explanation of a stale proposal in the caller's language, when the application gave one,
    /// such as who already did the work.
    /// </summary>
    public string? StaleExplanation { get; init; }

    /// <summary>
    /// Who made the proposal stale, when the application's guard named them.
    /// </summary>
    public string? StaleByActorId { get; init; }

    /// <summary>
    /// The name of <see cref="StaleByActorId"/> from the subject's member snapshot, or
    /// <see langword="null"/> when the snapshot does not know the person.
    /// </summary>
    public string? StaleByDisplayName { get; init; }

    /// <summary>
    /// The application objects the proposal changes, as its guards named them, so clients can group
    /// proposals by object. <c>Targets</c> holds the invocation's execution scopes instead.
    /// </summary>
    public IReadOnlyList<NhAssistantProposalObjectDto> Objects { get; init; } = [];

    /// <summary>
    /// The newer proposal for the same objects that replaced this withdrawn one.
    /// </summary>
    public Guid? SupersededByApprovalId { get; init; }

    /// <summary>
    /// Why the agent's rules proposed it, in the caller's language; <see langword="null"/> for model
    /// proposals.
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// The consultation that proposed it, when another assistant consulted the agent on behalf of
    /// <see cref="InitiatorActorId"/>. It executes with that person's rights only.
    /// </summary>
    public Guid? ConsultationId { get; init; }

    /// <summary>
    /// The caller's own feedback on the proposal, or <see langword="null"/> when they gave none.
    /// Other people's feedback is shown to administrators only.
    /// </summary>
    public NhAssistantProposalFeedbackDto? Feedback { get; init; }

    /// <summary>
    /// The name of <see cref="DecidedByActorId"/> from the subject's member snapshot, or
    /// <see langword="null"/> when the snapshot does not know the person; show the id then.
    /// </summary>
    public string? DecidedByDisplayName { get; init; }

    /// <summary>
    /// Whether the caller may give or change feedback with <c>PUT approvals/{approvalId}/feedback</c>:
    /// the proposal was rejected, expired, withdrawn or closed as stale, and the caller decided it or
    /// is an approver of the subject.
    /// </summary>
    public bool CanGiveFeedback { get; init; }

    /// <summary>
    /// The name of the subject type in the caller's language, when the application named the type;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>
    /// Where the application shows the subject; <see langword="null"/> without a link template.
    /// </summary>
    public string? SubjectLink { get; init; }

    /// <summary>
    /// The run that placed this proposal here from another subject, such as a run for the whole
    /// application; <see langword="null"/> for a proposal made in this thread. Clients show "Proposed by
    /// a run for …" with a link to that run's thread.
    /// </summary>
    public NhAssistantProposalOriginDto? PlacedBy { get; init; }
}

/// <summary>
/// The run that placed a proposal at another object, and the thread it ran in. A reader of the object
/// may not read the run's own subject, so its name is given only for the whole application; opening
/// the thread checks the reader's access as always.
/// </summary>
/// <param name="ConversationId">The thread of the run that placed the proposal.</param>
/// <param name="RunId">The run that placed it.</param>
/// <param name="SubjectType">The run's subject type, such as <c>nh-system</c> for the whole application.</param>
public sealed record NhAssistantProposalOriginDto(
    Guid ConversationId,
    Guid? RunId,
    string? SubjectType)
{
    /// <summary>
    /// The run's subject id for the whole application (<c>global</c> or <c>division-{id}</c>);
    /// <see langword="null"/> for another object.
    /// </summary>
    public string? SubjectId { get; init; }

    /// <summary>
    /// The name of the run's subject for the whole application, in the caller's language;
    /// <see langword="null"/> for another object.
    /// </summary>
    public string? SubjectDisplayName { get; init; }

    /// <summary>
    /// The name of the run's subject type in the caller's language, when the application named it.
    /// </summary>
    public string? SubjectTypeName { get; init; }
}

/// <summary>
/// An application object a proposal changes, such as <c>("project-task", "4711")</c>.
/// </summary>
public sealed record NhAssistantProposalObjectDto(string Type, string Id);

/// <param name="Category">One of <c>incorrect</c>, <c>wrong-value</c>, <c>not-needed</c>, <c>not-now</c>, <c>already-done</c>, <c>not-allowed</c> or <c>other</c>.</param>
/// <param name="Text">Optional explanation of at most 1,000 characters.</param>
public sealed record NhAssistantProposalFeedbackRequest(
    string? Category,
    string? Text);

/// <param name="ApprovalIds">At most 50 proposals that were rejected together.</param>
public sealed record NhAssistantBulkProposalFeedbackRequest(
    IReadOnlyList<Guid>? ApprovalIds,
    string? Category,
    string? Text);

/// <param name="Group"><c>wrong</c>, <c>right-but-not-now</c>, <c>not-allowed</c> or <c>other</c>.</param>
/// <param name="Bulk">The feedback was given once for several proposals rejected together.</param>
public sealed record NhAssistantProposalFeedbackDto(
    Guid ApprovalId,
    string Category,
    string Group,
    string? Text,
    bool Bulk,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NhAssistantFeedbackOutcomeDto(
    Guid ApprovalId,
    bool Succeeded,
    string? Code);

public sealed record NhAssistantBulkProposalFeedbackDto(IReadOnlyList<NhAssistantFeedbackOutcomeDto> Results);

public sealed record NhAssistantFeedbackCategoryCountDto(
    string Category,
    string Group,
    int Count);

/// <summary>
/// The proposals of one tool and agent version within the report's period and what people said
/// about them.
/// </summary>
/// <param name="AgentVersion">The agent row version of the runs that proposed them; <see langword="null"/> for proposals of consultations.</param>
/// <param name="Approved">Approved proposals, including those that executed, failed, closed as stale or were resolved.</param>
/// <param name="ExecutionFailed">Approved proposals whose execution failed; part of <paramref name="Approved"/>.</param>
/// <param name="Stale">Approved proposals closed right before execution because they no longer fit; part of <paramref name="Approved"/>.</param>
/// <param name="RejectionRate">Rejected divided by approved plus rejected, or <see langword="null"/> before anyone decided.</param>
/// <param name="RejectedWithFeedback">Rejected proposals with at least one feedback.</param>
/// <param name="FeedbackProposals">Proposals of any status with at least one feedback.</param>
/// <param name="FeedbackAuthors">Different people who gave feedback, so one critical approver is visible as one.</param>
public sealed record NhAssistantFeedbackSummaryRowDto(
    string ToolId,
    int? AgentVersion,
    int Pending,
    int Approved,
    int Rejected,
    int Expired,
    int Withdrawn,
    int Stale,
    int ExecutionFailed,
    double? RejectionRate,
    int RejectedWithFeedback,
    int FeedbackProposals,
    int FeedbackAuthors,
    IReadOnlyList<NhAssistantFeedbackCategoryCountDto> Categories)
{
    /// <summary>
    /// Open proposals closed before anyone decided because they no longer fit, such as work a person
    /// already did. They are neither approved nor rejected and do not count in the rejection rate.
    /// </summary>
    public int Invalidated { get; init; }

    /// <summary>
    /// Why proposals closed as stale, per kind and reason code, before and after approval.
    /// </summary>
    public IReadOnlyList<NhAssistantInvalidationCountDto> Invalidations { get; init; } = [];
}

/// <param name="Kind"><c>already-done</c>, <c>changed</c> or <c>not-applicable</c>.</param>
/// <param name="ReasonCode">The application guard's reason code.</param>
/// <param name="AfterApproval">True when the proposal closed right before execution, after its approval.</param>
public sealed record NhAssistantInvalidationCountDto(
    string Kind,
    string ReasonCode,
    bool AfterApproval,
    int Count);

/// <param name="From">Start of the period; proposals are selected by when they were made.</param>
/// <param name="To">End of the period, exclusive.</param>
public sealed record NhAssistantFeedbackSummaryDto(
    string AgentId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<NhAssistantFeedbackSummaryRowDto> Rows);

/// <param name="ApprovalStatus">The proposal's status when the feedback was given.</param>
/// <param name="Summary">The proposal's summary as the agent recorded it.</param>
/// <param name="Presentation">The application's explanation of the proposal, as approvers read it.</param>
public sealed record NhAssistantFeedbackItemDto(
    Guid Id,
    Guid ApprovalId,
    Guid ProposalId,
    Guid ConversationId,
    Guid? RunId,
    string ScopeKey,
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string ToolId,
    int? AgentVersion,
    string ApprovalStatus,
    string Summary,
    NhAssistantApprovalPresentationDto? Presentation,
    string Category,
    string Group,
    string? Text,
    string AuthorActorId,
    bool Bulk,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The name of <see cref="AuthorActorId"/> from the member snapshots, or <see langword="null"/>
    /// when no snapshot knows the person.
    /// </summary>
    public string? AuthorDisplayName { get; init; }
}

/// <summary>
/// One agent on a subject: its thread when it worked on the subject, and whether the caller may
/// start it.
/// </summary>
/// <param name="Thread">The agent's thread about the subject, or <see langword="null"/> before it worked on it.</param>
public sealed record NhAssistantSubjectAgentDto(
    string AgentId,
    NhAssistantThreadDto? Thread,
    bool CanStart)
{
    /// <summary>
    /// For administrators, the agent's shadow thread about the subject, where its work goes while
    /// the agent or one of its skills works in shadow; <see langword="null"/> otherwise. A shadow
    /// variant's own threads are in <see cref="Thread"/>.
    /// </summary>
    public NhAssistantThreadDto? ShadowThread { get; init; }
}

/// <summary>
/// A proposal on a subject with the caller's live decision right. Only a pending proposal can be
/// decided; for every other status <paramref name="CanDecide"/> is <see langword="false"/> and
/// <paramref name="ReasonCode"/> is <see langword="null"/>.
/// </summary>
/// <param name="ReasonCode">Why the caller may not decide, such as <c>assistant-four-eyes-required</c>.</param>
public sealed record NhAssistantSubjectProposalDto(
    NhAssistantProposalDto Proposal,
    bool CanDecide,
    string? ReasonCode);

/// <summary>
/// The proposals to read with <c>POST approvals/lookup</c>.
/// </summary>
/// <param name="ApprovalIds">Between 1 and <see cref="MaxApprovalIds"/> proposal ids; duplicates count once.</param>
public sealed record NhAssistantProposalLookupRequest(IReadOnlyList<Guid>? ApprovalIds)
{
    /// <summary>
    /// The most proposals one lookup reads. Every subject in a lookup costs a live call to its
    /// access resolver.
    /// </summary>
    public const int MaxApprovalIds = 100;
}

/// <summary>
/// The proposals of a lookup that the caller may see, in the order of the request. Ids the caller
/// may not see, or that do not exist, are left out.
/// </summary>
public sealed record NhAssistantProposalLookupDto(IReadOnlyList<NhAssistantSubjectProposalDto> Items);

/// <summary>
/// All agent work on one subject as the caller may see it: the agents with a thread on it and
/// their open proposals, oldest expiry first.
/// </summary>
/// <param name="Role">The caller's live role on the subject: reader, participant or approver.</param>
public sealed record NhAssistantSubjectWorkDto(
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string Role,
    IReadOnlyList<NhAssistantSubjectAgentDto> Agents,
    IReadOnlyList<NhAssistantSubjectProposalDto> OpenProposals)
{
    /// <summary>
    /// The name of the subject type in the caller's language, when the application named the type;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>
    /// Where the application shows the subject; <see langword="null"/> without a link template.
    /// </summary>
    public string? SubjectLink { get; init; }
}

/// <summary>
/// The agents that work on one subject and that the caller may use, as an object's page shows them
/// (<c>GET subjects/{type}/{id}/agents</c>): for each its latest run, its open proposals and whether
/// the caller may run it now. The subject may be an object or the whole application.
/// </summary>
/// <param name="Role">The caller's live role on the subject: reader, participant or approver.</param>
/// <param name="Agents">The agents ordered by id.</param>
public sealed record NhAssistantObjectAgentsDto(
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string Role,
    IReadOnlyList<NhAssistantObjectAgentDto> Agents)
{
    /// <summary>
    /// The name of the subject type in the caller's language, when the application named the type;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>
    /// Where the application shows the subject; <see langword="null"/> without a link template.
    /// </summary>
    public string? SubjectLink { get; init; }
}

/// <summary>
/// One agent that works on the subject.
/// </summary>
/// <param name="DisplayNameKey">The agent's name: a translation key for an agent in code, the text itself for one created in the administration, as in the agent picker.</param>
/// <param name="DescriptionKey">The agent's description, in the same way.</param>
/// <param name="CanStart">Whether the caller may run the agent on the subject now (<c>POST threads/start</c>).</param>
/// <param name="ConversationId">The agent's thread about the subject that people open, or <see langword="null"/> before it worked on it.</param>
/// <param name="LatestRun">The agent's newest run on the subject, or <see langword="null"/> before its first.</param>
/// <param name="OpenProposals">The agent's proposals on the subject that wait for a decision.</param>
public sealed record NhAssistantObjectAgentDto(
    string AgentId,
    string DisplayNameKey,
    string DescriptionKey,
    bool CanStart,
    Guid? ConversationId,
    NhAssistantLatestRunDto? LatestRun,
    int OpenProposals);

/// <summary>
/// The newest run of an agent on a subject, with the beginning of its result.
/// </summary>
/// <param name="At">When the run ended, or started, or was queued.</param>
/// <param name="Result">The first 300 characters of the run's stored answer (<c>GET runs/{id}</c> returns all of it), model output to show as untrusted text; for a run without one, such as a run that ended before runs kept their answer, the summary it ended with.</param>
/// <param name="ResultTruncated">The result is longer than <paramref name="Result"/>.</param>
/// <param name="Shadow">The run worked in the agent's shadow thread, which only administrators see.</param>
public sealed record NhAssistantLatestRunDto(
    Guid RunId,
    Guid ConversationId,
    string TriggerId,
    string Status,
    string? FailureCode,
    DateTimeOffset At,
    string? Result,
    bool ResultTruncated,
    bool Shadow);

/// <summary>
/// One run in a run history (<c>GET runs</c> for people, <c>GET admin/agents/{id}/runs</c> for
/// administrators): the agent, the subject it worked on, its trigger, status, usage and times, and the
/// beginning of its result.
/// </summary>
/// <param name="Kind">How the run worked: <c>model</c>, <c>rule</c> or <c>rule-then-model</c>.</param>
/// <param name="Skill">The skill the run worked in; <see langword="null"/> for the agent's own tools.</param>
/// <param name="Result">The first 300 characters of the run's answer, model output to show as untrusted text; for a run without a stored answer, the summary it ended with. <c>GET runs/{id}</c> returns the whole answer.</param>
/// <param name="ResultTruncated">The result is longer than <paramref name="Result"/>.</param>
/// <param name="Shadow">The run worked in the agent's shadow thread: nothing it proposed executes, and only administrators see it.</param>
public sealed record NhAssistantRunHistoryDto(
    Guid Id,
    Guid ConversationId,
    string AgentId,
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string TriggerId,
    string Status,
    string? FailureClass,
    string? FailureCode,
    string Kind,
    string? Skill,
    string? InitiatorActorId,
    int ToolCalls,
    int ModelCalls,
    long InputTokens,
    long OutputTokens,
    int ProposalCount,
    string? Result,
    bool ResultTruncated,
    bool Shadow,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt)
{
    /// <summary>
    /// The agent's name: a translation key for an agent in code, the text itself for one created in
    /// the administration, as in the agent picker; <see langword="null"/> for an agent that no longer
    /// exists.
    /// </summary>
    public string? AgentDisplayNameKey { get; init; }

    /// <summary>
    /// The name of the subject type in the caller's language, when the application named the type;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public string? SubjectTypeName { get; init; }

    /// <summary>
    /// Where the application shows the subject; <see langword="null"/> without a link template.
    /// </summary>
    public string? SubjectLink { get; init; }
}

/// <summary>
/// A page of a run history, newest first.
/// </summary>
/// <param name="Total">The number of runs that match the filter and that the caller may see.</param>
public sealed record NhAssistantRunHistoryListDto(
    IReadOnlyList<NhAssistantRunHistoryDto> Items,
    int Total)
{
    /// <summary>
    /// The history of <c>GET runs</c> considered only the agent threads with the most recent matching
    /// runs: older threads were left out. Narrow the filter, for example to one object or a period, to
    /// reach them. Always <see langword="false"/> in an agent's runs view for administrators.
    /// </summary>
    public bool Limited { get; init; }
}

/// <summary>
/// One run with its whole result, the proposals it made and the files it saved
/// (<c>GET runs/{id}</c>).
/// </summary>
/// <param name="Run">The run as a history lists it.</param>
/// <param name="Result">The run's whole answer as Markdown, at most 20,000 characters: model output, untrusted text that clients render only with a safe Markdown renderer and never as HTML; <see langword="null"/> while the run has none.</param>
/// <param name="ResultTruncated">The answer was longer than 20,000 characters and <paramref name="Result"/> is its beginning.</param>
/// <param name="Summary">The bounded summary the run ended with, which the agent's next runs start from.</param>
/// <param name="Proposals">The run's proposals the caller may see, oldest first: those in its own thread and those it placed at other objects the caller may read.</param>
/// <param name="HiddenProposals">The proposals the run placed at objects the caller may not read.</param>
/// <param name="Artifacts">The files the run saved, newest first; those of the agent's own grant that the caller may not read are marked restricted.</param>
/// <param name="CanOpenConversation">Whether the caller may open the run's thread conversation.</param>
public sealed record NhAssistantRunResultDto(
    NhAssistantRunHistoryDto Run,
    string? Result,
    bool ResultTruncated,
    string? Summary,
    IReadOnlyList<NhAssistantProposalDto> Proposals,
    int HiddenProposals,
    IReadOnlyList<NhAssistantArtifactDto> Artifacts,
    bool CanOpenConversation);

/// <summary>
/// The subjects of one type a search found that the caller may see, in the order the application's
/// search returned them.
/// </summary>
public sealed record NhAssistantSubjectSearchDto(
    string SubjectType,
    IReadOnlyList<NhAssistantSubjectSearchResultDto> Items);

/// <summary>
/// One subject a search found.
/// </summary>
/// <param name="DisplayName">The name <c>DescribeAsync</c> would give; show <paramref name="Id"/> without one.</param>
/// <param name="ScopeKey">The subject's isolation key: <c>tenant:…</c>, <c>division:…</c> or <c>global</c>.</param>
/// <param name="DivisionId">The subject's division, when it has one.</param>
public sealed record NhAssistantSubjectSearchResultDto(
    string Type,
    string Id,
    string? DisplayName,
    string ScopeKey,
    Guid? DivisionId)
{
    /// <summary>
    /// Where the application shows the subject; <see langword="null"/> without a link template.
    /// </summary>
    public string? Link { get; init; }
}

/// <param name="SubjectIds">At most 200 subjects of the route's subject type.</param>
public sealed record NhAssistantSubjectSummaryRequest(IReadOnlyList<string>? SubjectIds);

/// <summary>
/// The agent work on one subject for a list view.
/// </summary>
/// <param name="OpenProposals">Proposals that wait for a decision on the subject.</param>
/// <param name="ProposalsToDecide">The open proposals the caller may decide, counted like the approval inbox; each decision is checked again.</param>
/// <param name="RunActive">An agent works on the subject now.</param>
public sealed record NhAssistantSubjectSummaryDto(
    string SubjectId,
    int OpenProposals,
    int ProposalsToDecide,
    bool RunActive);

/// <param name="Items">One entry per requested subject, in request order; subjects without visible work count zero.</param>
public sealed record NhAssistantSubjectSummaryListDto(
    string SubjectType,
    IReadOnlyList<NhAssistantSubjectSummaryDto> Items);

/// <param name="AgentId">The agent whose conversation about the subject opens.</param>
public sealed record NhAssistantOpenThreadRequest(
    string? AgentId,
    string? SubjectType,
    string? SubjectId);

public sealed record NhAssistantFeedbackListDto(
    IReadOnlyList<NhAssistantFeedbackItemDto> Items,
    int Total);

/// <summary>
/// One line of the JSON Lines export: the feedback, the proposal as approvers read it and the tool
/// input the agent proposed, with the versions it came from. It holds subject data; treat the file
/// like the subjects themselves.
/// </summary>
/// <param name="Arguments">The tool input of the proposal.</param>
public sealed record NhAssistantFeedbackExportRecord(
    NhAssistantFeedbackItemDto Feedback,
    string ToolContractHash,
    string? PromptHash,
    JsonElement Arguments);

/// <summary>
/// One consultation of the thread's agent: who asked through which assistant, how it ended and what
/// it cost. Question and answer stay in the asking conversation.
/// </summary>
/// <param name="AskerDisplayName">The asker's name from the subject's member snapshot, when known.</param>
/// <param name="AskingAgentId">The assistant whose turn consulted, or <see langword="null"/> for a direct call.</param>
/// <param name="Status"><c>running</c>, <c>completed</c> or <c>failed</c>.</param>
public sealed record NhAssistantConsultationDto(
    Guid Id,
    string AskerActorId,
    string? AskerDisplayName,
    string? AskingAgentId,
    int Depth,
    string Status,
    string? FailureCode,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    int ProposalCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt);

public sealed record NhAssistantConsultationListDto(
    IReadOnlyList<NhAssistantConsultationDto> Items,
    int Total);

public sealed record NhAssistantInboxDto(
    IReadOnlyList<NhAssistantProposalDto> Items,
    int Total);

/// <param name="Decision"><c>approve</c> or <c>reject</c>.</param>
public sealed record NhAssistantProposalDecisionRequest(
    string? Decision,
    string? ExpectedProposalHash,
    string? Reason);

public sealed record NhAssistantBulkDecisionRequest(
    IReadOnlyList<Guid>? ApprovalIds,
    string? Decision,
    string? Reason);

public sealed record NhAssistantDecisionOutcomeDto(
    Guid ApprovalId,
    bool Succeeded,
    string? Code,
    string? Status);

public sealed record NhAssistantBulkDecisionDto(IReadOnlyList<NhAssistantDecisionOutcomeDto> Results);

/// <param name="Action"><c>re-execute</c> runs the proposal again; <c>mark-resolved</c> confirms the outcome without running it.</param>
public sealed record NhAssistantResolveExecutionRequest(
    string? Action,
    string? Reason);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantThreadLookupDto))]
[JsonSerializable(typeof(NhAssistantKillSwitchRequest))]
[JsonSerializable(typeof(NhAssistantThreadMemoryDto))]
[JsonSerializable(typeof(NhAssistantArtifactDto[]))]
[JsonSerializable(typeof(NhAssistantKillSwitchDto))]
[JsonSerializable(typeof(NhAssistantAgentState))]
[JsonSerializable(typeof(NhAssistantAgentStateRequest))]
[JsonSerializable(typeof(NhAssistantSkillState))]
[JsonSerializable(typeof(NhAssistantShadowRequest))]
[JsonSerializable(typeof(NhAssistantRunCatalogDto))]
[JsonSerializable(typeof(NhAssistantAccountDto[]))]
[JsonSerializable(typeof(NhAssistantAgentConfigurationDto))]
[JsonSerializable(typeof(NhAssistantDataSkillDto))]
[JsonSerializable(typeof(NhAssistantDataSkillVersionDto[]))]
[JsonSerializable(typeof(NhAssistantDataTriggerDto))]
[JsonSerializable(typeof(NhAssistantDataSkillRequest))]
[JsonSerializable(typeof(NhAssistantDataTriggerRequest))]
[JsonSerializable(typeof(NhAssistantAgentRunSettingsDto))]
[JsonSerializable(typeof(NhAssistantAgentRunSettingsRequest))]
[JsonSerializable(typeof(NhAssistantSkillCandidateRequest))]
[JsonSerializable(typeof(NhAssistantSkillComparisonDto))]
[JsonSerializable(typeof(NhAssistantServiceAccountRequest))]
[JsonSerializable(typeof(NhAssistantStartAgentRequest))]
[JsonSerializable(typeof(NhAssistantTriggerReceiptDto))]
[JsonSerializable(typeof(NhAssistantRunDto))]
[JsonSerializable(typeof(NhAssistantRunListDto))]
[JsonSerializable(typeof(NhAssistantRetryRunRequest))]
[JsonSerializable(typeof(NhAssistantProposalDto))]
[JsonSerializable(typeof(NhAssistantProposalDto[]))]
[JsonSerializable(typeof(NhAssistantInboxDto))]
[JsonSerializable(typeof(NhAssistantProposalDecisionRequest))]
[JsonSerializable(typeof(NhAssistantBulkDecisionRequest))]
[JsonSerializable(typeof(NhAssistantBulkDecisionDto))]
[JsonSerializable(typeof(NhAssistantResolveExecutionRequest))]
[JsonSerializable(typeof(NhAssistantConsultationListDto))]
[JsonSerializable(typeof(NhAssistantProposalFeedbackRequest))]
[JsonSerializable(typeof(NhAssistantBulkProposalFeedbackRequest))]
[JsonSerializable(typeof(NhAssistantProposalFeedbackDto))]
[JsonSerializable(typeof(NhAssistantBulkProposalFeedbackDto))]
[JsonSerializable(typeof(NhAssistantFeedbackSummaryDto))]
[JsonSerializable(typeof(NhAssistantFeedbackListDto))]
[JsonSerializable(typeof(NhAssistantFeedbackExportRecord))]
[JsonSerializable(typeof(NhAssistantEvaluationDto))]
[JsonSerializable(typeof(NhAssistantEvaluationExportRecord))]
[JsonSerializable(typeof(NhAssistantReplayRequest))]
[JsonSerializable(typeof(NhAssistantReplayDto))]
[JsonSerializable(typeof(NhAssistantReplayListDto))]
[JsonSerializable(typeof(NhAssistantSubjectWorkDto))]
[JsonSerializable(typeof(NhAssistantObjectAgentsDto))]
[JsonSerializable(typeof(NhAssistantRunHistoryListDto))]
[JsonSerializable(typeof(NhAssistantRunResultDto))]
[JsonSerializable(typeof(NhAssistantSubjectProposalDto))]
[JsonSerializable(typeof(NhAssistantProposalLookupRequest))]
[JsonSerializable(typeof(NhAssistantProposalLookupDto))]
[JsonSerializable(typeof(NhAssistantSubjectSummaryRequest))]
[JsonSerializable(typeof(NhAssistantSubjectSummaryListDto))]
[JsonSerializable(typeof(NhAssistantOpenThreadRequest))]
[JsonSerializable(typeof(NhAssistantSubjectSearchDto))]
[JsonSerializable(typeof(string[]))]
public sealed partial class NhAssistantAgentRunJsonSerializerContext : JsonSerializerContext;

internal static class NhAssistantAgentRunDtoMapper
{
    public static NhAssistantRunDto ToDto(AssistantRun run)
    {
        return new NhAssistantRunDto(
            run.Id,
            run.ConversationId,
            run.TriggerId,
            run.Status,
            run.FailureClass,
            run.FailureCode,
            run.InitiatorActorId,
            run.AccountableOwnerId,
            run.Attempt,
            run.AgentVersion,
            run.ToolCalls,
            run.InputTokens,
            run.OutputTokens,
            run.ProposalCount,
            run.Summary,
            run.Shadow,
            run.CreatedAt,
            run.StartedAt,
            run.EndedAt,
            run.BackgroundOperationId)
        {
            Kind = run.Kind,
            Skill = run.SkillId,
            SkillChosenBy = run.SkillChosenBy,
            SkillVersion = run.SkillVersion,
            SkillCandidate = run.SkillCandidate,
            RequestedByAgentId = run.RequestedByAgentId,
            HandOffDepth = run.HandOffDepth
        };
    }

    public static NhAssistantProposalDto ToDto(AssistantApproval approval, AssistantThread thread)
    {
        return new NhAssistantProposalDto(
            approval.Id,
            approval.ProposalId,
            approval.ProposalHash,
            approval.ConversationId,
            approval.RunId,
            thread.AgentId,
            thread.SubjectType,
            thread.SubjectId,
            thread.SubjectDisplayName,
            approval.ToolId,
            approval.Summary,
            NhAssistantApprovalPresentationDto.From(
                NhAssistantToolPresentationResolver.Pick(
                    approval.PresentationJson,
                    approval.PresentationsJson,
                    System.Globalization.CultureInfo.CurrentUICulture)),
            approval.ArgumentsPreview,
            ReadTargets(approval.TargetsJson),
            approval.Status,
            approval.Mode,
            approval.PolicyId,
            NhAssistantGrantSources.Name(approval.GrantSource),
            approval.CreatedAt,
            approval.ExpiresAt,
            approval.InitiatorActorId,
            approval.DecidedByActorId,
            approval.DecidedAt,
            approval.ExecutionResultCode,
            approval.ExecutedAt)
        {
            ConsultationId = approval.ConsultationId,
            StaleKind = NhAssistantConversationReader.StaleKindOf(approval),
            StaleExplanation = NhAssistantStaleExplanations.Pick(
                approval.StaleExplanationsJson,
                System.Globalization.CultureInfo.CurrentUICulture),
            StaleByActorId = approval.StaleByActorId,
            SupersededByApprovalId = approval.SupersededByApprovalId,
            Rationale = NhAssistantStaleExplanations.Pick(
                approval.RationalesJson,
                System.Globalization.CultureInfo.CurrentUICulture),
            // The run's subject follows in CompleteAsync.
            PlacedBy = approval.PlacedFromConversationId is { } origin
                ? new NhAssistantProposalOriginDto(origin, approval.RunId, null)
                : null
        };
    }

    /// <summary>
    /// Adds what a proposal row does not carry: the objects each proposal changes, the names of who
    /// decided it and who made it stale, from the member snapshot, and the name and link of its
    /// subject's type.
    /// </summary>
    public static async Task<IReadOnlyList<NhAssistantProposalDto>> CompleteAsync(
        IReadOnlyList<NhAssistantProposalDto> proposals,
        NhAssistantRunStore runs,
        NhAssistantSubjectAccessService subjects,
        CancellationToken cancellationToken)
    {
        if (proposals.Count == 0)
        {
            return proposals;
        }

        var keys = await runs.GetApprovalKeysAsync(proposals.Select(proposal => proposal.ApprovalId).ToArray(), cancellationToken);
        var names = await runs.GetDisplayNamesAsync(
            proposals.SelectMany(proposal => new[] { proposal.DecidedByActorId, proposal.StaleByActorId }),
            cancellationToken);
        var origins = await runs.FindThreadsByConversationsAsync(
            proposals
                .Where(proposal => proposal.PlacedBy is not null)
                .Select(proposal => proposal.PlacedBy!.ConversationId)
                .ToArray(),
            cancellationToken);
        return proposals
            .Select(proposal =>
            {
                var (typeName, link) = SubjectOf(subjects, proposal.SubjectType, proposal.SubjectId);
                return proposal with
                {
                    Objects = keys[proposal.ApprovalId].Select(key => new NhAssistantProposalObjectDto(key.Type, key.Id)).ToArray(),
                    DecidedByDisplayName = proposal.DecidedByDisplayName
                        ?? (proposal.DecidedByActorId is { } decidedBy ? names.GetValueOrDefault(decidedBy) : null),
                    StaleByDisplayName = proposal.StaleByActorId is { } staleBy ? names.GetValueOrDefault(staleBy) : null,
                    SubjectDisplayName = DisplayNameOf(subjects, proposal.SubjectType, proposal.SubjectId, proposal.SubjectDisplayName),
                    SubjectTypeName = typeName,
                    SubjectLink = link,
                    PlacedBy = proposal.PlacedBy is { } placedBy ? OriginOf(subjects, placedBy, origins) : null
                };
            })
            .ToArray();
    }

    /// <summary>
    /// The subject of the run that placed a proposal: named for the whole application, only by its kind
    /// for another object, whose name a reader of this one may not read. A removed thread names nothing.
    /// </summary>
    private static NhAssistantProposalOriginDto OriginOf(
        NhAssistantSubjectAccessService subjects,
        NhAssistantProposalOriginDto placedBy,
        IReadOnlyDictionary<Guid, AssistantThread> origins)
    {
        if (!origins.TryGetValue(placedBy.ConversationId, out var origin))
        {
            return placedBy;
        }

        var (typeName, _) = SubjectOf(subjects, origin.SubjectType, origin.SubjectId);
        var system = string.Equals(origin.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal);
        return placedBy with
        {
            SubjectType = origin.SubjectType,
            SubjectId = system ? origin.SubjectId : null,
            SubjectDisplayName = system
                ? DisplayNameOf(subjects, origin.SubjectType, origin.SubjectId, origin.SubjectDisplayName)
                : null,
            SubjectTypeName = typeName
        };
    }

    /// <summary>
    /// The name of the subject's type in the request's language, when the application named the type,
    /// and the link to the subject, when the type has a link template.
    /// </summary>
    public static (string? TypeName, string? Link) SubjectOf(
        NhAssistantSubjectAccessService subjects,
        string subjectType,
        string subjectId)
    {
        var type = subjects.FindType(subjectType);
        if (type is null)
        {
            return (null, null);
        }

        return (type.IsNamed ? type.NameIn(System.Globalization.CultureInfo.CurrentUICulture) : null, type.LinkFor(subjectId));
    }

    /// <summary>
    /// The name people see of a subject: the stored one, except for the whole application app-wide,
    /// which is named in the request's language.
    /// </summary>
    public static string? DisplayNameOf(
        NhAssistantSubjectAccessService subjects,
        string subjectType,
        string subjectId,
        string? storedName)
    {
        return subjects.FindType(subjectType) is { } type
            ? type.DisplayNameOf(subjectId, storedName, System.Globalization.CultureInfo.CurrentUICulture)
            : storedName;
    }

    public static NhAssistantProposalFeedbackDto ToDto(AssistantProposalFeedback feedback)
    {
        return new NhAssistantProposalFeedbackDto(
            feedback.ApprovalId,
            feedback.Category,
            GroupOf(feedback.Category),
            feedback.Text,
            feedback.Bulk,
            feedback.CreatedAt,
            feedback.UpdatedAt);
    }

    public static NhAssistantFeedbackItemDto ToDto(NhAssistantFeedbackRow row)
    {
        var feedback = row.Feedback;
        return new NhAssistantFeedbackItemDto(
            feedback.Id,
            feedback.ApprovalId,
            row.ProposalId,
            feedback.ConversationId,
            row.RunId,
            feedback.ScopeKey,
            feedback.SubjectType,
            feedback.SubjectId,
            row.SubjectDisplayName,
            feedback.ToolId,
            feedback.AgentVersion,
            feedback.ApprovalStatus,
            row.Summary,
            NhAssistantApprovalPresentationDto.From(
                NhAssistantToolPresentationResolver.Pick(
                    row.PresentationJson,
                    row.PresentationsJson,
                    System.Globalization.CultureInfo.CurrentUICulture)),
            feedback.Category,
            GroupOf(feedback.Category),
            feedback.Text,
            feedback.AuthorActorId,
            feedback.Bulk,
            feedback.CreatedAt,
            feedback.UpdatedAt);
    }

    /// <summary>
    /// The group of a stored category; a category that a later version no longer knows counts as
    /// <c>other</c>.
    /// </summary>
    public static string GroupOf(string category)
    {
        return NhAssistantProposalFeedbackCategories.IsKnown(category)
            ? NhAssistantProposalFeedbackCategories.GroupOf(category)
            : NhAssistantProposalFeedbackGroups.Other;
    }

    public static NhAssistantConsultationDto ToDto(AssistantConsultation consultation, string? askerDisplayName)
    {
        return new NhAssistantConsultationDto(
            consultation.Id,
            consultation.AskerActorId,
            askerDisplayName,
            consultation.AskingAgentId,
            consultation.Depth,
            consultation.Status,
            consultation.FailureCode,
            consultation.ToolCalls,
            consultation.InputTokens,
            consultation.OutputTokens,
            consultation.ProposalCount,
            consultation.CreatedAt,
            consultation.EndedAt);
    }

    public static string RoleName(NhAiSubjectRole role)
    {
        return role switch
        {
            NhAiSubjectRole.Reader => "reader",
            NhAiSubjectRole.Participant => "participant",
            NhAiSubjectRole.Approver => "approver",
            _ => "none"
        };
    }

    private static IReadOnlyList<string> ReadTargets(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, NhAssistantAgentRunJsonSerializerContext.Default.StringArray) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
