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
    bool CanMutate)
{
    /// <summary>
    /// What people may attach to a message to this agent; <see langword="null"/> when they can attach
    /// nothing: the application registered no file source, accepts no media types for uploads, or the
    /// agent accepts no files.
    /// </summary>
    public NhAssistantAgentUploadsDto? Uploads { get; init; }
}

/// <summary>
/// What people may upload with <c>POST conversations/{id}/files</c> and attach to one message to an
/// agent.
/// </summary>
/// <param name="AcceptedMediaTypes">The media types people may upload, lowercase.</param>
/// <param name="MaxFileBytes">The largest file in bytes: the application's limit or the agent's lower one.</param>
/// <param name="MaxFiles">The most files of one message.</param>
public sealed record NhAssistantAgentUploadsDto(
    IReadOnlyList<string> AcceptedMediaTypes,
    int MaxFileBytes,
    int MaxFiles)
{
    /// <summary>
    /// The file name extensions the application mapped to one of <see cref="AcceptedMediaTypes"/>
    /// (<c>NhAssistantUploadOptions.MapExtension</c>), ordered by extension. A client derives the type
    /// of a file without a usable type from them before the platform's table, as the server does, and
    /// offers them in its file picker.
    /// </summary>
    public IReadOnlyList<NhAssistantUploadExtensionDto> MappedExtensions { get; init; } = [];
}

/// <summary>
/// A file name extension the application mapped to an accepted media type for uploads.
/// </summary>
/// <param name="Extension">The extension in lowercase with its dot, such as <c>.dwg</c>.</param>
/// <param name="MediaType">The accepted media type it names, such as <c>application/acad</c>.</param>
public sealed record NhAssistantUploadExtensionDto(
    string Extension,
    string MediaType);

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
    IReadOnlyList<NhAssistantThreadDecisionDto> Decisions)
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

/// <param name="Role"><c>user</c>, <c>assistant</c>, or <c>application</c> for a message the application
/// posted (see <see cref="NhAssistantMessageRoles"/>).</param>
/// <param name="Sequence">Position of the message in the conversation.</param>
/// <param name="AuthorActorId">The writer of a user message; <see langword="null"/> for assistant messages.</param>
/// <param name="AuthorKind">
/// <c>person</c> for a user message a person wrote, <c>trigger</c> for one a trigger wrote into an agent
/// thread, <c>rule</c> for the agent's message about what its rules did without a model,
/// <c>application</c> for a message the application posted (see
/// <see cref="NhAssistantMessageAuthorKinds"/>); <see langword="null"/> for the model's messages.
/// </param>
public sealed record NhAssistantMessageDto(
    Guid Id,
    string Role,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NhAssistantMessagePartDto> Parts,
    int Sequence = 0,
    string? AuthorActorId = null,
    string? AuthorKind = null)
{
    /// <summary>
    /// The title, sender, link and trusted flag of a message the application posted; left out for
    /// other messages. Its text is the message's <c>text</c> part.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NhAssistantApplicationMessageDto? Application { get; init; }
}

/// <summary>
/// What the application said about a message it posted, besides its text.
/// </summary>
/// <param name="Title">A short heading above the text.</param>
/// <param name="SenderName">Who in the application posted it; the chat shows the message as the
/// application's either way.</param>
/// <param name="Link">Where people continue: a path within the application or an <c>https</c> address.</param>
/// <param name="Trusted">Whether the assistant follows the message as the application's instruction
/// (<see cref="NhAssistantApplicationMessage.Trusted"/>); <see langword="false"/> for a message the
/// assistant reads as information. Always present, so people can see what the assistant follows.</param>
public sealed record NhAssistantApplicationMessageDto(
    string? Title,
    string? SenderName,
    NhAssistantApplicationMessageLinkDto? Link,
    bool Trusted);

