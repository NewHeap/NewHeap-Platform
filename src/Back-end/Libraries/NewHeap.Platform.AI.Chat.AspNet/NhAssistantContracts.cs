using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat.AspNet;

public sealed record NhAssistantLimitsDto(
    int MaxMessageChars,
    int MaxToolCallsPerTurn);

/// <param name="Collaboration">
/// Which sharing, live update and notification features the server offers; <see langword="null"/>
/// while the assistant is disabled.
/// </param>
public sealed record NhAssistantStatusDto(
    bool Enabled,
    IReadOnlyList<NhAssistantAgentSummaryDto> Agents,
    NhAssistantLimitsDto Limits,
    bool CanAdminister,
    NhAssistantCollaborationDto? Collaboration = null);

/// <param name="Directory">Owners can invite colleagues directly through the application's directory.</param>
/// <param name="HubPath">Path of the SignalR hub for live updates, or <see langword="null"/> when they are off.</param>
/// <param name="Push">Web Push notifications are configured.</param>
/// <param name="MaxParticipants">Maximum participants per conversation, not counting the owner.</param>
public sealed record NhAssistantCollaborationDto(
    bool Directory,
    string? HubPath,
    bool Push,
    int MaxParticipants);

public sealed record NhAssistantAgentSummaryDto(
    string Id,
    int Version,
    string DisplayNameKey,
    string DescriptionKey,
    bool CanMutate);

/// <param name="Role">The caller's relation: <c>owner</c> or <c>participant</c>.</param>
/// <param name="ParticipantCount">People the owner shared the conversation with.</param>
/// <param name="LastMessageSequence">Sequence of the latest message.</param>
/// <param name="LastReadSequence">The caller's read position; unread while lower than <paramref name="LastMessageSequence"/>.</param>
/// <param name="ActiveActorId">The owner or participant whose turn runs or waits for approval.</param>
public sealed record NhAssistantConversationSummaryDto(
    Guid Id,
    string AgentId,
    string? Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Role = NhAssistantParticipantRoles.Owner,
    int ParticipantCount = 0,
    int LastMessageSequence = 0,
    int LastReadSequence = 0,
    string? ActiveActorId = null);

public sealed record NhAssistantConversationListDto(
    IReadOnlyList<NhAssistantConversationSummaryDto> Items,
    int Total);

/// <summary>
/// A conversation with its messages as the caller sees it. <see cref="PendingApproval"/> is typed as a
/// message part so the <c>type: "approval"</c> discriminator is part of the payload.
/// </summary>
/// <param name="Role">The caller's relation: <c>owner</c> or <c>participant</c>.</param>
/// <param name="LastReadSequence">The caller's read position.</param>
/// <param name="LastMessageSequence">Sequence of the latest message.</param>
/// <param name="ActiveActorId">
/// The owner or participant whose turn runs or waits for approval. In a chat only that person decides
/// the approval; in an agent thread <paramref name="Thread"/> says who may decide.
/// </param>
/// <param name="Members">The owner followed by the participants; empty while the conversation is not shared.</param>
/// <param name="ShareToken">The current invitation-link token; only returned to the owner.</param>
/// <param name="CurrentActorId">The caller's actor id, to recognize the caller's own messages.</param>
/// <param name="Kind">
/// <c>chat</c> for a conversation people start, <c>thread</c> for the durable thread of an agent about
/// one subject (see <see cref="NhAssistantConversationKinds"/>).
/// </param>
/// <param name="Thread">
/// The subject of an agent thread and the caller's standing in it, including who may decide its
/// pending proposals; <see langword="null"/> for a chat.
/// </param>
public sealed record NhAssistantConversationDto(
    Guid Id,
    string AgentId,
    string? Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int AgentVersion,
    IReadOnlyList<NhAssistantMessageDto> Messages,
    NhAssistantMessagePartDto? PendingApproval,
    string Role = NhAssistantParticipantRoles.Owner,
    int LastReadSequence = 0,
    int LastMessageSequence = 0,
    string? ActiveActorId = null,
    IReadOnlyList<NhAssistantMemberDto>? Members = null,
    string? ShareToken = null,
    string? CurrentActorId = null,
    string Kind = NhAssistantConversationKinds.Chat,
    NhAssistantConversationThreadDto? Thread = null);

