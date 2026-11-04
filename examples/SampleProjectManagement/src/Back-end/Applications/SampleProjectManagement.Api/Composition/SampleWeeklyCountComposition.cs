using System.Globalization;
using System.Resources;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-396: the agent builder builds an application-wide weekly count through the API gateway (FB93 to
/// FB95). The application declares only the alert kind <see cref="KindId"/> in code, with its name, urgency,
/// texts per language and the people the rule names (the whole application has no responsible person); no
/// agent in code lists it. An administrator asks the builder "every Monday, count the tasks that changed last
/// week and warn the operations lead when there are more than ten", and the builder, after an approval for
/// each step: creates the weekly count (<see cref="AgentId"/>) in the administration for the whole
/// application; lets it answer to a technical account with gateway reads and lists the kind in its run
/// settings, on a card that says this changes whose rights the agent uses; saves the rule script of
/// <see cref="ScriptFor"/>, which counts the changed tasks through the gateway's <c>query</c> with
/// <c>countOnly</c> and raises the kind above <see cref="Threshold"/>; dry-runs it on the whole application;
/// and asks to set it live, which another administrator does under the rule scripts' four eyes (SPM-393).
/// Agents created in the administration may have rule scripts (<c>UseRuleScriptsAsData</c>, SPM-395).
/// Call it after <c>AddSampleAiBridge</c>, whose gateway the count reads through.
/// </summary>
public static class SampleWeeklyCountComposition
{
    /// <summary>The weekly count the agent builder creates in the administration.</summary>
    public const string AgentId = "sample-weekly-task-changes";

    public const string KindId = "weekly-task-changes";

    /// <summary>The front-end translation key of the kind's name.</summary>
    public const string KindNameKey = "nh-assistant.alert-kinds.weekly-task-changes.name";

    /// <summary>More changed tasks than this in a week raise the kind.</summary>
    public const int Threshold = 10;

    /// <summary>The gateway's query tool, as a rule script calls it.</summary>
    public const string GatewayQueryFunction = SampleAiBridgeComposition.GatewayToolSetId + "_query_v1";

    private static readonly ResourceManager Texts = new(typeof(SampleAssistantToolPresenter));

    /// <summary>
    /// The rule script the agent builder saves for the weekly count: it counts the tasks whose last change
    /// falls in the week before the run through the gateway (<c>countOnly</c>, filtered on
    /// <c>lastModifiedDateTime</c>) and raises the kind for <paramref name="leadActorId"/> when there are
    /// more than <see cref="Threshold"/>. It is data, not code: the builder or an administrator stores it.
    /// </summary>
    public static string ScriptFor(string leadActorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leadActorId);
        return $$"""
            const since = new Date(Date.parse(run.now) - 7 * 24 * 60 * 60 * 1000).toISOString();
            const changed = await tools["{{GatewayQueryFunction}}"]({
              resource: "project-task",
              countOnly: true,
              filter: [{ key: "lastModifiedDateTime", operator: ">=", value: since }]
            });
            if (!changed.success) {
              handOver("The tasks that changed last week could not be counted (" + changed.code + ").");
              return;
            }
            const count = changed.data.body.totalCount;
            if (count > {{Threshold}}) {
              alerts.raise({ kind: "{{KindId}}", key: "week", parameters: { count: count }, recipients: ["{{leadActorId}}"] });
              done(count + " tasks changed last week, more than {{Threshold}}; the operations lead is warned.");
              return;
            }
            done(count + " tasks changed last week; nothing to warn about.");
            """;
    }

    public static IServiceCollection AddSampleWeeklyCount(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAlertKind(KindId, kind => kind
                .DisplayName(KindNameKey)
                .Urgency(NhAssistantAlertUrgency.Warning)
                // The platform writes the texts in each reader's own language from the parameters.
                .Title((culture, parameters) => Format("AlertWeeklyTaskChangesTitle", culture, parameters["count"]))
                .Text((culture, _) => Format("AlertWeeklyTaskChangesText", culture, Threshold))
                // The whole application has no responsible person: the rule names whom it warns.
                .Recipients(NhAssistantAlertRecipients.Explicit))
            .UseRuleScriptsAsData(scripts => scripts.ForAgentsCreatedInAdministration()));
        return services;
    }

    private static string Format(string name, CultureInfo culture, params object[] values)
    {
        var text = Texts.GetString(name, culture) ?? throw new InvalidOperationException($"The sample text {name} is missing.");
        return string.Format(culture, text, values);
    }
}