/// <param name="Url">A path within the application, such as <c>/exports/42</c>, or an <c>https</c> address.</param>
/// <param name="Label">The text of the link; the chat shows its own text without one.</param>
public sealed record NhAssistantApplicationMessageLinkDto(string Url, string? Label);

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
[JsonDerivedType(typeof(NhAssistantConnectionRequiredPartDto), "connection-required")]
[JsonDerivedType(typeof(NhAssistantFilePartDto), "file")]
public abstract record NhAssistantMessagePartDto;

/// <summary>
/// A file a person attached to the message: its name, media type and size in bytes, never its
/// content. The application keeps the file; a later turn opens it again with the rights of that turn.
/// </summary>
public sealed record NhAssistantFilePartDto(string Name, string MediaType, long Size) : NhAssistantMessagePartDto;

/// <summary>
/// The person had to connect an MCP server, which each person connects to with OAuth, before the
/// agent could use it: <c>not-connected</c>, <c>reauthorization-required</c> or
/// <c>insufficient-scope</c> (<see cref="NhAssistantMcpConnectionReasons"/>).
/// </summary>
public sealed record NhAssistantConnectionRequiredPartDto(string ServerId, string DisplayName, string Reason) : NhAssistantMessagePartDto;

/// <summary>The data of the <c>connection.required</c> event of a turn.</summary>
public sealed record NhAssistantConnectionRequiredDto(string ServerId, string DisplayName, string Reason);

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

    /// <summary>
    /// The consultation that proposed it, when the conversation's assistant consulted another agent
    /// and the person who asked decides its proposal here (<c>AskerDecides()</c>). Approving executes
    /// it in this request with the person's own rights. Only written for such proposals.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NhAssistantApprovalConsultationDto? Consultation { get; init; }
}

/// <summary>
/// Where a proposal the person who asked decides in their own conversation comes from.
/// </summary>
/// <param name="ConsultationId">The consultation record in the consulted agent's thread.</param>
/// <param name="AgentId">The consulted agent that proposed it.</param>
/// <param name="SubjectType">The type of the subject the consultation was about.</param>
/// <param name="SubjectId">The id of that subject.</param>
/// <param name="SubjectDisplayName">The subject's name as the consulted agent's thread knows it.</param>
public sealed record NhAssistantApprovalConsultationDto(
    Guid ConsultationId,
    string AgentId,
    string SubjectType,
    string SubjectId,
    string? SubjectDisplayName)
{
    /// <summary>
    /// The route at the subject the person may leave the pending proposal for with the decision
    /// <c>leave</c>: <c>subject-agent-rights</c> or <c>subject-asker-rights</c>; <see langword="null"/>
    /// when the person can only approve or reject it.
    /// </summary>
    public string? LeaveRoute { get; init; }

    /// <summary>
    /// The consulted agent's answer, bounded, when the consultation's classification allows a preview.
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>
    /// The proposal that waits at the subject since the person left this one for its approvers.
    /// </summary>
    public Guid? MovedToApprovalId { get; init; }
}

public sealed record NhAssistantCreateConversationRequest(
    string? AgentId,
    string? Title);

/// <param name="ClientContext">
/// Optional page context (<see cref="NhAssistantClientContextDto"/>). It is read leniently: a value of
/// the wrong shape is ignored instead of rejecting the message.
/// </param>
/// <param name="Files">
/// The files the person attaches, as <c>POST conversations/{id}/files</c> returned them, at most the
/// agent's <see cref="NhAssistantAgentUploadsDto.MaxFiles"/>. The turn opens each through the
/// application's file source with the sender's rights and ends with the code of the first refusal.
/// </param>
public sealed record NhAssistantSendMessageRequest(
    string? Text,
    string? ClientMessageId,
    JsonElement? ClientContext = null,
    IReadOnlyList<NhAiFileReference>? Files = null);

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

