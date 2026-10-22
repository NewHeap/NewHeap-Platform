using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The one mapping of assistant failure codes to HTTP statuses, for every assistant endpoint: the
/// chat, the administration, agent runs, subjects, run histories, boards, MCP connections and OAuth.
/// Every code has exactly one status; an endpoint never chooses another status for a code. The
/// error body carries the code, its localization key and the field errors of the result.
/// </summary>
internal static class NhAssistantHttpErrors
{
    private static readonly FrozenDictionary<string, int> Statuses = Build();

    /// <summary>
    /// The HTTP status of <paramref name="code"/>; <c>400</c> for a code the table does not know,
    /// such as one an application's resolver returned.
    /// </summary>
    public static int StatusOf(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return Statuses.TryGetValue(code, out var status) ? status : StatusCodes.Status400BadRequest;
    }

    /// <summary>
    /// Whether the table names <paramref name="code"/> explicitly; every code of the assistant does.
    /// </summary>
    public static bool IsClassified(string code)
    {
        return Statuses.ContainsKey(code);
    }

    /// <summary>
    /// The error response of a failed result: its code with the code's status, and its field errors.
    /// A result without a code but with field errors is <c>assistant-validation</c>.
    /// </summary>
    public static IResult Failure(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var fieldErrors = NhAssistantResults.FieldErrorsOf(result);
        var code = NhAssistantResults.CodeOf(result)
            ?? (fieldErrors.Count > 0 ? NhAssistantAdminErrorCodes.ValidationFailed : NhAssistantErrorCodes.TurnFailed);
        return Error(code, fieldErrors);
    }

    /// <summary>
    /// The error response of <paramref name="code"/> with its status.
    /// </summary>
    public static IResult Error(string code)
    {
        return Error(code, null);
    }

    /// <summary>
    /// The error response of <paramref name="code"/> with its status and field errors.
    /// </summary>
    public static IResult Error(string code, IReadOnlyDictionary<string, string[]>? fieldErrors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return TypedResults.Json(
            new NhAssistantErrorDto(code, NhAssistantErrorCodes.MessageKey(code), fieldErrors is { Count: > 0 } ? fieldErrors : null),
            NhAssistantJsonSerializerContext.Default.NhAssistantErrorDto,
            statusCode: StatusOf(code));
    }