/// <summary>
/// The subject of an agent thread as the caller sees it. Access follows the subject access
/// resolver on every request, so the role and the decisions are live.
/// </summary>
/// <param name="Role">The caller's role on the subject: <c>reader</c>, <c>participant</c> or <c>approver</c>.</param>
/// <param name="Decisions">Each pending proposal of the thread and whether the caller may decide it now, oldest first.</param>
public sealed record NhAssistantConversationThreadDto(
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName,
    string Role,
    IReadOnlyList<NhAssistantThreadDecisionDto> Decisions);

/// <summary>
/// The caller's right to decide one pending proposal of an agent thread, checked against the
/// approval policy, four-eyes and the tool's approve policy like the decision itself.
/// </summary>
/// <param name="RunId">
/// The run that proposed it: decide it with <c>POST approvals/{approvalId}/decisions</c>. Without a
/// run the proposal belongs to a person's turn in the thread and is decided in the conversation.
/// </param>
/// <param name="CanDecide">Whether the caller may approve or reject the proposal now.</param>
/// <param name="ReasonCode">
/// Why the caller may not decide, for example <c>assistant-four-eyes-required</c> for the person who
/// started the work or <c>assistant-approval-policy-denied</c>; <see langword="null"/> when they may.
/// </param>
public sealed record NhAssistantThreadDecisionDto(
    Guid ApprovalId,
    Guid? RunId,
    bool CanDecide,
    string? ReasonCode);

/// <param name="Sequence">Position of the message in the conversation.</param>
/// <param name="AuthorActorId">The writer of a user message; <see langword="null"/> for assistant messages.</param>
/// <param name="AuthorKind">
/// <c>person</c> for a user message a person wrote, <c>trigger</c> for one a trigger wrote into an agent
/// thread, <c>rule</c> for the agent's message about what its rules did without a model (see
/// <see cref="NhAssistantMessageAuthorKinds"/>); <see langword="null"/> for the model's messages.
/// </param>
public sealed record NhAssistantMessageDto(
    Guid Id,
    string Role,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NhAssistantMessagePartDto> Parts,
    int Sequence = 0,
    string? AuthorActorId = null,
    string? AuthorKind = null);

/// <summary>
/// The owner or a participant of a shared conversation. <paramref name="DisplayName"/> is
/// <see langword="null"/> when the application provides no name.
/// </summary>
public sealed record NhAssistantMemberDto(
    string ActorId,
    string? DisplayName,
    string Role,
    DateTimeOffset JoinedAt);

/// <param name="Sequence">The sequence the caller has read up to; omitted means the latest message.</param>
public sealed record NhAssistantMarkReadRequest(int? Sequence);

public sealed record NhAssistantShareLinkDto(string Token);

public sealed record NhAssistantJoinConversationRequest(string? Token);

public sealed record NhAssistantInviteParticipantRequest(string? ActorId);

public sealed record NhAssistantDirectoryEntryDto(
    string ActorId,
    string DisplayName,
    string? Detail);

/// <param name="PushEnabled">The caller's choice; push notifications are on by default.</param>
/// <param name="PushAvailable">The server has Web Push configured.</param>
/// <param name="PublicKey">The VAPID application server key browsers subscribe with.</param>
public sealed record NhAssistantNotificationSettingsDto(
    bool PushEnabled,
    bool PushAvailable,
    string? PublicKey);

public sealed record NhAssistantUpdateNotificationSettingsRequest(bool? PushEnabled);

/// <summary>
/// A browser push subscription as <c>PushSubscription.toJSON()</c> returns it, plus the language of
/// the notification texts (<c>en</c> or <c>nl</c>).
/// </summary>
public sealed record NhAssistantPushSubscriptionRequest(
    string? Endpoint,
    NhAssistantPushSubscriptionKeysDto? Keys,
    string? Language = null);

public sealed record NhAssistantPushSubscriptionKeysDto(
    string? P256dh,
    string? Auth);

public sealed record NhAssistantRemovePushSubscriptionRequest(string? Endpoint);

/// <summary>
/// Live update: status, title, active actor, latest message or participants of a conversation changed.
/// </summary>
public sealed record NhAssistantLiveConversationDto(
    Guid ConversationId,
    string Status,
    string? Title,
    DateTimeOffset UpdatedAt,
    string? ActiveActorId,
    int LastMessageSequence,
    int ParticipantCount);

/// <summary>
/// Live update for the caller only: the caller read the conversation in another tab or device.
/// </summary>
public sealed record NhAssistantLiveReadDto(
    Guid ConversationId,
    int LastReadSequence);