/// <param name="Decision">
/// <c>approve</c> or <c>reject</c>; for a proposal of a consultation that the person who asked decides
/// here also <c>leave</c>, which leaves it for the subject's approvers when a route at the subject
/// applies (<c>consultation.leaveRoute</c> on the approval).
/// </param>
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
[JsonSerializable(typeof(NhAssistantMcpAuthModeDto[]))]
[JsonSerializable(typeof(NhAssistantMcpServerInputDto))]
[JsonSerializable(typeof(NhAssistantMcpServerTestResultDto))]
[JsonSerializable(typeof(NhAssistantMcpToolDto))]
[JsonSerializable(typeof(NhAssistantMcpToolDto[]))]
[JsonSerializable(typeof(NhAssistantUpdateMcpToolRequest))]
[JsonSerializable(typeof(NhAssistantMcpConnectionDto))]
[JsonSerializable(typeof(NhAssistantMcpConnectionDto[]))]
[JsonSerializable(typeof(NhAssistantUpdateMcpConnectionRequest))]
[JsonSerializable(typeof(NhAssistantMcpDiscoverRequest))]
[JsonSerializable(typeof(NhAssistantMcpDiscoveryDto))]
[JsonSerializable(typeof(NhAssistantMcpAuthorizeRequest))]
[JsonSerializable(typeof(NhAssistantMcpAuthorizationStartDto))]
[JsonSerializable(typeof(NhAssistantMcpCompleteRequest))]
[JsonSerializable(typeof(NhAssistantMcpCompletionDto))]
[JsonSerializable(typeof(NhAssistantMcpOrganizationConnectionDto))]
[JsonSerializable(typeof(NhAssistantMcpConnectionsSummaryDto))]
[JsonSerializable(typeof(NhAssistantConnectionRequiredDto))]
[JsonSerializable(typeof(NhAiFileReference))]
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
    string Description)
{
    /// <summary>
    /// The object type a call of the tool names, declared with <c>[NhAiSubject]</c>: a run places its
    /// proposal for another object at that object. <see langword="null"/> when the tool declares none,
    /// as MCP tools never do; their proposals stay in the run's own thread.
    /// </summary>
    public string? SubjectType { get; init; }
}

