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
/// <param name="ActiveActorId">The owner or participant whose turn runs or waits for approval. Only that person decides the approval.</param>
/// <param name="Members">The owner followed by the participants; empty while the conversation is not shared.</param>
/// <param name="ShareToken">The current invitation-link token; only returned to the owner.</param>
/// <param name="CurrentActorId">The caller's actor id, to recognize the caller's own messages.</param>
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
    string? CurrentActorId = null);

/// <param name="Sequence">Position of the message in the conversation.</param>
/// <param name="AuthorActorId">The writer of a user message; <see langword="null"/> for assistant messages.</param>
public sealed record NhAssistantMessageDto(
    Guid Id,
    string Role,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NhAssistantMessagePartDto> Parts,
    int Sequence = 0,
    string? AuthorActorId = null);

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
    string? ResultCode) : NhAssistantMessagePartDto;

public sealed record NhAssistantPresentationFieldDto(string Label, string Value);

public sealed record NhAssistantApprovalPresentationDto(
    string ToolDisplayName,
    string Summary,
    IReadOnlyList<NhAssistantPresentationFieldDto> Fields,
    string? Notice);

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
    string Status) : NhAssistantMessagePartDto;

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
    int? ExpectedVersion = null);

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
    DateTimeOffset UpdatedAt);

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
    bool? IsEnabled);

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
    IReadOnlyList<string> AssignedAgentIds);

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
