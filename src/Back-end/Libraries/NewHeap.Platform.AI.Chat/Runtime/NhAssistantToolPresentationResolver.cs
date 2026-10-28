using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Runtime;

internal sealed class NhAssistantToolPresentationResolver(
    IEnumerable<INhAssistantToolPresenter> presenters,
    ILogger<NhAssistantToolPresentationResolver> logger,
    INhAssistantPresentationCultures? presentationCultures = null)
{
    public const int MaxDisplayNameLength = NhAssistantPresentationLimits.MaxDisplayNameLength;
    public const int MaxSummaryLength = NhAssistantPresentationLimits.MaxSummaryLength;
    public const int MaxFieldCount = NhAssistantPresentationLimits.MaxFields;
    public const int MaxFieldLabelLength = NhAssistantPresentationLimits.MaxFieldLabelLength;
    public const int MaxFieldValueLength = NhAssistantPresentationLimits.MaxFieldValueLength;
    public const int MaxNoticeLength = NhAssistantPresentationLimits.MaxNoticeLength;
    public const int MaxSerializedLength = NhAssistantPresentationLimits.MaxSerializedLength;

    /// <summary>
    /// The most cultures one approval is presented in.
    /// </summary>
    public const int MaxLocalizedCultures = NhAssistantPresentationLimits.MaxCultures;

    public const int MaxLocalizedSerializedLength = MaxLocalizedCultures * (MaxSerializedLength + 64);

    private readonly IReadOnlyList<INhAssistantToolPresenter> _presenters = presenters.ToArray();

    public async Task<string> ResolveDisplayNameAsync(
        NhAiToolDescriptor descriptor,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        foreach (var presenter in _presenters)
        {
            var result = await TryInvokeAsync(
                presenter,
                token => presenter.GetDisplayNameAsync(
                    new NhAssistantToolDisplayNameContext(descriptor, culture),
                    token),
                descriptor.Id,
                cancellationToken);
            if (result is null)
            {
                continue;
            }
            if (result is { Length: > 0 and <= MaxDisplayNameLength })
            {
                return result;
            }

            LogDropped(
                presenter,
                descriptor.Id,
                culture,
                string.IsNullOrWhiteSpace(result) ? NhAssistantPresentationRules.DisplayNameMissing : NhAssistantPresentationRules.DisplayNameTooLong,
                "display name");
        }

        return descriptor.Id;
    }

    public async Task<NhAssistantApprovalPresentation?> ResolveApprovalAsync(
        NhAiToolDescriptor descriptor,
        object arguments,
        NhAiInvocationContext invocationContext,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        if (descriptor.DataClassification >= NhAiDataClassification.Confidential)
        {
            return null;
        }

        foreach (var presenter in _presenters)
        {
            var result = await TryInvokeAsync(
                presenter,
                token => presenter.PresentApprovalAsync(
                    new NhAssistantApprovalPresentationContext(
                        descriptor,
                        arguments,
                        invocationContext,
                        culture),
                    token),
                descriptor.Id,
                cancellationToken);
            if (result is null)
            {
                continue;
            }
            if (NhAssistantPresentationLimits.FindBrokenRule(result) is not { } rule)
            {
                return result;
            }

            // The presenter is the application's to fix; the approver reads the generic summary.
            LogDropped(presenter, descriptor.Id, culture, rule, "approval presentation");
        }

        return null;
    }

    /// <summary>
    /// Presents an approval in <paramref name="culture"/> and, when the application supports more UI
    /// cultures, in each of them, so every viewer later reads it in their own language. The
    /// presenter runs with the current culture set to the culture it presents in.
    /// </summary>
    public async Task<NhAssistantLocalizedApprovalPresentation> ResolveApprovalInCulturesAsync(
        NhAiToolDescriptor descriptor,
        object arguments,
        NhAiInvocationContext invocationContext,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        var presentation = await ResolveApprovalAsync(descriptor, arguments, invocationContext, culture, cancellationToken);
        var others = (presentationCultures?.GetCultures() ?? [])
            .Where(other => !string.Equals(other.Name, culture.Name, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(other => other.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLocalizedCultures - 1)
            .ToArray();
        if (presentation is null || others.Length == 0)
        {
            return new NhAssistantLocalizedApprovalPresentation(presentation, null);
        }

        var localized = new Dictionary<string, NhAssistantApprovalPresentation>(StringComparer.OrdinalIgnoreCase)
        {
            [culture.Name] = presentation
        };
        foreach (var other in others)
        {
            if (await ResolveInCultureAsync(descriptor, arguments, invocationContext, other, cancellationToken) is { } translated)
            {
                localized[other.Name] = translated;
            }
        }

        return new NhAssistantLocalizedApprovalPresentation(
            presentation,
            localized.Count > 1 ? JsonSerializer.Serialize(localized, NhAssistantContent.JsonOptions) : null);
    }

    /// <summary>
    /// The presentation for a viewer in <paramref name="culture"/>: the exact culture, then its
    /// parents, then any culture of the same language, and otherwise the culture the approval was
    /// made in.
    /// </summary>
    public static NhAssistantApprovalPresentation? Pick(
        string? presentationJson,
        string? presentationsJson,
        CultureInfo culture)
    {
        if (DeserializeLocalized(presentationsJson) is { Count: > 0 } localized)
        {
            for (var candidate = culture; !string.IsNullOrEmpty(candidate.Name); candidate = candidate.Parent)
            {
                if (localized.TryGetValue(candidate.Name, out var exact) && IsValid(exact))
                {
                    return exact;
                }
            }

            var sameLanguage = localized
                .Where(entry => SameLanguage(entry.Key, culture) && IsValid(entry.Value))
                .Select(entry => entry.Value)
                .FirstOrDefault();
            if (sameLanguage is not null)
            {
                return sameLanguage;
            }
        }

        return Deserialize(presentationJson);
    }

    public static string? Serialize(NhAssistantApprovalPresentation? presentation)
    {
        if (!IsValid(presentation))
        {
            return null;
        }

        return JsonSerializer.Serialize(presentation, NhAssistantContent.JsonOptions);
    }

    public static NhAssistantApprovalPresentation? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var presentation = JsonSerializer.Deserialize<NhAssistantApprovalPresentation>(
                json,
                NhAssistantContent.JsonOptions);
            return IsValid(presentation) ? presentation : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, NhAssistantApprovalPresentation>? DeserializeLocalized(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var localized = JsonSerializer.Deserialize<Dictionary<string, NhAssistantApprovalPresentation>>(
                json,
                NhAssistantContent.JsonOptions);
            return localized is null
                ? null
                : new Dictionary<string, NhAssistantApprovalPresentation>(localized, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SameLanguage(string cultureName, CultureInfo culture)
    {
        try
        {
            return string.Equals(
                CultureInfo.GetCultureInfo(cultureName).TwoLetterISOLanguageName,
                culture.TwoLetterISOLanguageName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private async Task<NhAssistantApprovalPresentation?> ResolveInCultureAsync(
        NhAiToolDescriptor descriptor,
        object arguments,
        NhAiInvocationContext invocationContext,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        // Presenters often read resources through the current culture; the change stays inside
        // this asynchronous call.
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return await ResolveApprovalAsync(descriptor, arguments, invocationContext, culture, cancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>
    /// Whether a stored presentation still keeps within the limits, by the public check
    /// (<see cref="NhAssistantPresentationLimits.FindBrokenRule"/>) that presenters are tested with.
    /// </summary>
    private static bool IsValid(NhAssistantApprovalPresentation? presentation)
    {
        return presentation is not null && NhAssistantPresentationLimits.FindBrokenRule(presentation) is null;
    }

    /// <summary>
    /// Logs that a presenter's result was dropped: its type, the tool, the culture and the broken rule,
    /// never the text it returned.
    /// </summary>
    private void LogDropped(INhAssistantToolPresenter presenter, string toolId, CultureInfo culture, string rule, string what)
    {
        logger.LogWarning(
            "Assistant tool presenter {PresenterType} returned a {PresentationKind} for tool {ToolId} in culture {Culture} that breaks rule {PresentationRule}; it is dropped and the fallback is shown.",
            presenter.GetType().FullName,
            what,
            toolId,
            culture.Name,
            rule);
    }

    private async Task<T?> TryInvokeAsync<T>(
        INhAssistantToolPresenter presenter,
        Func<CancellationToken, ValueTask<T?>> invoke,
        string toolId,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(NhAssistantPresentationLimits.PresenterTimeout);
            return await invoke(timeout.Token).AsTask().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Assistant tool presenter {PresenterType} timed out for tool {ToolId}.",
                presenter.GetType().FullName,
                toolId);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Assistant tool presenter {PresenterType} failed for tool {ToolId} with {ExceptionType}.",
                presenter.GetType().FullName,
                toolId,
                exception.GetType().FullName ?? exception.GetType().Name);
            return null;
        }
    }
}

/// <summary>
/// An approval's presentation in the culture it was made in and, when the application supports more
/// UI cultures, all its translations as stored JSON.
/// </summary>
internal sealed record NhAssistantLocalizedApprovalPresentation(
    NhAssistantApprovalPresentation? Presentation,
    string? LocalizedJson);

/// <summary>
/// The UI cultures an approval is presented in besides the culture it was made in. The ASP.NET
/// package uses the application's supported request UI cultures; without it, approvals are
/// presented in one culture.
/// </summary>
internal interface INhAssistantPresentationCultures
{
    IReadOnlyList<CultureInfo> GetCultures();
}
