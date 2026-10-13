using System.Text.Json;
using System.Text.Json.Serialization;
using NewHeap.Platform.AI.Chat.Entities;
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
    int WaitingInputs);

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
    Guid? BackgroundOperationId);

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
    /// The consultation that proposed it, when another assistant consulted the agent on behalf of
    /// <see cref="InitiatorActorId"/>. It executes with that person's rights only.
    /// </summary>
    public Guid? ConsultationId { get; init; }
}

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
            run.BackgroundOperationId);
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
                NhAssistantToolPresentationResolver.Deserialize(approval.PresentationJson)),
            approval.ArgumentsPreview,
            ReadTargets(approval.TargetsJson),
            approval.Status,
            approval.Mode,
            approval.PolicyId,
            approval.GrantSource.ToString().ToLowerInvariant(),
            approval.CreatedAt,
            approval.ExpiresAt,
            approval.InitiatorActorId,
            approval.DecidedByActorId,
            approval.DecidedAt,
            approval.ExecutionResultCode,
            approval.ExecutedAt)
        {
            ConsultationId = approval.ConsultationId
        };
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
