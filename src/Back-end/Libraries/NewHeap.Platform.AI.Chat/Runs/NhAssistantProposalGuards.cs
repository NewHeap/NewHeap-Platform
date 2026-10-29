using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// A guard registered with <c>AddProposalGuard</c>: its type, the tools it knows and the stages it
/// runs at.
/// </summary>
internal sealed record NhAssistantProposalGuardRegistration(
    Type GuardType,
    IReadOnlyList<string> ToolSelectors,
    NhAiProposalGuardStages Stages)
{
    public bool Matches(string toolId)
    {
        return ToolSelectors.Any(selector =>
            string.Equals(selector, toolId, StringComparison.Ordinal)
            || (selector.EndsWith(".*", StringComparison.Ordinal)
                && toolId.StartsWith(selector[..^1], StringComparison.Ordinal)));
    }
}

/// <summary>
/// The outcome of the guards of one proposal at one stage.
/// </summary>
/// <param name="Stale">The first stale result, or <see langword="null"/>.</param>
/// <param name="Failed">True when a guard threw; the proposal then stays as it is.</param>
internal sealed record NhAssistantGuardCheck(NhAiPreconditionResult? Stale, bool Failed)
{
    public static NhAssistantGuardCheck Fits { get; } = new(null, false);

    public static NhAssistantGuardCheck CheckFailed { get; } = new(null, true);
}

