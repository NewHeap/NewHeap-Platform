using System.Globalization;
using System.Resources;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-395: alert kinds of agents created in the administration (FB93). The application declares the kind
/// <see cref="KindId"/> in code, with its name, urgency, texts per language and recipients, and lets agents
/// created in the administration have read-only rule scripts. No agent in code lists the kind: an
/// administrator creates the project reminder (<see cref="AgentId"/>) in the administration, lists the kind
/// in its run settings (<c>alertKinds</c>) and writes its rules as the rule script
/// <see cref="ReminderScript"/>, which raises the kind for the project's owner when someone starts the
/// reminder on a project. The kind in code stays the ceiling: its texts, recipients and delivery, and the
/// administrator's settings of the kind apply as for an agent in code. A kind the run settings do not list
/// is refused, also one another agent lists, such as the document check's <c>project-missing-document</c>.
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the project subject type.
/// </summary>
public static class SampleAlertKindsAsDataComposition
{
    /// <summary>The project reminder, an agent an administrator creates in the administration.</summary>
    public const string AgentId = "sample-project-reminder";

    public const string KindId = "project-review-reminder";

    /// <summary>The front-end translation key of the kind's name.</summary>
    public const string KindNameKey = "nh-assistant.alert-kinds.project-review-reminder.name";

    /// <summary>
    /// The script an administrator saves as the reminder's rules (SPM-395): it raises the kind its run
    /// settings list for the project the run is about and ends with a summary. It is data, not code.
    /// </summary>
    public const string ReminderScript = """
        alerts.raise({ kind: "project-review-reminder", key: "review", parameters: { project: subject.displayName ?? subject.id } });
        done("Reminded the project's owner to review the project.");
        """;

    /// <summary>
    /// A version that raises the document check's kind, which the reminder's run settings do not list: the
    /// script stops at <c>alerts.raise</c> and the run raises nothing.
    /// </summary>
    public const string UnlistedKindScript = """
        alerts.raise({ kind: "project-missing-document", key: "documents", parameters: { project: subject.id } });
        done("Never reached.");
        """;

    private static readonly ResourceManager Texts = new(typeof(SampleAssistantToolPresenter));

    public static IServiceCollection AddSampleAlertKindsAsData(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAlertKind(KindId, kind => kind
                .DisplayName(KindNameKey)
                .Urgency(NhAssistantAlertUrgency.Info)
                // The platform writes the texts in each reader's own language from the parameters.
                .Title((culture, parameters) => Format("AlertProjectReviewReminderTitle", culture, parameters["project"]))
                .Text((culture, _) => Format("AlertProjectReviewReminderText", culture))
                .Recipients(NhAssistantAlertRecipients.SubjectResponsible)
                .RepeatAfter(TimeSpan.FromDays(7)))
            // Agents created in the administration may have rule scripts that read, raise and resolve
            // alerts of the kinds their run settings list, and propose; they write nothing themselves.
            .UseRuleScriptsAsData(scripts => scripts.ForAgentsCreatedInAdministration()));
        return services;
    }

    private static string Format(string name, CultureInfo culture, params object[] values)
    {
        var text = Texts.GetString(name, culture) ?? throw new InvalidOperationException($"The sample text {name} is missing.");
        return string.Format(culture, text, values);
    }
}
