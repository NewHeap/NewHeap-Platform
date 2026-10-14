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
        int proposalCount)
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
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (title, body);
    }
}
