using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Runtime;

internal sealed class NhAssistantToolPresentationResolver(
    IEnumerable<INhAssistantToolPresenter> presenters,
    ILogger<NhAssistantToolPresentationResolver> logger,
    INhAssistantPresentationCultures? presentationCultures = null,
    TimeSpan? requiredPresenterTimeout = null)
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
    /// Whether the proposal of <paramref name="descriptor"/> needs one presenter's own card
    /// (<see cref="INhAssistantRequiredToolPresenter"/>), so no card prepared elsewhere stands in for it.
    /// </summary>
    public bool RequiresOwnCard(NhAiToolDescriptor descriptor)
    {
        return _presenters.OfType<INhAssistantRequiredToolPresenter>().Any(item => item.Requires(descriptor));
    }

    /// <summary>
    /// How long a presenter whose own card a proposal needs may take (<see cref="INhAssistantRequiredToolPresenter"/>);
    /// after it the proposal is refused, not made with the generic summary.
    /// </summary>
    public static TimeSpan RequiredPresenterTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Presents an approval in <paramref name="culture"/> and, when the application supports more UI
    /// cultures, in each of them, so every viewer later reads it in their own language. The
    /// presenter runs with the current culture set to the culture it presents in. A tool whose proposal
    /// needs one presenter's own card gets that card in every culture or a refusal, never another
    /// presenter's card or the generic summary.
    /// </summary>
    public async Task<NhAssistantLocalizedApprovalPresentation> ResolveApprovalInCulturesAsync(
        NhAiToolDescriptor descriptor,
        object arguments,
        NhAiInvocationContext invocationContext,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        var others = (presentationCultures?.GetCultures() ?? [])
            .Where(other => !string.Equals(other.Name, culture.Name, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(other => other.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLocalizedCultures - 1)
            .ToArray();
        if (_presenters.OfType<INhAssistantRequiredToolPresenter>().FirstOrDefault(item => item.Requires(descriptor)) is { } required)
        {
            return await ResolveRequiredAsync(required, descriptor, arguments, invocationContext, [culture, .. others], cancellationToken);
        }

        var presentation = await ResolveApprovalAsync(descriptor, arguments, invocationContext, culture, cancellationToken);
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

    /// <summary>
    /// The card of a presenter a proposal needs, in each culture, or why there is none: its refusal, a
    /// card that breaks a limit, a failure or a timeout.
    /// </summary>
    private async Task<NhAssistantLocalizedApprovalPresentation> ResolveRequiredAsync(
        INhAssistantRequiredToolPresenter presenter,
        NhAiToolDescriptor descriptor,
        object arguments,
        NhAiInvocationContext invocationContext,
        IReadOnlyList<CultureInfo> cultures,
        CancellationToken cancellationToken)
    {
        if (descriptor.DataClassification >= NhAiDataClassification.Confidential)
        {
            return Refused(NhAssistantRequiredPresentation.NotShown);
        }

        var localized = new Dictionary<string, NhAssistantApprovalPresentation>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in cultures)
        {
            var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            NhAssistantRequiredPresentation result;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(requiredPresenterTimeout ?? RequiredPresenterTimeout);
                result = await presenter
                    .PresentRequiredAsync(new NhAssistantApprovalPresentationContext(descriptor, arguments, invocationContext, culture), timeout.Token)
                    .AsTask()
                    .WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Assistant tool presenter {PresenterType} timed out for tool {ToolId}; its proposal is refused.",
                    presenter.GetType().FullName,
                    descriptor.Id);
                return Refused(NhAssistantRequiredPresentation.TimedOut);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Assistant tool presenter {PresenterType} failed for tool {ToolId} with {ExceptionType}; its proposal is refused.",
                    presenter.GetType().FullName,
                    descriptor.Id,
                    exception.GetType().FullName ?? exception.GetType().Name);
                return Refused(NhAssistantRequiredPresentation.Failed);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }

            if (result.Card is not { } card)
            {
                return Refused(result.Refusal ?? NhAssistantRequiredPresentation.Failed);
            }
            if (NhAssistantPresentationLimits.FindBrokenRule(card) is { } rule)
            {
                LogDropped((INhAssistantToolPresenter)presenter, descriptor.Id, culture, rule, "required approval presentation");
                return Refused(NhAssistantRequiredPresentation.Failed);
            }
            localized.TryAdd(culture.Name, card);
        }

        return new NhAssistantLocalizedApprovalPresentation(
            localized[cultures[0].Name],
            localized.Count > 1 ? JsonSerializer.Serialize(localized, NhAssistantContent.JsonOptions) : null);

        static NhAssistantLocalizedApprovalPresentation Refused(string reason)
        {
            return new NhAssistantLocalizedApprovalPresentation(null, null) { Refusal = reason };
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
    string? LocalizedJson)
{
    /// <summary>
    /// Why the tool's proposal is not recorded: its required presenter built no card
    /// (<see cref="INhAssistantRequiredToolPresenter"/>), as a message the model can act on.
    /// </summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// A presenter whose own card a tool's proposal needs, such as the agent builder's for its writes: the
/// proposal is recorded only with the card it builds, valid in every culture it is presented in, never
/// with another presenter's card or the generic summary. When it builds none, fails or takes longer than
/// <see cref="NhAssistantToolPresentationResolver.RequiredPresenterTimeout"/>, nothing is proposed and the
/// model reads why. It is registered as an <see cref="INhAssistantToolPresenter"/> as well.
/// </summary>
internal interface INhAssistantRequiredToolPresenter
{
    bool Requires(NhAiToolDescriptor descriptor);

    ValueTask<NhAssistantRequiredPresentation> PresentRequiredAsync(
        NhAssistantApprovalPresentationContext context,
        CancellationToken cancellationToken);
}

/// <summary>The card a required presenter built, or why it built none.</summary>
internal sealed record NhAssistantRequiredPresentation(NhAssistantApprovalPresentation? Card, string? Refusal)
{
    public const string NotShown = "Nothing was proposed: the approval card of this change cannot be shown for data of its classification.";

    public const string TimedOut = "Nothing was proposed: the approval card of this change could not be built in time. Try again.";

    public const string Failed = "Nothing was proposed: the approval card of this change could not be built. Read the agent again and make the change in smaller steps.";
}

/// <summary>
/// The UI cultures an approval is presented in besides the culture it was made in. The ASP.NET
/// package uses the application's supported request UI cultures; without it, approvals are
/// presented in one culture.
/// </summary>
internal interface INhAssistantPresentationCultures
{
    IReadOnlyList<CultureInfo> GetCultures();
}