/// <summary>
/// Runs the application's proposal guards: the keys of a new proposal and the checks when it is
/// proposed, at revalidation, decision and execution. Guards run in their own service scope without
/// a person; a guard that throws is logged and never closes a proposal, nor keeps one from being made.
/// </summary>
internal sealed class NhAssistantProposalGuards(
    IServiceScopeFactory scopes,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantProposalGuards> logger)
{
    /// <summary>
    /// The longest reason code a guard may report; longer codes are stored as the generic stale code.
    /// </summary>
    public const int MaxReasonCodeLength = 128;

    public bool HasGuards(string toolId, NhAiProposalGuardStages stages)
    {
        return Registrations(toolId, stages).Any();
    }

    /// <summary>
    /// The application objects a new proposal changes, from every guard of its tool, at most
    /// <see cref="NhAssistantLimits.MaxProposalKeys"/>.
    /// </summary>
    public async Task<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken)
    {
        var registrations = Registrations(context.ToolId, NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal).ToArray();
        if (registrations.Length == 0)
        {
            return [];
        }

        var keys = new List<NhAiProposalKey>();
        await using var scope = scopes.CreateAsyncScope();
        foreach (var guard in registrations)
        {
            try
            {
                var instance = (INhAiProposalGuard)scope.ServiceProvider.GetRequiredService(guard.GuardType);
                keys.AddRange(await instance.GetKeysAsync(context, cancellationToken) ?? []);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Proposal guard {GuardType} failed to name the objects of proposal {ProposalId} with {ExceptionType}.",
                    guard.GuardType.FullName,
                    context.ProposalId,
                    exception.GetType().Name);
            }
        }

        return keys
            .Where(key => key is not null)
            .Distinct()
            .Take(NhAssistantLimits.MaxProposalKeys)
            .ToArray();
    }

    /// <summary>
    /// Runs the guards of the proposal's tool registered for any of <paramref name="stages"/>, in
    /// registration order. The first stale result wins; a guard that throws makes the check fail.
    /// </summary>
    public async Task<NhAssistantGuardCheck> CheckAsync(
        NhAiProposalGuardContext context,
        NhAiProposalGuardStages stages,
        CancellationToken cancellationToken)
    {
        var registrations = Registrations(context.ToolId, stages).ToArray();
        if (registrations.Length == 0)
        {
            return NhAssistantGuardCheck.Fits;
        }

        var failed = false;
        await using var scope = scopes.CreateAsyncScope();
        foreach (var guard in registrations)
        {
            try
            {
                var instance = (INhAiProposalGuard)scope.ServiceProvider.GetRequiredService(guard.GuardType);
                var result = await instance.CheckAsync(context, cancellationToken);
                if (result is { Outcome: NhAiPreconditionOutcome.Stale })
                {
                    return new NhAssistantGuardCheck(result, false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                failed = true;
                logger.LogWarning(
                    "Proposal guard {GuardType} failed to check proposal {ProposalId} with {ExceptionType}; the proposal stays as it is.",
                    guard.GuardType.FullName,
                    context.ProposalId,
                    exception.GetType().Name);
            }
        }

        return failed ? NhAssistantGuardCheck.CheckFailed : NhAssistantGuardCheck.Fits;
    }

    /// <summary>
    /// The guard context of a stored proposal, for revalidation, decision and execution: no call proposes
    /// at those stages, so it names no invocation, conversation, turn or channel (FB26). It names the
    /// approval policy and mode the proposal recorded (FB51), also a policy a selector chose.
    /// </summary>
    public static NhAiProposalGuardContext ContextOf(AssistantApproval approval, AssistantThread? thread)
    {
        var proposal = Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        return new NhAiProposalGuardContext(
            approval.ProposalId,
            approval.ToolId,
            proposal.Arguments,
            thread is null ? null : new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
            thread?.ScopeKey,
            approval.CreatedAt)
        {
            PolicyId = approval.PolicyId,
            Mode = NhAssistantApprovalModes.ToMode(approval.Mode)
        };
    }

    /// <summary>
    /// The reason code stored for a stale result: the guard's code when it fits the column, otherwise
    /// the generic stale code.
    /// </summary>
    public static string ReasonCodeOf(NhAiPreconditionResult result)
    {
        return result.ReasonCode is { Length: > 0 and <= MaxReasonCodeLength } code
            ? code
            : NhAssistantErrorCodes.ProposalStale;
    }

    private IEnumerable<NhAssistantProposalGuardRegistration> Registrations(string toolId, NhAiProposalGuardStages stages)
    {
        return registration.ProposalGuards
            .Where(guard => (guard.Stages & stages) != 0 && guard.Matches(toolId));
    }
}

/// <summary>
/// The explanations of stale proposals in every supported UI culture, stored as a JSON object from
/// culture name to text, the culture the platform runs in first.
/// </summary>
internal sealed class NhAssistantStaleExplanations(
    ILogger<NhAssistantStaleExplanations> logger,
    INhAssistantPresentationCultures? presentationCultures = null)
{
    /// <summary>
    /// Renders <paramref name="explain"/> in the current UI culture and every supported UI culture,
    /// with each culture set as the current culture while it renders.
    /// </summary>
    public string? Render(Func<CultureInfo, string?>? explain)
    {
        var texts = RenderTexts(explain);
        return texts.Count == 0 ? null : JsonSerializer.Serialize(texts, NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// Renders <paramref name="explain"/> as <see cref="Render"/> does and returns the texts by culture
    /// name, the current UI culture first: the same texts the stored JSON holds, without the cultures
    /// that would make it longer than <see cref="NhAssistantLimits.MaxStaleExplanationsSerializedLength"/>.
    /// Empty without an explanation.
    /// </summary>
    public IReadOnlyDictionary<string, string> RenderTexts(Func<CultureInfo, string?>? explain)
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (explain is null)
        {
            return texts;
        }

        var cultures = new[] { CultureInfo.CurrentUICulture }
            .Concat(presentationCultures?.GetCultures() ?? [])
            .Where(culture => !string.IsNullOrEmpty(culture.Name))
            .DistinctBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .Take(NhAssistantToolPresentationResolver.MaxLocalizedCultures);
        foreach (var culture in cultures)
        {
            if (RenderIn(explain, culture) is { } text)
            {
                texts[culture.Name] = text;
            }
        }

        while (texts.Count > 0
               && JsonSerializer.Serialize(texts, NhAssistantContent.JsonOptions).Length > NhAssistantLimits.MaxStaleExplanationsSerializedLength)
        {
            texts.Remove(texts.Keys.Last());
        }

        return texts;
    }

    /// <summary>
    /// The explanation for a reader in <paramref name="culture"/>: the exact culture, then its
    /// parents, then any culture of the same language, and otherwise the first stored text.
    /// </summary>
    public static string? Pick(string? explanationsJson, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(explanationsJson))
        {
            return null;
        }

        Dictionary<string, string>? texts;
        try
        {
            texts = JsonSerializer.Deserialize<Dictionary<string, string>>(explanationsJson, NhAssistantContent.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return texts is null ? null : PickFrom(texts, culture);
    }

    /// <summary>
    /// The explanation for a reader in <paramref name="culture"/> from texts by culture name, as
    /// <see cref="Pick"/> chooses it.
    /// </summary>
    public static string? PickFrom(IReadOnlyDictionary<string, string> texts, CultureInfo culture)
    {
        if (texts.Count == 0)
        {
            return null;
        }

        var lookup = new Dictionary<string, string>(texts, StringComparer.OrdinalIgnoreCase);
        for (var candidate = culture; !string.IsNullOrEmpty(candidate.Name); candidate = candidate.Parent)
        {
            if (lookup.TryGetValue(candidate.Name, out var exact))
            {
                return exact;
            }
        }

        foreach (var (name, text) in texts)
        {
            if (SameLanguage(name, culture))
            {
                return text;
            }
        }

        return texts.Values.First();
    }

    private string? RenderIn(Func<CultureInfo, string?> explain, CultureInfo culture)
    {
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            var text = explain(culture)?.Trim();
            return string.IsNullOrEmpty(text)
                ? null
                : text.Length <= NhAssistantLimits.MaxStaleExplanationCharacters
                    ? text
                    : text[..NhAssistantLimits.MaxStaleExplanationCharacters];
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(
                "The explanation of a stale proposal failed in culture {Culture} with {ExceptionType}.",
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
}