/// <summary>
/// Live update for the caller only: the conversation is no longer available to the caller.
/// </summary>
public sealed record NhAssistantLiveRemovedDto(Guid ConversationId);

/// <summary>
/// Live update for the people on an agent thread's subject: a run, a proposal or its feedback
/// changed. It carries no content; clients reload the agent-run endpoints they show.
/// </summary>
/// <param name="ConversationId">The agent thread.</param>
/// <param name="Reason"><c>run</c>, <c>proposal</c> or <c>feedback</c>.</param>
/// <param name="SubjectType">The thread's subject, so lists and detail pages refresh the right entry.</param>
public sealed record NhAssistantLiveAgentWorkDto(
    Guid ConversationId,
    string Reason,
    Guid? ApprovalId,
    Guid? RunId,
    string SubjectType,
    string SubjectId);

/// <summary>
/// Live update: one event of a turn, with the same <paramref name="Type"/> and <paramref name="Data"/>
/// as the server-sent events of the starting request, or <c>message.created</c> with a
/// <see cref="NhAssistantMessageDto"/> when a participant's message was stored.
/// </summary>
public sealed record NhAssistantLiveEventDto(
    Guid ConversationId,
    string ActorId,
    string Type,
    JsonElement Data);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NhAssistantTextPartDto), "text")]
[JsonDerivedType(typeof(NhAssistantToolCallPartDto), "tool-call")]
[JsonDerivedType(typeof(NhAssistantApprovalPartDto), "approval")]
public abstract record NhAssistantMessagePartDto;

public sealed record NhAssistantTextPartDto(string Text) : NhAssistantMessagePartDto;

public sealed record NhAssistantToolCallPartDto(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string DisplayName,
    string Status,
    string? ArgumentsPreview,
    string? ResultPreview,
    string? ResultCode) : NhAssistantMessagePartDto
{
    /// <summary>
    /// <c>delegated</c>, <c>agent</c> or <c>delegated-read</c> when an agent used rights beyond the
    /// caller's own; <see langword="null"/> otherwise.
    /// </summary>
    public string? GrantSource { get; init; }

    /// <summary>
    /// The result came from the agent's own grant and is shielded from this viewer.
    /// </summary>
    public bool Restricted { get; init; }
}

public sealed record NhAssistantPresentationFieldDto(string Label, string Value);

public sealed record NhAssistantApprovalPresentationDto(
    string ToolDisplayName,
    string Summary,
    IReadOnlyList<NhAssistantPresentationFieldDto> Fields,
    string? Notice)
{
    internal static NhAssistantApprovalPresentationDto? From(NhAssistantApprovalPresentation? presentation)
    {
        return presentation is null
            ? null
            : new NhAssistantApprovalPresentationDto(
                presentation.ToolDisplayName,
                presentation.Summary,
                presentation.Fields
                    .Select(field => new NhAssistantPresentationFieldDto(field.Label, field.Value))
                    .ToArray(),
                presentation.Notice);
    }
}

public sealed record NhAssistantApprovalPartDto(
    Guid ApprovalId,
    Guid ProposalId,
    string ProposalHash,
    string ToolId,
    string Summary,
    NhAssistantApprovalPresentationDto? Presentation,
    string ArgumentsPreview,
    IReadOnlyList<string> Targets,
    DateTimeOffset ExpiresAt,
    string Status) : NhAssistantMessagePartDto
{
    /// <summary>
    /// Why a stale proposal no longer fits: <c>already-done</c>, <c>changed</c> or
    /// <c>not-applicable</c>. Only written for stale proposals.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleKind { get; init; }

    /// <summary>
    /// The explanation of a stale proposal in the caller's language, when the application gave one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleExplanation { get; init; }

    /// <summary>
    /// Why the agent's rules proposed it, in the caller's language. Only written for rule proposals.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rationale { get; init; }

    /// <summary>
    /// The newer proposal for the same objects that replaced this withdrawn one. Only written for
    /// superseded proposals.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SupersededByApprovalId { get; init; }
}

public sealed record NhAssistantCreateConversationRequest(
    string? AgentId,
    string? Title);

/// <param name="ClientContext">
/// Optional page context (<see cref="NhAssistantClientContextDto"/>). It is read leniently: a value of
/// the wrong shape is ignored instead of rejecting the message.
/// </param>
public sealed record NhAssistantSendMessageRequest(
    string? Text,
    string? ClientMessageId,
    JsonElement? ClientContext = null);

