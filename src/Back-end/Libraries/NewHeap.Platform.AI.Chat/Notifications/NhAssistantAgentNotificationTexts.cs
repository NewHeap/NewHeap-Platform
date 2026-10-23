using System.Globalization;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// Texts of agent notifications and of the notification of an application message in English and
/// Dutch, for Web Push and the in-app channel.
/// </summary>
internal static class NhAssistantAgentNotificationTexts
{
    /// <summary>
    /// Whether <paramref name="kind"/> is a notification about the proposals of a run, which names its
    /// objects.
    /// </summary>
    public static bool IsRunProposals(NhAssistantAgentNotificationKind kind)
    {
        return kind is NhAssistantAgentNotificationKind.RunProposalsWaiting
            or NhAssistantAgentNotificationKind.RunProposalsExpiring
            or NhAssistantAgentNotificationKind.RunProposalsEscalated;
    }

    /// <summary>
    /// The plural names of the registered subject types, in a language; without the subject service,
    /// none.
    /// </summary>
    public static Func<string, CultureInfo, string?> PluralNamesOf(NhAssistantSubjectAccessService? subjects)
    {
        return (subjectType, culture) => subjects?.FindType(subjectType)?.PluralNameIn(culture);
    }

    /// <summary>
    /// The notification about the proposals of a run (RN4). For one object it reads as for that
    /// object: its name as the title, or "Assistant" without one, and how many proposals wait. For
    /// several the title is "Assistant" and the text counts the proposals and the objects, named by
    /// the plural of their subject type in <paramref name="language"/>, or "objects" when the types
    /// differ or the type has no plural name. Never a name of one of several objects.
    /// </summary>
    public static (string Title, string Body) ForRunProposals(
        NhAssistantAgentNotificationKind kind,
        string language,
        IReadOnlyList<NhAssistantAgentNotificationObject> objects,
        int proposalCount,
        string? subjectDisplayName,
        Func<string, CultureInfo, string?>? pluralName)
    {
        var dutch = IsDutch(language);
        if (objects.Count <= 1)
        {
            var title = string.IsNullOrWhiteSpace(subjectDisplayName) ? (dutch ? "Assistent" : "Assistant") : subjectDisplayName;
            return (title, AtOneObject(kind, dutch, proposalCount));
        }

        var culture = CultureOf(language);
        var types = objects.Select(item => item.Subject.Type).Distinct(StringComparer.Ordinal).ToArray();
        var plural = types.Length == 1 ? pluralName?.Invoke(types[0], culture) : null;
        var noun = string.IsNullOrWhiteSpace(plural) ? (dutch ? "objecten" : "objects") : InSentence(plural, culture);
        var count = proposalCount.ToString(CultureInfo.InvariantCulture);
        var at = objects.Count.ToString(CultureInfo.InvariantCulture);
        var body = kind switch
        {
            NhAssistantAgentNotificationKind.RunProposalsWaiting => dutch
                ? $"{count} voorstellen bij {at} {noun} wachten op je besluit."
                : $"{count} proposals at {at} {noun} wait for your decision.",
            NhAssistantAgentNotificationKind.RunProposalsExpiring => dutch
                ? $"{count} voorstellen bij {at} {noun} wachten nog op je besluit."
                : $"{count} proposals at {at} {noun} still wait for your decision.",
            NhAssistantAgentNotificationKind.RunProposalsEscalated => dutch
                ? $"{count} voorstellen bij {at} {noun} zijn doorgezet omdat niemand tijdig besliste."
                : $"{count} proposals at {at} {noun} were escalated because nobody decided in time.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (dutch ? "Assistent" : "Assistant", body);
    }
    /// <summary>
    /// The notification of an application message: the conversation's title, or "Assistant" without
    /// one, and a fixed text; never the message itself.
    /// </summary>
    public static (string Title, string Body) ForApplicationMessage(string language, string? conversationTitle)
    {
        var dutch = language.StartsWith("nl", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(conversationTitle) ? (dutch ? "Assistent" : "Assistant") : conversationTitle;
        var body = dutch
            ? "De applicatie heeft een bericht in het gesprek gezet."
            : "The application posted a message in the conversation.";
        return (title, body);
    }

    public static (string Title, string Body) ForAgent(
        NhAssistantAgentNotificationKind kind,
        string language,
        string? subjectDisplayName,
        int proposalCount,
        string? mcpServerDisplayName = null,
        string? code = null,
        string? proposalStatus = null)
    {
        var dutch = IsDutch(language);
        var title = string.IsNullOrWhiteSpace(subjectDisplayName) ? (dutch ? "Assistent" : "Assistant") : subjectDisplayName;
        var body = kind switch
        {
            NhAssistantAgentNotificationKind.RunEndedWithProposals
                or NhAssistantAgentNotificationKind.RunProposalsWaiting => AtOneObject(NhAssistantAgentNotificationKind.RunProposalsWaiting, dutch, proposalCount),
            NhAssistantAgentNotificationKind.ProposalExpiring => AtOneObject(NhAssistantAgentNotificationKind.RunProposalsExpiring, dutch, 1),
            NhAssistantAgentNotificationKind.RunProposalsExpiring => AtOneObject(kind, dutch, proposalCount),
            NhAssistantAgentNotificationKind.ProposalEscalated => AtOneObject(NhAssistantAgentNotificationKind.RunProposalsEscalated, dutch, 1),
            NhAssistantAgentNotificationKind.RunProposalsEscalated => AtOneObject(kind, dutch, proposalCount),
            NhAssistantAgentNotificationKind.ExecutionUnknown => dutch ? "Van een uitgevoerd voorstel is de uitkomst onbekend." : "The outcome of an executed proposal is unknown.",
            NhAssistantAgentNotificationKind.RunFailed => dutch ? "De agent kon zijn taak niet afronden." : "The agent could not complete its task.",
            NhAssistantAgentNotificationKind.RequestAnswered => dutch ? "De agent heeft je verzoek afgerond." : "The agent finished your request.",
            NhAssistantAgentNotificationKind.McpConnectionMissing => McpConnectionMissing(dutch, mcpServerDisplayName, code),
            NhAssistantAgentNotificationKind.ConsultationProposalDecided => ConsultationProposalDecided(dutch, proposalStatus),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (title, body);
    }

    /// <summary>
    /// The text about the proposals that wait at one object, as before the proposals of a run were
    /// bundled: one proposal, or how many.
    /// </summary>
    private static string AtOneObject(NhAssistantAgentNotificationKind kind, bool dutch, int proposalCount)
    {
        var several = proposalCount > 1;
        var count = proposalCount.ToString(CultureInfo.InvariantCulture);
        return kind switch
        {
            NhAssistantAgentNotificationKind.RunProposalsWaiting => several
                ? (dutch ? $"{count} voorstellen wachten op een besluit." : $"{count} proposals wait for a decision.")
                : (dutch ? "Een voorstel wacht op een besluit." : "A proposal waits for a decision."),
            NhAssistantAgentNotificationKind.RunProposalsExpiring => several
                ? (dutch ? $"{count} voorstellen wachten nog op een besluit." : $"{count} proposals still wait for a decision.")
                : (dutch ? "Een voorstel wacht nog op een besluit." : "A proposal still waits for a decision."),
            NhAssistantAgentNotificationKind.RunProposalsEscalated => several
                ? (dutch ? $"{count} voorstellen zijn doorgezet omdat niemand tijdig besliste." : $"{count} proposals were escalated because nobody decided in time.")
                : (dutch ? "Een voorstel is doorgezet omdat niemand tijdig besliste." : "A proposal was escalated because nobody decided in time."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>
    /// A plural name as it reads within a sentence: a capital that only starts the name, as in
    /// "Projects", becomes lowercase; a name such as "CRM accounts" stays as it is.
    /// </summary>
    private static string InSentence(string name, CultureInfo culture)
    {
        if (name.Length > 1 && char.IsUpper(name[0]) && char.IsLower(name[1]))
        {
            return char.ToLower(name[0], culture) + name[1..];
        }

        return name;
    }

    private static CultureInfo CultureOf(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static bool IsDutch(string language)
    {
        return language.StartsWith("nl", StringComparison.OrdinalIgnoreCase);
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
