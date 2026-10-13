using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Runtime;

internal sealed class NhAssistantToolPresentationResolver(
    IEnumerable<INhAssistantToolPresenter> presenters,
    ILogger<NhAssistantToolPresentationResolver> logger,
    INhAssistantPresentationCultures? presentationCultures = null)
{
    public const int MaxDisplayNameLength = 160;
    public const int MaxSummaryLength = 512;
    public const int MaxFieldCount = 8;
    public const int MaxFieldLabelLength = 80;
    public const int MaxFieldValueLength = 256;
    public const int MaxNoticeLength = 512;
    public const int MaxSerializedLength = 4_000;

    /// <summary>
    /// The most cultures one approval is presented in.
    /// </summary>
    public const int MaxLocalizedCultures = 8;

    public const int MaxLocalizedSerializedLength = MaxLocalizedCultures * (MaxSerializedLength + 64);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
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
            if (result is { Length: > 0 and <= MaxDisplayNameLength })
            {
                return result;
            }
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
            if (IsValid(result))
            {
                return result;
            }
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

    private static bool IsValid(NhAssistantApprovalPresentation? presentation)
    {
        if (presentation is null
            || string.IsNullOrWhiteSpace(presentation.ToolDisplayName)
            || presentation.ToolDisplayName.Length > MaxDisplayNameLength
            || string.IsNullOrWhiteSpace(presentation.Summary)
            || presentation.Summary.Length > MaxSummaryLength
            || presentation.Fields is null
            || presentation.Fields.Count > MaxFieldCount
            || presentation.Fields.Any(field =>
                string.IsNullOrWhiteSpace(field.Label)
                || field.Label.Length > MaxFieldLabelLength
                || string.IsNullOrWhiteSpace(field.Value)
                || field.Value.Length > MaxFieldValueLength)
            || presentation.Notice?.Length > MaxNoticeLength)
        {
            return false;
        }

        return JsonSerializer.Serialize(presentation, NhAssistantContent.JsonOptions).Length
            <= MaxSerializedLength;
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
            timeout.CancelAfter(Timeout);
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