/// <summary>
/// What the user has open: route (max 200), title (max 120) and at most 5 entities. Untrusted data
/// for the model; it never grants access.
/// </summary>
public sealed record NhAssistantClientContextDto(
    string Route,
    string? Title,
    IReadOnlyList<NhAssistantClientEntityDto>? Entities);

/// <summary>
/// An entity on the user's screen: dash-case type, id (max 64) and optional label (max 120).
/// </summary>
public sealed record NhAssistantClientEntityDto(
    string Type,
    string Id,
    string? Label);

public sealed record NhAssistantDecideApprovalRequest(
    string? Decision,
    string? ExpectedProposalHash,
    string? Reason);

public sealed record NhAssistantErrorDto(
    string Code,
    string MessageKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record NhAssistantTurnStartedDto(
    Guid TurnId,
    Guid UserMessageId,
    Guid AssistantMessageId);

public sealed record NhAssistantMessageDeltaDto(
    Guid MessageId,
    string Text);

public sealed record NhAssistantToolStartedDto(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string DisplayName,
    string? ArgumentsPreview);

public sealed record NhAssistantToolCompletedDto(
    Guid InvocationId,
    string Status,
    string? ResultCode,
    string? ResultPreview);

public sealed record NhAssistantUsageDto(
    int InputTokens,
    int OutputTokens,
    int ToolCalls);

public sealed record NhAssistantTurnCompletedDto(
    Guid TurnId,
    string Status,
    NhAssistantUsageDto Usage,
    string? ErrorCode);

/// <summary>
/// Source-generated JSON contract of the assistant HTTP API: web (camelCase) names, nulls written.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantStatusDto))]
[JsonSerializable(typeof(NhAssistantAgentSummaryDto[]))]
[JsonSerializable(typeof(NhAssistantConversationListDto))]
[JsonSerializable(typeof(NhAssistantConversationDto))]
[JsonSerializable(typeof(NhAssistantMessagePartDto))]
[JsonSerializable(typeof(NhAssistantCreateConversationRequest))]
[JsonSerializable(typeof(NhAssistantSendMessageRequest))]
[JsonSerializable(typeof(NhAssistantClientContextDto))]
[JsonSerializable(typeof(NhAssistantDecideApprovalRequest))]
[JsonSerializable(typeof(NhAssistantErrorDto))]
[JsonSerializable(typeof(NhAssistantTurnStartedDto))]
[JsonSerializable(typeof(NhAssistantMessageDeltaDto))]
[JsonSerializable(typeof(NhAssistantToolStartedDto))]
[JsonSerializable(typeof(NhAssistantToolCompletedDto))]
[JsonSerializable(typeof(NhAssistantTurnCompletedDto))]
[JsonSerializable(typeof(NhAssistantMessageDto))]
[JsonSerializable(typeof(NhAssistantMarkReadRequest))]
[JsonSerializable(typeof(NhAssistantShareLinkDto))]
[JsonSerializable(typeof(NhAssistantJoinConversationRequest))]
[JsonSerializable(typeof(NhAssistantInviteParticipantRequest))]
[JsonSerializable(typeof(NhAssistantDirectoryEntryDto[]))]
[JsonSerializable(typeof(NhAssistantNotificationSettingsDto))]
[JsonSerializable(typeof(NhAssistantUpdateNotificationSettingsRequest))]
[JsonSerializable(typeof(NhAssistantPushSubscriptionRequest))]
[JsonSerializable(typeof(NhAssistantRemovePushSubscriptionRequest))]
[JsonSerializable(typeof(NhAssistantLiveConversationDto))]
[JsonSerializable(typeof(NhAssistantLiveReadDto))]
[JsonSerializable(typeof(NhAssistantLiveRemovedDto))]
[JsonSerializable(typeof(NhAssistantLiveAgentWorkDto))]
[JsonSerializable(typeof(NhAssistantLiveEventDto))]
[JsonSerializable(typeof(NhAssistantPreferencesDto))]
[JsonSerializable(typeof(NhAssistantApplicationContextDto))]
[JsonSerializable(typeof(NhAssistantApplicationContextVersionDto[]))]
[JsonSerializable(typeof(NhAssistantUpdateContextRequest))]
[JsonSerializable(typeof(NhAssistantToolCatalogEntryDto[]))]
[JsonSerializable(typeof(NhAssistantAdminAgentDto))]
[JsonSerializable(typeof(NhAssistantAdminAgentDto[]))]
[JsonSerializable(typeof(NhAssistantAdminAgentInputDto))]
[JsonSerializable(typeof(NhAssistantMcpServerDto))]
[JsonSerializable(typeof(NhAssistantMcpServerDto[]))]
[JsonSerializable(typeof(NhAssistantMcpServerInputDto))]
[JsonSerializable(typeof(NhAssistantMcpServerTestResultDto))]
[JsonSerializable(typeof(NhAssistantMcpToolDto))]
[JsonSerializable(typeof(NhAssistantMcpToolDto[]))]
[JsonSerializable(typeof(NhAssistantUpdateMcpToolRequest))]
[JsonSerializable(typeof(NhAssistantMcpConnectionDto))]
[JsonSerializable(typeof(NhAssistantMcpConnectionDto[]))]
[JsonSerializable(typeof(NhAssistantUpdateMcpConnectionRequest))]
public sealed partial class NhAssistantJsonSerializerContext : JsonSerializerContext;

public sealed record NhAssistantPreferencesDto(
    string? Style,
    string? AddressForm,
    string? ResponseLength,
    string? CustomInstructions);

public sealed record NhAssistantApplicationContextDto(
    string Text,
    int Version,
    string Hash,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

public sealed record NhAssistantApplicationContextVersionDto(
    int Version,
    string Hash,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

public sealed record NhAssistantUpdateContextRequest(
    string? Text,
    int? ExpectedVersion);

public sealed record NhAssistantToolCatalogEntryDto(
    string Id,
    string Source,
    string Effect,
    string Description);

/// <summary>
/// Agent input. <see cref="ExpectedVersion"/> is required on update and ignored on create.
/// <see cref="IsEnabled"/> is accepted and ignored until the next major version: the run state
/// changes with <c>POST admin/agents/{id}/state</c>, and a new agent starts active.
/// <see cref="OfferedInChat"/> applies to agents created in the administration: omitted keeps it on
/// update and offers a new agent in the chat; agents in code take it from <c>HideFromChat()</c>.
/// </summary>
public sealed record NhAssistantAdminAgentInputDto(
    string? Id,
    string? DisplayName,
    string? Description,
    string? Instructions,
    IReadOnlyList<string>? ToolSelectors,
    IReadOnlyList<string>? McpServerIds,
    string? RequiredPolicy,
    string? Autonomy,
    bool? IsEnabled,
    int? ExpectedVersion = null,
    bool? OfferedInChat = null);

/// <param name="IsEnabled"><see langword="false"/> while the agent is stopped; see <paramref name="RunState"/>.</param>
/// <param name="RunState"><c>active</c>, <c>paused</c>, <c>stopped</c> or <c>configured-off</c>.</param>
/// <param name="WaitingInputs">The inputs that wait while the agent is paused.</param>
public sealed record NhAssistantAdminAgentDto(
    string Id,
    string DisplayName,
    string Description,
    string Instructions,
    IReadOnlyList<string> ToolSelectors,
    IReadOnlyList<string> McpServerIds,
    string? RequiredPolicy,
    string Autonomy,
    bool IsEnabled,
    int Version,
    string Source,
    bool IsOverridden,
    string InstructionsHash,
    DateTimeOffset UpdatedAt,
    string RunState = NhAssistantAgentRunStates.Active,
    DateTimeOffset? StateChangedAt = null,
    string? StateChangedBy = null,
    string? StateReason = null,
    int WaitingInputs = 0)
{
    /// <summary>
    /// The agent's skills with their states and today's usage; empty for an agent without skills.
    /// </summary>
    public IReadOnlyList<NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantSkillState> Skills { get; init; } = [];

    /// <summary>
    /// The live agent this agent is a shadow variant of; <see langword="null"/> for an agent that is
    /// no variant.
    /// </summary>
    public string? ShadowOf { get; init; }

    /// <summary>
    /// Whether the agent works in shadow, and how the code and an administrator decided it.
    /// </summary>
    public NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantShadowState? Shadow { get; init; }

    /// <summary>
    /// The technical account that answers for the agent's new runs; <see langword="null"/> for an
    /// agent that does not answer to one.
    /// </summary>
    public NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantServiceAccountState? ServiceAccount { get; init; }

    /// <summary>
    /// Whether people can choose the agent in the chat. An agent that is not offered keeps working on
    /// its triggers and in its subject threads.
    /// </summary>
    public bool OfferedInChat { get; init; } = true;
}

/// <summary>
/// MCP server input. <see cref="Secret"/>: omitted or null keeps the stored secret, an empty
/// string clears it, any other value replaces it.
/// </summary>
public sealed record NhAssistantMcpServerInputDto(
    string? Id,
    string? DisplayName,
    string? Url,
    string? AuthMode,
    string? HeaderName,
    string? Secret,
    string? RequiredPolicy,
    bool? IsEnabled)
{
    /// <summary>
    /// Whether the tools are offered in headless runs: <c>never</c> or <c>organization</c>
    /// (<see cref="NhAssistantMcpHeadlessUses"/>). Omitted or null keeps the stored value; a new
    /// server starts at <c>never</c>.
    /// </summary>
    public string? HeadlessUse { get; init; }

    /// <summary>
    /// The user name of <c>basic</c> authentication. Omitted or null keeps the stored value.
    /// </summary>
    public string? UserName { get; init; }

    /// <summary>
    /// A reference to the secret instead of a stored one, <c>config:&lt;name&gt;</c> or a prefix the
    /// application registered. Omitted or null keeps the stored value, an empty string clears it.
    /// </summary>
    public string? SecretReference { get; init; }

    /// <summary>
    /// The name of a configured client certificate for mutual TLS. Omitted or null keeps the stored
    /// value, an empty string clears it.
    /// </summary>
    public string? ClientCertificateReference { get; init; }

    /// <summary>
    /// <c>streamable-http</c> or <c>auto</c> (<see cref="NhAssistantMcpTransports"/>). Omitted or null
    /// keeps the stored value; a new server starts at <c>streamable-http</c>.
    /// </summary>
    public string? Transport { get; init; }
}

public sealed record NhAssistantMcpServerDto(
    string Id,
    string DisplayName,
    string Url,
    string AuthMode,
    string? HeaderName,
    bool HasSecret,
    string? RequiredPolicy,
    bool IsEnabled,
    DateTimeOffset? LastSyncAt,
    string? LastSyncStatus,
    IReadOnlyList<string> AssignedAgentIds)
{
    /// <summary>
    /// Whether the tools are offered in headless runs (<see cref="NhAssistantMcpHeadlessUses"/>).
    /// </summary>
    public string HeadlessUse { get; init; } = NhAssistantMcpHeadlessUses.Never;

    /// <summary>
    /// When an administrator's test or sync, or a turn with the shared credential, last connected.
    /// </summary>
    public DateTimeOffset? LastConnectAt { get; init; }

    /// <summary>
    /// The failure code of that connection; null when it succeeded or never ran.
    /// </summary>
    public string? LastConnectCode { get; init; }

    /// <summary>
    /// The user name of <c>basic</c> authentication; the password is a secret and never returned.
    /// </summary>
    public string? UserName { get; init; }

    /// <summary>
    /// The secret reference; it names where the secret is, it is not the secret.
    /// </summary>
    public string? SecretReference { get; init; }

    /// <summary>
    /// The name of the configured client certificate for mutual TLS.
    /// </summary>
    public string? ClientCertificateReference { get; init; }

    /// <summary>
    /// <c>streamable-http</c> or <c>auto</c> (<see cref="NhAssistantMcpTransports"/>).
    /// </summary>
    public string Transport { get; init; } = NhAssistantMcpTransports.StreamableHttp;
}

/// <summary>
/// An MCP server that agents may use in background runs with the caller's own credential once the
/// caller consents, and the caller's decision.
/// </summary>
public sealed record NhAssistantMcpConnectionDto(
    string ServerId,
    string DisplayName,
    string Host,
    bool AllowBackgroundUse,
    DateTimeOffset? ConsentedAt,
    IReadOnlyList<string> AgentIds);

public sealed record NhAssistantUpdateMcpConnectionRequest(bool? AllowBackgroundUse);

public sealed record NhAssistantMcpServerTestResultDto(
    bool Ok,
    string? Code,
    int? ToolCount);

public sealed record NhAssistantMcpToolDto(
    string RemoteName,
    string LocalId,
    string Description,
    string? DescriptionOverride,
    bool IsEnabled,
    string Effect,
    string Status,
    bool? ReadOnlyHint);

public sealed record NhAssistantUpdateMcpToolRequest(
    bool? IsEnabled,
    string? Effect,
    string? DescriptionOverride);
