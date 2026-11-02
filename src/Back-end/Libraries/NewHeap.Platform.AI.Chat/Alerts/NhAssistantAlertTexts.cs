using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// The title and text of an alert in one culture.
/// </summary>
/// <param name="Text">The text; <see langword="null"/> for a kind with a title only or a text that failed.</param>
internal sealed record NhAssistantAlertText(string Title, string? Text);

/// <summary>
/// Writes the title and text of an alert from its stored parameters with the functions of its kind in
/// code, in the culture of the person who reads or receives it, with that culture current so the
/// application's localizers follow it. A function that throws is logged with the exception's type only,
/// because the application's texts carry content; a title that fails, is empty or is too long gives no
/// text at all, and a text that does gives the title only.
/// </summary>
internal sealed class NhAssistantAlertTexts(ILogger<NhAssistantAlertTexts> logger)
{
    /// <summary>
    /// The alert's title and text in <paramref name="culture"/>, or <see langword="null"/> when its title
    /// cannot be written.
    /// </summary>
    public NhAssistantAlertText? Render(
        NhAssistantAlertKind kind,
        IReadOnlyDictionary<string, string> parameters,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(culture);
        var title = Write(kind, kind.Title, parameters, culture, NhAssistantAlertLimits.MaxTitleLength);
        if (title is null)
        {
            return null;
        }

        var text = kind.Text is { } writeText
            ? Write(kind, writeText, parameters, culture, NhAssistantAlertLimits.MaxTextLength)
            : null;
        return new NhAssistantAlertText(title, text);
    }

    /// <summary>
    /// <see cref="Render(NhAssistantAlertKind, IReadOnlyDictionary{string, string}, CultureInfo)"/> with
    /// the parameters as the alert's store keeps them.
    /// </summary>
    public NhAssistantAlertText? Render(NhAssistantAlertKind kind, string parametersJson, CultureInfo culture)
    {
        return Render(kind, ParametersOf(parametersJson), culture);
    }

    /// <summary>
    /// The stored parameters of an alert.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParametersOf(string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(parametersJson);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson);
        return parameters is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(parameters, StringComparer.Ordinal);
    }

    /// <summary>
    /// The parameters as the alert's store keeps them: a JSON object of strings, ordered by name, so the
    /// same parameters always store the same text.
    /// </summary>
    public static string Serialize(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            ordered[name] = value;
        }

        return JsonSerializer.Serialize(ordered);
    }

    private string? Write(
        NhAssistantAlertKind kind,
        Func<CultureInfo, IReadOnlyDictionary<string, string>, string> write,
        IReadOnlyDictionary<string, string> parameters,
        CultureInfo culture,
        int maximum)
    {
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            var text = write(culture, parameters)?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > maximum)
            {
                logger.LogWarning(
                    "A text of alert kind {KindId} in culture {Culture} is empty or longer than {Maximum} characters.",
                    kind.Id,
                    culture.Name,
                    maximum);
                return null;
            }

            return text;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Application texts can carry content in exception messages; log the type only.
            logger.LogWarning(
                "A text of alert kind {KindId} could not be written in culture {Culture} ({ExceptionType}).",
                kind.Id,
                culture.Name,
                exception.GetType().Name);
            return null;
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
