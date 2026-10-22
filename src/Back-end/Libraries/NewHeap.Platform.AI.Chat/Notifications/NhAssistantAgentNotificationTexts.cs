namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// Texts of agent notifications in English and Dutch, for Web Push and the in-app channel.
/// </summary>
internal static class NhAssistantAgentNotificationTexts
{
    public static (string Title, string Body) ForAgent(
        NhAssistantAgentNotificationKind kind,
        string language,
        string? subjectDisplayName,
        int proposalCount,
        string? mcpServerDisplayName = null,
        string? code = null,
        string? proposalStatus = null)
    {
        var dutch = language.StartsWith("nl", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(subjectDisplayName) ? (dutch ? "Assistent" : "Assistant") : subjectDisplayName;
        var body = kind switch
        {
            NhAssistantAgentNotificationKind.RunEndedWithProposals => proposalCount > 1
                ? (dutch ? $"{proposalCount} voorstellen wachten op een besluit." : $"{proposalCount} proposals wait for a decision.")
                : (dutch ? "Een voorstel wacht op een besluit." : "A proposal waits for a decision."),
            NhAssistantAgentNotificationKind.ProposalExpiring => dutch ? "Een voorstel wacht nog op een besluit." : "A proposal still waits for a decision.",
            NhAssistantAgentNotificationKind.ProposalEscalated => dutch ? "Een voorstel is doorgezet omdat niemand tijdig besliste." : "A proposal was escalated because nobody decided in time.",
            NhAssistantAgentNotificationKind.ExecutionUnknown => dutch ? "Van een uitgevoerd voorstel is de uitkomst onbekend." : "The outcome of an executed proposal is unknown.",
            NhAssistantAgentNotificationKind.RunFailed => dutch ? "De agent kon zijn taak niet afronden." : "The agent could not complete its task.",
            NhAssistantAgentNotificationKind.RequestAnswered => dutch ? "De agent heeft je verzoek afgerond." : "The agent finished your request.",
            NhAssistantAgentNotificationKind.McpConnectionMissing => McpConnectionMissing(dutch, mcpServerDisplayName, code),
            NhAssistantAgentNotificationKind.ConsultationProposalDecided => ConsultationProposalDecided(dutch, proposalStatus),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (title, body);
    }

    private static string ConsultationProposalDecided(bool dutch, string? status)
    {
        return status switch
        {
            NhAssistantApprovalStatuses.Executed => dutch
                ? "Een voorstel uit je consultatie is uitgevoerd."
                : "A proposal from your consultation was executed.",
            NhAssistantApprovalStatuses.Rejected => dutch
                ? "Een voorstel uit je consultatie is afgewezen."
                : "A proposal from your consultation was rejected.",
            NhAssistantApprovalStatuses.Expired => dutch
                ? "Een voorstel uit je consultatie is verlopen zonder besluit."
                : "A proposal from your consultation expired without a decision.",
            NhAssistantApprovalStatuses.ExecutionFailed or NhAssistantApprovalStatuses.ExecutionUnknown => dutch
                ? "Een voorstel uit je consultatie kon niet worden uitgevoerd."
                : "A proposal from your consultation could not be executed.",
            _ => dutch
                ? "Een voorstel uit je consultatie is gesloten zonder uitvoering."
                : "A proposal from your consultation was closed without being executed."
        };
    }

    private static string McpConnectionMissing(bool dutch, string? serverDisplayName, string? code)
    {
        var server = string.IsNullOrWhiteSpace(serverDisplayName)
            ? (dutch ? "een gekoppelde dienst" : "a connected service")
            : serverDisplayName;
        if (code == NhAssistantAdminErrorCodes.McpConsentRequired)
        {
            return dutch
                ? $"De agent kon {server} niet op de achtergrond gebruiken. Sta achtergrondgebruik toe in je assistentvoorkeuren."
                : $"The agent could not use {server} in the background. Allow background use in your assistant preferences.";
        }
        return dutch
            ? $"De agent kon {server} niet op de achtergrond gebruiken. Koppel je account in je assistentvoorkeuren."
            : $"The agent could not use {server} in the background. Connect your account in your assistant preferences.";
    }
}