/// <summary>
/// Agent input. <see cref="ExpectedVersion"/> is required on update and ignored on create.
/// <see cref="IsEnabled"/> is accepted and ignored until the next major version: the run state
/// changes with <c>POST admin/agents/{id}/state</c>, and a new agent starts active, or off with
/// <see cref="StartsOff"/>.
/// <see cref="OfferedInChat"/> applies to agents created in the administration: omitted keeps it on
/// update and offers a new agent in the chat; agents in code take it from <c>HideFromChat()</c>.
/// What the agent works on (<see cref="WorksOnSubjectTypes"/>, <see cref="WorksOnAllSubjects"/>,
/// <see cref="WorksOnApplication"/>, <see cref="PerDivision"/>, <see cref="RunPolicy"/> and
/// <see cref="ViewPolicy"/>) also applies to agents created in the administration and is sent
/// together: when all six are omitted an update keeps it and a new agent works on all object types;
/// otherwise the omitted ones read as not chosen, and without types, all objects or the application
/// the agent works on all object types. Agents in code take it from <c>WorksOn</c>.
/// </summary>
/// <param name="WorksOnSubjectTypes">Registered object types, at most 20; field errors
/// <c>worksOnSubjectTypes</c>.</param>
/// <param name="WorksOnAllSubjects">Every object type; not next to types (field error
/// <c>worksOn</c>).</param>
/// <param name="WorksOnApplication">The whole application.</param>
/// <param name="PerDivision">Per division; only when the run catalog reports <c>divisions</c>.</param>
/// <param name="RunPolicy">A policy of the run catalog's <c>agentPolicies</c>, or empty for the
/// administration policy.</param>
/// <param name="ViewPolicy">A policy of the run catalog's <c>agentPolicies</c>, or empty for the
/// agent's required policy.</param>
/// <param name="StartsOff">On create only: the new agent starts off (<c>off</c>), so its triggers are
/// ignored until someone switches it on with <c>POST admin/agents/{id}/state</c>. Omitted or
/// <see langword="false"/> starts it active; ignored on update.</param>
/// <param name="Profile">For an agent created in the administration: the model profile of its model
/// calls, one of the run catalog's <c>profiles</c> (field error <c>profile: not-found</c> otherwise).
/// Omitted keeps it on update; a new agent uses the application's chat profile. Agents in code take
/// it from their code.</param>
/// <param name="DataClassification">For an agent created in the administration: the classification of
/// the data its model calls carry, in people's chats with it and in its runs, one of
/// <c>public</c>, <c>internal</c>, <c>confidential</c> or <c>restricted</c> (field error
/// <c>dataClassification: invalid</c> otherwise). A skill's own wins. Omitted keeps it on update; a new
/// agent has none, so its model calls are <c>internal</c>. Agents in code take it from their
/// code.</param>
/// <param name="Metadata">For an agent created in the administration: values of entries of agent
/// metadata registered with <c>AddAgentMetadata</c>, by entry name, saved with the definition's
/// version; <c>null</c> removes an entry's value and entries left out keep theirs. They are checked as
/// <c>PUT agents/{id}/metadata/{name}</c> checks them, with field errors <c>metadata.&lt;name&gt;</c>
/// (<c>not-found</c> for an entry that is not registered). An agent in code changes its editable entries
/// one by one (field error <c>metadata: not-allowed</c>).</param>
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
    bool? OfferedInChat = null,
    IReadOnlyList<string>? WorksOnSubjectTypes = null,
    bool? WorksOnAllSubjects = null,
    bool? WorksOnApplication = null,
    bool? PerDivision = null,
    string? RunPolicy = null,
    string? ViewPolicy = null,
    bool? StartsOff = null,
    string? Profile = null,
    string? DataClassification = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null)
{
    /// <summary>
    /// For an agent created in the administration: the asset of the application its instructions come
    /// from, instead of <see cref="Instructions"/>, which is then empty or omitted
    /// (<c>instructions: not-allowed</c> otherwise). The save asks the application's text asset source:
    /// <c>instructionsAsset: not-allowed</c> without a source or for an agent in code,
    /// <c>instructionsAsset: not-found</c> for an asset or version it does not have,
    /// <c>instructionsAsset.hash: invalid</c> for another hash, <c>instructionsAsset: invalid</c> for an
    /// asset that cannot be instructions, and <c>503 assistant-instructions-asset-unavailable</c> when the
    /// source fails. Omitted or <see langword="null"/> keeps or makes the instructions text.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }

    /// <summary>
    /// On create only: the new agent starts in shadow, so once it has run settings, also when they are
    /// saved right after, its runs work in its shadow threads until someone puts it live with
    /// <c>POST admin/agents/{id}/shadow</c>. Omitted or <see langword="false"/> starts it live; ignored
    /// on update.
    /// </summary>
    public bool? StartsInShadow { get; init; }

    /// <summary>
    /// On create only: the live agent the new agent is a data shadow variant of, within
    /// <c>UseShadowVariantsAsData</c>. The variant receives every trigger of the live agent, always
    /// works in shadow and is never offered in the chat. A live agent the ceiling does not allow answers
    /// <c>409 assistant-shadow-variants-not-allowed</c>, one without a run configuration the field error
    /// <c>shadowOf: not-found</c>, one that is no live agent <c>shadowOf: invalid</c> and one with as many
    /// data variants as allowed <c>409 assistant-configuration-limit</c>. Ignored on update: a variant
    /// never changes its live agent. A variant ignores <see cref="StartsInShadow"/>: it always works in
    /// shadow.
    /// </summary>
    public string? ShadowOf { get; init; }
}

