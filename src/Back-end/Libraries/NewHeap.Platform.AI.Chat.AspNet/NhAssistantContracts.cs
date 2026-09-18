using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat.AspNet;

public sealed record NhAssistantLimitsDto(
    int MaxMessageChars,
    int MaxToolCallsPerTurn);

public sealed record NhAssistantStatusDto(
    bool Enabled,
    IReadOnlyList<NhAssistantAgentSummaryDto> Agents,
    NhAssistantLimitsDto Limits,
    bool CanAdminister);

public sealed record NhAssistantAgentSummaryDto(
    string Id,
    int Version,
    string DisplayNameKey,
    string DescriptionKey,
    bool CanMutate);

public sealed record NhAssistantConversationSummaryDto(
    Guid Id,
    string AgentId,
    string? Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NhAssistantConversationListDto(
    IReadOnlyList<NhAssistantConversationSummaryDto> Items,
    int Total);

/// <summary>
/// A conversation with its messages. <see cref="PendingApproval"/> is typed as a message part so the
/// <c>type: "approval"</c> discriminator is part of the payload.
/// </summary>
public sealed record NhAssistantConversationDto(
    Guid Id,
    string AgentId,
    string? Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int AgentVersion,
    IReadOnlyList<NhAssistantMessageDto> Messages,
    NhAssistantMessagePartDto? PendingApproval);

public sealed record NhAssistantMessageDto(
    Guid Id,
    string Role,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NhAssistantMessagePartDto> Parts);

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

public sealed record NhAssistantApprovalPartDto(
    Guid ApprovalId,
    Guid ProposalId,
    string ProposalHash,
    string ToolId,
    string Summary,
    string ArgumentsPreview,
    IReadOnlyList<string> Targets,
    DateTimeOffset ExpiresAt,
    string Status) : NhAssistantMessagePartDto;

public sealed record NhAssistantCreateConversationRequest(
    string? AgentId,
    string? Title);

public sealed record NhAssistantSendMessageRequest(
    string? Text,
    string? ClientMessageId);

public sealed record NhAssistantDecideApprovalRequest(
    string? Decision,
    string? ExpectedProposalHash,
    string? Reason);

public sealed record NhAssistantErrorDto(
    string Code,
    string MessageKey);

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
[JsonSerializable(typeof(NhAssistantDecideApprovalRequest))]
[JsonSerializable(typeof(NhAssistantErrorDto))]
[JsonSerializable(typeof(NhAssistantTurnStartedDto))]
[JsonSerializable(typeof(NhAssistantMessageDeltaDto))]
[JsonSerializable(typeof(NhAssistantToolStartedDto))]
[JsonSerializable(typeof(NhAssistantToolCompletedDto))]
[JsonSerializable(typeof(NhAssistantTurnCompletedDto))]
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
