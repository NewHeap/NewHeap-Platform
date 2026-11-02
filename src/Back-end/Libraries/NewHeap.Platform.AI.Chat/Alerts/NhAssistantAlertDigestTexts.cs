using System.Globalization;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// The assistant's own texts of a notification of alerts, in English and Dutch: the title of a digest,
/// which counts its alerts per urgency ("3 alerts, 1 urgent", "3 attenties, 1 urgent"), and the line
/// that counts the alerts a digest leaves out ("and 2 more", "en nog 2"). Any other language reads
/// English. The alerts' own titles and texts are the application's, from the kind in code.
/// </summary>
internal static class NhAssistantAlertDigestTexts
{
    /// <summary>
    /// The title of a digest of <paramref name="urgencies"/>: the number of alerts, then the urgent ones
    /// and the warnings among them when there are any.
    /// </summary>
    public static string Title(CultureInfo culture, IReadOnlyCollection<NhAssistantAlertUrgency> urgencies)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(urgencies);
        var dutch = IsDutch(culture);
        var total = urgencies.Count;
        var urgent = urgencies.Count(urgency => urgency == NhAssistantAlertUrgency.Urgent);
        var warnings = urgencies.Count(urgency => urgency == NhAssistantAlertUrgency.Warning);

        var parts = new List<string>
        {
            dutch
                ? Count(total, "attentie", "attenties")
                : Count(total, "alert", "alerts")
        };
        if (urgent > 0)
        {
            // English and Dutch both say "urgent".
            parts.Add(urgent.ToString(CultureInfo.InvariantCulture) + " urgent");
        }
        if (warnings > 0)
        {
            parts.Add(dutch
                ? Count(warnings, "waarschuwing", "waarschuwingen")
                : Count(warnings, "warning", "warnings"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The line after the listed alerts of a digest that leaves <paramref name="count"/> out.
    /// </summary>
    public static string More(CultureInfo culture, int count)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return IsDutch(culture) ? $"en nog {count}" : $"and {count} more";
    }

    /// <summary>
    /// The lines of a digest: one title per listed alert, then the line that counts the rest.
    /// </summary>
    public static string Lines(CultureInfo culture, IEnumerable<string> titles, int moreCount)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var lines = titles.ToList();
        if (moreCount > 0)
        {
            lines.Add(More(culture, moreCount));
        }

        return string.Join('\n', lines);
    }

    private static string Count(int count, string one, string many)
    {
        return count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
    }

    private static bool IsDutch(CultureInfo culture)
    {
        return culture.TwoLetterISOLanguageName.Equals("nl", StringComparison.OrdinalIgnoreCase);
    }
}