/// <param name="IsEnabled"><see langword="false"/> while the agent is stopped; see <paramref name="RunState"/>.</param>
/// <param name="RunState"><c>active</c>, <c>paused</c>, <c>stopped</c>, <c>off</c> or <c>configured-off</c>.</param>
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

    /// <summary>
    /// The model profile of the agent's model calls: from its code for an agent in code, chosen in
    /// the administration for one created there. A skill may use another.
    /// </summary>
    public string Profile { get; init; } = string.Empty;

    /// <summary>
    /// The classification of the data the agent's model calls carry, one of
    /// <see cref="NhAssistantDataClassificationNames"/>: from its code for an agent in code, chosen in
    /// the administration for one created there. <see langword="null"/> when it has none, so its model
    /// calls are <c>internal</c>. A skill's own wins.
    /// </summary>
    public string? DataClassification { get; init; }

    /// <summary>
    /// Why the agent, saved and in effect, may still not work as configured, as
    /// <see cref="NhAssistantConfigurationWarnings"/>:
    /// <see cref="NhAssistantConfigurationWarnings.ClassificationNotPermitted"/> when neither its
    /// profile nor any of the profile's fallbacks permits its classification. Empty without warnings.
    /// A warning never refuses a save.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// The object types the agent works on, ordered by id; empty with
    /// <see cref="WorksOnAllSubjects"/> or for an agent that works on the application only.
    /// </summary>
    public IReadOnlyList<string> WorksOnSubjectTypes { get; init; } = [];

    /// <summary>
    /// Whether the agent works on every object type.
    /// </summary>
    public bool WorksOnAllSubjects { get; init; } = true;

    /// <summary>
    /// Whether the agent works on the whole application.
    /// </summary>
    public bool WorksOnApplication { get; init; }

    /// <summary>
    /// Whether the agent works per division instead of app-wide.
    /// </summary>
    public bool PerDivision { get; init; }

    /// <summary>
    /// Who may run the agent for the whole application; <see langword="null"/>: the administration
    /// policy.
    /// </summary>
    public string? RunPolicy { get; init; }

    /// <summary>
    /// Who sees the agent's results for the whole application; <see langword="null"/>: the agent's
    /// required policy.
    /// </summary>
    public string? ViewPolicy { get; init; }

    /// <summary>
    /// Whether the agent says what it works on: always for an agent created in the administration;
    /// for an agent in code only with <c>WorksOn</c>, <c>WorksOnAllObjects</c> or
    /// <c>WorksOnApplication</c>. An agent that does not say works on all object types.
    /// </summary>
    public bool WorksOnDeclared { get; init; } = true;

    /// <summary>
    /// The object types the agent is known to work on, as a hint next to "all objects": those it
    /// accepts hand-offs and consultations about, those its triggers name up front and those of its
    /// existing threads, ordered by id.
    /// </summary>
    public IReadOnlyList<string> KnownSubjectTypes { get; init; } = [];

    /// <summary>
    /// The groups of agents the agent is in now, ordered by id: an administrator's choice per group,
    /// otherwise its <c>InGroup</c> in code. Change them with <c>PUT admin/agents/{id}/groups</c>.
    /// </summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>
    /// The groups the agent joins in code, ordered by id; empty for an agent created in the
    /// administration.
    /// </summary>
    public IReadOnlyList<string> CodeGroups { get; init; } = [];

    /// <summary>
    /// <see langword="true"/> while the agent is stopped and was off when it stopped: releasing the
    /// kill switch switches it back off, while resuming switches it on. <see langword="false"/>
    /// otherwise (<see cref="NewHeap.Platform.AI.Chat.AspNet.AgentRuns.NhAssistantAgentState.OffBeforeStop"/>).
    /// </summary>
    public bool OffBeforeStop { get; init; }

    /// <summary>
    /// The asset of the application an agent created in the administration takes its instructions
    /// from; <see langword="null"/> when they are text. <see cref="Instructions"/> then holds the
    /// asset's verified text, read-only, and <see cref="InstructionsHash"/> the hash the agent was saved
    /// with.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }

    /// <summary>
    /// Why the instructions of <see cref="InstructionsAsset"/> are not available, as
    /// <c>no-asset-source</c>, <c>asset-missing</c>, <c>asset-changed</c>, <c>asset-invalid</c> or
    /// <c>asset-unavailable</c>: the agent then starts no model turn
    /// (<c>assistant-agent-instructions-unavailable</c>) and <see cref="Instructions"/> is empty.
    /// <see langword="null"/> when they resolve.
    /// </summary>
    public string? InstructionsProblem { get; init; }
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

    /// <summary>
    /// For <c>oauth</c>: <c>organization</c> (one connection for everyone) or <c>user</c> (each person
    /// connects). Omitted or null keeps the stored value; a new server starts at <c>organization</c>.
    /// </summary>
    public string? ConnectionScope { get; init; }

    /// <summary>The OAuth settings of <c>oauth</c> and <c>oauth-client-credentials</c>.</summary>
    [JsonPropertyName("oauth")]
    public NhAssistantMcpOAuthInputDto? OAuth { get; init; }

    /// <summary>
    /// The readers of the server: synced tools that turn files of their media types into text for
    /// models that do not read them, at most 16. Omitted or null keeps the stored readers; an empty
    /// list removes them all. A field error of a reader names its index, such as
    /// <c>readers[0].toolName</c>.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpReaderInputDto?>? Readers { get; init; }
}