    private static FrozenDictionary<string, int> Build()
    {
        var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
        Add(statuses, StatusCodes.Status400BadRequest,
            NhAssistantErrorCodes.MessageInvalid,
            NhAssistantErrorCodes.MessageTooLong,
            NhAssistantErrorCodes.TitleInvalid,
            NhAssistantErrorCodes.ApprovalDecisionInvalid,
            NhAssistantErrorCodes.ApprovalExpired,
            NhAssistantErrorCodes.ApprovalInvalid,
            NhAssistantErrorCodes.BudgetExhausted,
            NhAssistantErrorCodes.ToolCallLimitReached,
            NhAssistantErrorCodes.TurnTimeout,
            NhAssistantErrorCodes.ModelUnavailable,
            NhAssistantErrorCodes.ClassificationNotPermitted,
            NhAssistantErrorCodes.TurnFailed,
            NhAssistantErrorCodes.PushSubscriptionInvalid,
            NhAssistantErrorCodes.SubjectInvalid,
            NhAssistantErrorCodes.RunTokenLimit,
            NhAssistantErrorCodes.ServiceAccountNotFound,
            NhAssistantErrorCodes.SkillVersionGone,
            NhAssistantErrorCodes.RunOperationCancelled,
            NhAssistantErrorCodes.RunOperationStalled,
            NhAssistantErrorCodes.RunOperationEnded,
            NhAssistantErrorCodes.SkillNotAvailable,
            NhAssistantErrorCodes.SkillSwitchLimit,
            NhAssistantErrorCodes.RunNotRetryable,
            NhAssistantErrorCodes.RunFilterInvalid,
            NhAssistantErrorCodes.BoardScopeInvalid,
            NhAssistantErrorCodes.ProposalAlreadyOpen,
            NhAssistantErrorCodes.ProposalRecentlyRejected,
            NhAssistantErrorCodes.ProposalSuperseded,
            NhAssistantErrorCodes.ProposalRefused,
            NhAssistantErrorCodes.ProposalPlaced,
            NhAssistantErrorCodes.ProposalNotPlaced,
            NhAssistantErrorCodes.ProposalNotNeeded,
            NhAssistantErrorCodes.MemoryNotConfigured,
            NhAssistantErrorCodes.MemoryInvalid,
            NhAssistantErrorCodes.ArtifactInvalid,
            NhAssistantErrorCodes.ArtifactOutsideTurn,
            NhAssistantErrorCodes.WebhookPayloadInvalid,
            NhAssistantErrorCodes.ConsultSubjectUnavailable,
            NhAssistantErrorCodes.ConsultAgentUnavailable,
            NhAssistantErrorCodes.ConsultDepthExceeded,
            NhAssistantErrorCodes.ConsultBudgetExhausted,
            NhAssistantErrorCodes.ConsultLimitReached,
            NhAssistantErrorCodes.ConsultTimeout,
            NhAssistantErrorCodes.ConsultFailed,
            NhAssistantErrorCodes.ConsultReadOnly,
            NhAssistantErrorCodes.ReplayNotShadow,
            NhAssistantErrorCodes.EvaluationNotVariant,
            NhAssistantErrorCodes.HandOffUnavailable,
            NhAssistantErrorCodes.HandOffNotAccepted,
            NhAssistantErrorCodes.HandOffSubjectUnavailable,
            NhAssistantErrorCodes.HandOffShadowToLive,
            NhAssistantErrorCodes.HandOffChainLimit,
            NhAssistantErrorCodes.HandOffLimitReached,
            NhAssistantErrorCodes.FeedbackInvalid,
            NhAssistantAdminErrorCodes.ValidationFailed,
            NhAssistantAdminErrorCodes.McpHostBlocked,
            NhAssistantAdminErrorCodes.McpSchemaChanged,
            NhAssistantAdminErrorCodes.McpToolDisabled,
            NhAssistantAdminErrorCodes.McpSecretReferenceInvalid,
            NhAssistantAdminErrorCodes.McpCertificateReferenceInvalid,
            NhAssistantAdminErrorCodes.McpScopeNotAllowed,
            NhAssistantAdminErrorCodes.McpAuthorizationFailed,
            NhAssistantAdminErrorCodes.McpAuthorizationExpired,
            NhAssistantAdminErrorCodes.McpAuthorizationInvalid,
            NhAssistantAdminErrorCodes.McpPkceUnsupported,
            NhAssistantAdminErrorCodes.McpClientRegistrationRequired,
            NhAssistantAdminErrorCodes.McpOAuthNotConfigured,
            NhAssistantAdminErrorCodes.InstructionsTooLong);
        Add(statuses, StatusCodes.Status401Unauthorized,
            NhAssistantErrorCodes.WebhookSignatureInvalid);
        Add(statuses, StatusCodes.Status403Forbidden,
            NhAssistantErrorCodes.AgentForbidden,
            NhAssistantErrorCodes.ContextUnavailable,
            NhAssistantErrorCodes.OwnerRequired,
            NhAssistantErrorCodes.ApprovalForbidden,
            NhAssistantErrorCodes.TurnForbidden,
            NhAssistantErrorCodes.SubjectAccessDenied,
            NhAssistantErrorCodes.ApprovalPolicyDenied,
            NhAssistantErrorCodes.FourEyesRequired,
            NhAssistantAdminErrorCodes.Forbidden,
            NhAssistantAdminErrorCodes.McpCredentialMismatch,
            NhAssistantAdminErrorCodes.McpInitiatorOnly);
        Add(statuses, StatusCodes.Status404NotFound,
            NhAssistantErrorCodes.Disabled,
            NhAssistantErrorCodes.ConversationNotFound,
            NhAssistantErrorCodes.AgentNotFound,
            NhAssistantErrorCodes.ApprovalNotFound,
            NhAssistantErrorCodes.ShareLinkInvalid,
            NhAssistantErrorCodes.ParticipantNotFound,
            NhAssistantErrorCodes.DirectoryUnavailable,
            NhAssistantErrorCodes.PushUnavailable,
            NhAssistantErrorCodes.SubjectNotFound,
            NhAssistantErrorCodes.SubjectTypeUnknown,
            NhAssistantErrorCodes.SkillNotFound,
            NhAssistantErrorCodes.AccountSearchUnavailable,
            NhAssistantErrorCodes.SubjectSearchUnavailable,
            NhAssistantErrorCodes.SkillCandidateNotFound,
            NhAssistantErrorCodes.RunNotFound,
            NhAssistantErrorCodes.BoardNotFound,
            NhAssistantErrorCodes.ArtifactNotFound,
            NhAssistantErrorCodes.TriggerNotFound,
            NhAssistantAdminErrorCodes.NotFound,
            NhAssistantAdminErrorCodes.ContextNotFound,
            NhAssistantAdminErrorCodes.McpServerNotFound,
            NhAssistantAdminErrorCodes.McpToolNotFound);
        Add(statuses, StatusCodes.Status409Conflict,
            NhAssistantErrorCodes.ConversationBusy,
            NhAssistantErrorCodes.MessageDuplicate,
            NhAssistantErrorCodes.ApprovalNotPending,
            NhAssistantErrorCodes.ProposalHashMismatch,
            NhAssistantErrorCodes.ParticipantLimitReached,
            NhAssistantErrorCodes.NoAccountableOwner,
            NhAssistantErrorCodes.AgentDisabled,
            NhAssistantErrorCodes.AgentStopped,
            NhAssistantErrorCodes.AgentConfiguredOff,
            NhAssistantErrorCodes.AgentPaused,
            NhAssistantErrorCodes.SkillStopped,
            NhAssistantErrorCodes.SkillPaused,
            NhAssistantErrorCodes.AgentOff,
            NhAssistantErrorCodes.SkillOff,
            NhAssistantErrorCodes.AgentDoesNotWorkOn,
            NhAssistantErrorCodes.MovedToShadow,
            NhAssistantErrorCodes.ShadowVariant,
            NhAssistantErrorCodes.ServiceAccountNotUsed,
            NhAssistantErrorCodes.SkillInCode,
            NhAssistantErrorCodes.TriggerInCode,
            NhAssistantErrorCodes.SkillInUse,
            NhAssistantErrorCodes.ConfigurationLimit,
            NhAssistantErrorCodes.RunSettingsInCode,
            NhAssistantErrorCodes.RunSettingsInUse,
            NhAssistantErrorCodes.AgentNotConfigured,
            NhAssistantErrorCodes.ExecutionNotUncertain,
            NhAssistantErrorCodes.ProposalStale,
            NhAssistantErrorCodes.MemoryConflict,
            NhAssistantErrorCodes.ReplayActive,
            NhAssistantErrorCodes.FeedbackNotAccepted,
            NhAssistantTriggerReasons.RateLimited,
            NhAssistantAdminErrorCodes.VersionConflict,
            NhAssistantAdminErrorCodes.AgentExists,
            NhAssistantAdminErrorCodes.CodeAgentNotDeletable,
            NhAssistantAdminErrorCodes.NotCodeAgent,
            NhAssistantAdminErrorCodes.McpServerExists,
            NhAssistantAdminErrorCodes.McpHeadlessNotAllowed,
            NhAssistantAdminErrorCodes.McpConsentRequired,
            NhAssistantAdminErrorCodes.McpConnectionRequired,
            NhAssistantAdminErrorCodes.McpReauthorizationRequired,
            NhAssistantAdminErrorCodes.McpIssuerChanged,
            NhAssistantAdminErrorCodes.McpAuthorizationNotConfirmed,
            NhAssistantAdminErrorCodes.McpConnectionNotSupported,
            NhAssistantAdminErrorCodes.McpSecretUnreadable);
        Add(statuses, StatusCodes.Status413PayloadTooLarge,
            NhAssistantErrorCodes.WebhookPayloadTooLarge);
        Add(statuses, StatusCodes.Status429TooManyRequests,
            NhAssistantAdminErrorCodes.McpTooManyAuthorizations);
        // Failures of a remote MCP or OAuth server.
        Add(statuses, StatusCodes.Status502BadGateway,
            NhAssistantAdminErrorCodes.McpUnreachable,
            NhAssistantAdminErrorCodes.McpUnauthorized,
            NhAssistantAdminErrorCodes.McpInsufficientScope,
            NhAssistantAdminErrorCodes.McpDelegatedTokenUnavailable,
            NhAssistantAdminErrorCodes.McpDiscoveryFailed,
            NhAssistantAdminErrorCodes.McpTokenRequestFailed);
        Add(statuses, StatusCodes.Status503ServiceUnavailable,
            NhAssistantErrorCodes.ProposalCheckFailed,
            NhAssistantRunStore.OrphanedRunCode,
            NhAssistantAdminErrorCodes.McpSecretUnavailable);
        return statuses.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Add(Dictionary<string, int> statuses, int status, params string[] codes)
    {
        foreach (var code in codes)
        {
            // A code listed twice would have two statuses; the table refuses that at startup.
            if (!statuses.TryAdd(code, status))
            {
                throw new InvalidOperationException($"The assistant error code '{code}' has more than one HTTP status.");
            }
        }
    }
}
