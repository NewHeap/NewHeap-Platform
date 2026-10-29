using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB53: the public HTTP status of an assistant code (<see cref="NhAssistantHttpStatuses"/>), which an
/// application's channel answers a failed service result with and the endpoints answer the same code
/// with. The table is every code the endpoints answered as of 25.0.1, with its status then.
/// </summary>
public sealed class AssistantHttpStatusTests
{
    [Fact]
    public void Every_code_of_the_endpoints_keeps_its_status()
    {
        var changed = StatusesAsOf25
            .SelectMany(group => group.Codes.Select(code => (Code: code, Expected: group.Status)))
            .Where(item => NhAssistantHttpStatuses.Of(item.Code) != item.Expected)
            .Select(item => $"{item.Code}: {NhAssistantHttpStatuses.Of(item.Code)} instead of {item.Expected}")
            .ToArray();

        Assert.Empty(changed);
        Assert.Equal(225, StatusesAsOf25.Sum(group => group.Codes.Length));
    }

    [Fact]
    public async Task The_endpoints_answer_every_code_with_the_public_status()
    {
        foreach (var code in StatusesAsOf25.SelectMany(group => group.Codes))
        {
            var (status, body) = await ExecuteAsync(NhAssistantHttpErrors.Error(code));

            Assert.Equal(NhAssistantHttpStatuses.Of(code), status);
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("application-own-code")]
    [InlineData("sample-assistant-channel-delivery-invalid")]
    [InlineData("ASSISTANT-CONVERSATION-NOT-FOUND")]
    public async Task A_code_the_assistant_does_not_know_answers_400(string code)
    {
        var (status, _) = await ExecuteAsync(NhAssistantHttpErrors.Error(code));

        Assert.Equal(StatusCodes.Status400BadRequest, NhAssistantHttpStatuses.UnknownCodeStatus);
        Assert.Equal(NhAssistantHttpStatuses.UnknownCodeStatus, NhAssistantHttpStatuses.Of(code));
        Assert.Equal(NhAssistantHttpStatuses.UnknownCodeStatus, status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_code_is_the_callers_mistake(string? code)
    {
        Assert.ThrowsAny<ArgumentException>(() => NhAssistantHttpStatuses.Of(code!));
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>
    /// Every code the endpoints answered in 25.0.1 and its status then. A code added later gets its
    /// status in the endpoints' table; <c>AssistantHttpErrorTests</c> checks that every code has one.
    /// </summary>
    private static readonly (int Status, string[] Codes)[] StatusesAsOf25 =
    [
        (StatusCodes.Status400BadRequest,
        [
            NhAssistantErrorCodes.MessageInvalid,
            NhAssistantErrorCodes.MessageTooLong,
            NhAssistantErrorCodes.TitleInvalid,
            NhAssistantErrorCodes.ApprovalDecisionInvalid,
            NhAssistantErrorCodes.ExecutionReportInvalid,
            NhAssistantErrorCodes.ApprovalExpired,
            NhAssistantErrorCodes.ApprovalInvalid,
            NhAssistantErrorCodes.BudgetExhausted,
            NhAssistantErrorCodes.ToolCallLimitReached,
            NhAssistantErrorCodes.TurnTimeout,
            NhAssistantErrorCodes.ModelUnavailable,
            NhAssistantErrorCodes.ClassificationNotPermitted,
            NhAssistantErrorCodes.ModelRegionDenied,
            NhAssistantErrorCodes.FileReferenceInvalid,
            NhAssistantErrorCodes.FileUploadInvalid,
            NhAssistantErrorCodes.FileTooManyPages,
            NhAssistantErrorCodes.FileCountLimit,
            NhAssistantErrorCodes.FileTokenLimit,
            NhAssistantErrorCodes.TurnFailed,
            NhAssistantErrorCodes.PushSubscriptionInvalid,
            NhAssistantErrorCodes.SubjectInvalid,
            NhAssistantErrorCodes.SubjectRequired,
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
            NhAssistantErrorCodes.ConsultTurnLimit,
            NhAssistantErrorCodes.ConsultTimeout,
            NhAssistantErrorCodes.ConsultFailed,
            NhAssistantErrorCodes.ConsultReadOnly,
            NhAssistantErrorCodes.ConsultToolNotSelected,
            NhAssistantErrorCodes.CompositionOff,
            NhAssistantErrorCodes.ScriptingOff,
            NhAssistantErrorCodes.ReplayNotShadow,
            NhAssistantErrorCodes.EvaluationNotVariant,
            NhAssistantErrorCodes.HandOffUnavailable,
            NhAssistantErrorCodes.HandOffNotAccepted,
            NhAssistantErrorCodes.HandOffSubjectUnavailable,
            NhAssistantErrorCodes.HandOffShadowToLive,
            NhAssistantErrorCodes.HandOffChainLimit,
            NhAssistantErrorCodes.HandOffLimitReached,
            NhAssistantErrorCodes.FeedbackInvalid,
            NhAssistantErrorCodes.QuestionInvalid,
            NhAssistantErrorCodes.QuestionAnswerInvalid,
            NhAssistantErrorCodes.TriggerFilterNotFound,
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
            NhAssistantAdminErrorCodes.InstructionsTooLong,
            NhAssistantErrorCodes.PackageFormatUnsupported,
            NhAssistantErrorCodes.ProposalPresentationInvalid
        ]),
        (StatusCodes.Status401Unauthorized,
        [
            NhAssistantErrorCodes.WebhookSignatureInvalid
        ]),
        (StatusCodes.Status403Forbidden,
        [
            NhAssistantErrorCodes.AgentForbidden,
            NhAssistantErrorCodes.ContextUnavailable,
            NhAssistantErrorCodes.OwnerRequired,
            NhAssistantErrorCodes.ApprovalForbidden,
            NhAssistantErrorCodes.QuestionForbidden,
            NhAssistantErrorCodes.TurnForbidden,
            NhAssistantErrorCodes.SubjectAccessDenied,
            NhAssistantErrorCodes.ApprovalPolicyDenied,
            NhAssistantErrorCodes.FourEyesRequired,
            NhAssistantErrorCodes.FileForbidden,
            NhAssistantErrorCodes.ServiceAccountNotPermitted,
            NhAssistantAdminErrorCodes.Forbidden,
            NhAssistantAdminErrorCodes.McpCredentialMismatch,
            NhAssistantAdminErrorCodes.McpInitiatorOnly,
            NhAssistantErrorCodes.NotAllowedWhileImpersonating
        ]),
        (StatusCodes.Status404NotFound,
        [
            NhAssistantErrorCodes.Disabled,
            NhAssistantErrorCodes.ConversationNotFound,
            NhAssistantErrorCodes.AgentNotFound,
            NhAssistantErrorCodes.ApprovalNotFound,
            NhAssistantErrorCodes.QuestionNotFound,
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
            NhAssistantErrorCodes.TriggerCandidateNotFound,
            NhAssistantErrorCodes.ConsultationNotFound,
            NhAssistantErrorCodes.HandOffNotFound,
            NhAssistantAdminErrorCodes.MetadataEntryNotFound,
            NhAssistantErrorCodes.FilesUnavailable,
            NhAssistantErrorCodes.FileNotFound,
            NhAssistantAdminErrorCodes.NotFound,
            NhAssistantAdminErrorCodes.ContextNotFound,
            NhAssistantAdminErrorCodes.McpServerNotFound,
            NhAssistantAdminErrorCodes.McpToolNotFound,
            NhAssistantAdminErrorCodes.InstructionsAssetNotFound,
            NhAssistantErrorCodes.PackageNotLinked,
            NhAssistantErrorCodes.PackageNotFound
        ]),
        (StatusCodes.Status409Conflict,
        [
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
            NhAssistantErrorCodes.AgentWorkOff,
            NhAssistantErrorCodes.SkillOff,
            NhAssistantErrorCodes.AgentDoesNotWorkOn,
            NhAssistantErrorCodes.MovedToShadow,
            NhAssistantErrorCodes.ShadowVariant,
            NhAssistantErrorCodes.NotShadowVariant,
            NhAssistantErrorCodes.ServiceAccountNotUsed,
            NhAssistantErrorCodes.ConsultRouteNotAllowed,
            NhAssistantErrorCodes.ConsultProposalRefused,
            NhAssistantErrorCodes.ConsultAskerDecides,
            NhAssistantErrorCodes.ConsultProposalMoved,
            NhAssistantErrorCodes.SkillInCode,
            NhAssistantErrorCodes.TriggerInCode,
            NhAssistantErrorCodes.SkillInUse,
            NhAssistantErrorCodes.ConfigurationLimit,
            NhAssistantErrorCodes.RunSettingsInCode,
            NhAssistantErrorCodes.RunSettingsInUse,
            NhAssistantErrorCodes.ConsultationInCode,
            NhAssistantErrorCodes.ConsultationsNotAllowed,
            NhAssistantErrorCodes.HandOffsNotAllowed,
            NhAssistantErrorCodes.HandOffInCode,
            NhAssistantErrorCodes.ShadowVariantsNotAllowed,
            NhAssistantErrorCodes.AgentNotConfigured,
            NhAssistantErrorCodes.ExecutionNotUncertain,
            NhAssistantErrorCodes.ExecutionReportNotAwaited,
            NhAssistantErrorCodes.ProposalStale,
            NhAssistantErrorCodes.MemoryConflict,
            NhAssistantErrorCodes.ReplayActive,
            NhAssistantErrorCodes.FeedbackNotAccepted,
            NhAssistantErrorCodes.QuestionNotPending,
            NhAssistantErrorCodes.QuestionExpired,
            NhAssistantErrorCodes.QuestionCancelled,
            NhAssistantErrorCodes.FilesNotAccepted,
            NhAssistantTriggerReasons.RateLimited,
            NhAssistantAdminErrorCodes.VersionConflict,
            NhAssistantAdminErrorCodes.AgentExists,
            NhAssistantAdminErrorCodes.CodeAgentNotDeletable,
            NhAssistantAdminErrorCodes.NotCodeAgent,
            NhAssistantAdminErrorCodes.MetadataReadOnly,
            NhAssistantAdminErrorCodes.McpServerExists,
            NhAssistantAdminErrorCodes.McpHeadlessNotAllowed,
            NhAssistantAdminErrorCodes.McpConsentRequired,
            NhAssistantAdminErrorCodes.McpConnectionRequired,
            NhAssistantAdminErrorCodes.McpReauthorizationRequired,
            NhAssistantAdminErrorCodes.McpIssuerChanged,
            NhAssistantAdminErrorCodes.McpAuthorizationNotConfirmed,
            NhAssistantAdminErrorCodes.McpConnectionNotSupported,
            NhAssistantAdminErrorCodes.McpSecretUnreadable,
            NhAssistantAdminErrorCodes.McpToolNameConflict,
            NhAssistantErrorCodes.AgentInstructionsUnavailable,
            NhAssistantErrorCodes.PackageRefused,
            NhAssistantErrorCodes.PackageLocked,
            NhAssistantErrorCodes.ApprovalPolicyUnknown,
            NhAssistantErrorCodes.ProposalIdempotencyConflict
        ]),
        (StatusCodes.Status413PayloadTooLarge,
        [
            NhAssistantErrorCodes.WebhookPayloadTooLarge,
            NhAssistantErrorCodes.FileTooLarge,
            NhAssistantErrorCodes.PackageTooLarge
        ]),
        (StatusCodes.Status415UnsupportedMediaType,
        [
            NhAssistantErrorCodes.FileTypeNotSupported,
            NhAssistantErrorCodes.FileTypeNotAccepted
        ]),
        (StatusCodes.Status422UnprocessableEntity,
        [
            NhAssistantErrorCodes.FileUploadRefused,
            NhAssistantErrorCodes.FileScanRefused
        ]),
        (StatusCodes.Status429TooManyRequests,
        [
            NhAssistantAdminErrorCodes.McpTooManyAuthorizations
        ]),
        (StatusCodes.Status502BadGateway,
        [
            NhAssistantAdminErrorCodes.McpUnreachable,
            NhAssistantAdminErrorCodes.McpUnauthorized,
            NhAssistantAdminErrorCodes.McpInsufficientScope,
            NhAssistantAdminErrorCodes.McpDelegatedTokenUnavailable,
            NhAssistantAdminErrorCodes.McpDiscoveryFailed,
            NhAssistantAdminErrorCodes.McpTokenRequestFailed,
            NhAssistantAdminErrorCodes.McpResponseTooLarge,
            NhAssistantAdminErrorCodes.McpToolListTooLarge,
            NhAssistantErrorCodes.FileReaderFailed
        ]),
        (StatusCodes.Status503ServiceUnavailable,
        [
            NhAssistantErrorCodes.ProposalCheckFailed,
            NhAssistantErrorCodes.FileUnavailable,
            NhAssistantErrorCodes.FileUploadFailed,
            NhAssistantRunStore.OrphanedRunCode,
            NhAssistantAdminErrorCodes.McpSecretUnavailable,
            NhAssistantAdminErrorCodes.InstructionsAssetUnavailable,
            NhAssistantErrorCodes.ApprovalPolicySelectorFailed,
            NhAssistantErrorCodes.ApprovalPolicySelectorTimedOut
        ])
    ];
}