/// <summary>
/// One reader of an MCP server. <see cref="MediaTypes"/>: one to 16 media types, such as
/// <c>application/vnd.openxmlformats-officedocument.wordprocessingml.document</c>.
/// <see cref="ToolName"/>: the remote name of a tool synced from the server, one reader per tool, which
/// receives <c>name</c>, <c>mediaType</c> and the base64 <c>content</c> of a file and returns its text.
/// <see cref="MaxClassification"/>: the highest classification that may be sent to the tool,
/// <c>public</c>, <c>internal</c>, <c>confidential</c> or <c>restricted</c>. <see cref="Priority"/>:
/// from -1000 to 1000, omitted 0; the MCP readers of every server that read a file's type are asked in
/// order of priority, highest first, then by server id and tool name, after the application's own.
/// </summary>
public sealed record NhAssistantMcpReaderInputDto(
    IReadOnlyList<string>? MediaTypes,
    string? ToolName,
    string? MaxClassification,
    int? Priority = null);

/// <summary>
/// One reader of an MCP server as stored: the media types it reads, the tool that turns a file into
/// text, the highest classification that may be sent to it and its priority, higher first.
/// </summary>
public sealed record NhAssistantMcpReaderDto(
    IReadOnlyList<string> MediaTypes,
    string ToolName,
    string MaxClassification,
    int Priority);

/// <summary>
/// OAuth settings of an MCP server. <see cref="ClientSecret"/> is write-only. Omitted or null values
/// keep the stored value, an empty string clears it. <see cref="ConfirmAuthorization"/> runs discovery
/// again and pins the authorization server and resource it finds; a new OAuth server and a changed
/// address need it.
/// </summary>
public sealed record NhAssistantMcpOAuthInputDto(
    string? ClientId,
    string? ClientSecret,
    string? ClientSecretReference,
    string? Scopes,
    string? OrganizationAccountLabel,
    bool? ConfirmAuthorization);

/// <summary>The OAuth settings of an MCP server as returned: never the client secret.</summary>
public sealed record NhAssistantMcpOAuthDto(
    string? ClientId,
    bool HasClientSecret,
    string? ClientSecretReference,
    string? Scopes,
    string? OrganizationAccountLabel);

/// <summary>The authorization server and resource an administrator confirmed (pinned).</summary>
public sealed record NhAssistantMcpAuthorizationDto(
    string Issuer,
    string Resource,
    DateTimeOffset ConfirmedAt,
    string? ConfirmedBy);

/// <summary>
/// The organization's OAuth connection of a server: <c>not-connected</c>, <c>active</c>,
/// <c>reauthorization-required</c> or <c>issuer-changed</c>, without tokens.
/// </summary>
public sealed record NhAssistantMcpOrganizationConnectionDto(
    string Status,
    DateTimeOffset? ConnectedAt,
    string? ConnectedBy,
    string? GrantedScopes,
    string? RequiredScopes,
    DateTimeOffset? AccessTokenExpiresAt,
    string? LastFailureCode);

public sealed record NhAssistantMcpDiscoverRequest(string? Url);

/// <summary>
/// The result of discovery for an MCP server URL; nothing is stored. Saving the server with
/// <c>oauth.confirmAuthorization</c> pins the authorization server and resource.
/// </summary>
public sealed record NhAssistantMcpDiscoveryDto(
    string Resource,
    string Issuer,
    IReadOnlyList<string> AuthorizationServers,
    IReadOnlyList<string> ChallengedScopes,
    IReadOnlyList<string> ScopesSupported,
    IReadOnlyList<string> GrantTypesSupported,
    bool PkceSupported,
    bool ClientIdMetadataDocumentSupported,
    bool DynamicRegistrationSupported,
    IReadOnlyList<string> TokenEndpointAuthMethods);

/// <summary>
/// The start of an authorization. <see cref="ReturnUrl"/> chooses redirect mode: the callback returns
/// there with the flow and completion code in the fragment; its origin must be allowed. For a personal
/// connection <see cref="AllowBackgroundUse"/> records the consent to background use with it.
/// </summary>
public sealed record NhAssistantMcpAuthorizeRequest(string? ReturnUrl, bool? AllowBackgroundUse);

/// <summary>
/// The flow and the URL the owner's browser opens; it expires at <see cref="ExpiresAt"/>. The page
/// accepts the callback's message only from <see cref="CallbackOrigin"/>, the origin of the redirect URI.
/// </summary>
public sealed record NhAssistantMcpAuthorizationStartDto(Guid FlowId, string AuthorizationUrl, DateTimeOffset ExpiresAt, string CallbackOrigin);

/// <summary>The flow and the one-time completion code the callback page handed to the starter's browser.</summary>
public sealed record NhAssistantMcpCompleteRequest(Guid? FlowId, string? CompletionCode);

/// <summary>The connection a completed authorization activated.</summary>
public sealed record NhAssistantMcpCompletionDto(string ServerId, string OwnerKind, string Status, string? GrantedScopes);

/// <summary>The personal connections of a server per status.</summary>
public sealed record NhAssistantMcpConnectionsSummaryDto(int Active, int ReauthorizationRequired, int IssuerChanged);

/// <summary>
/// An authentication mode the application registered in code (<c>AddMcpCredentialProvider</c>):
/// its name and whose credential it sends, <c>organization</c> or <c>person</c>.
/// </summary>
public sealed record NhAssistantMcpAuthModeDto(string Mode, string Ownership);

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

    /// <summary><c>organization</c>, or <c>user</c> for an <c>oauth</c> server each person connects to.</summary>
    public string ConnectionScope { get; init; } = NhAssistantMcpConnectionScopes.Organization;

    /// <summary>The OAuth settings of an OAuth server; null for other modes.</summary>
    [JsonPropertyName("oauth")]
    public NhAssistantMcpOAuthDto? OAuth { get; init; }

    /// <summary>The confirmed authorization server and resource; null until an administrator confirmed them.</summary>
    public NhAssistantMcpAuthorizationDto? Authorization { get; init; }

    /// <summary>The organization's OAuth connection; null for servers without one.</summary>
    public NhAssistantMcpOrganizationConnectionDto? OrganizationConnection { get; init; }

    /// <summary>
    /// The readers of the server, by priority, highest first, then by tool name; empty when the server
    /// reads no files.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpReaderDto> Readers { get; init; } = [];
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
    IReadOnlyList<string> AgentIds)
{
    /// <summary>
    /// For a server each person connects to with OAuth: <c>not-connected</c>, <c>active</c>,
    /// <c>reauthorization-required</c> or <c>issuer-changed</c>; null for a server that forwards the
    /// caller's token.
    /// </summary>
    public string? ConnectionStatus { get; init; }

    /// <summary>The scopes the caller's connection grants.</summary>
    public string? GrantedScopes { get; init; }

    /// <summary>Scopes the server asked for that the connection lacks; connecting again adds them.</summary>
    public string? RequiredScopes { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }

    /// <summary>Whether background use is offered for the server (<c>personal</c> headless use).</summary>
    public bool OffersBackgroundUse { get; init; } = true;
}

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
