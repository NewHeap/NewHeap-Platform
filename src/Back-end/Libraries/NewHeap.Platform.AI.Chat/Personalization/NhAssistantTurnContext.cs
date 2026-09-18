using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Supplies short facts about the current user and environment for one assistant turn, such as the
/// user's name, roles or active division. Facts come from the authenticated context and the
/// application's own data only, never from model input. Register providers with
/// <c>UseTurnContextProvider&lt;T&gt;()</c>; several providers are allowed.
/// </summary>
public interface INhAssistantTurnContextProvider
{
    ValueTask<IReadOnlyList<NhAssistantContextFact>> GetFactsAsync(
        NhAiInvocationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// One fact for the "Situation" block of a turn. The label is limited to 60 and the value to 300
/// characters; longer text is cut off. The block is data for the model, not instructions.
/// </summary>
public sealed record NhAssistantContextFact(string Label, string Value);

/// <summary>
/// One entity on the user's screen: dash-case type, id (at most 64 characters) and optional label.
/// </summary>
internal sealed record NhAssistantClientEntity(string Type, string Id, string? Label);

/// <summary>
/// The validated, bounded page context the client sent with a user message. It is untrusted data:
/// the model may use entity ids as search hints only; it never grants access.
/// </summary>
internal sealed partial record NhAssistantClientContext(
    string Route,
    string? Title,
    IReadOnlyList<NhAssistantClientEntity> Entities)
{
    public const int MaxRouteLength = 200;
    public const int MaxTitleLength = 120;
    public const int MaxEntities = 5;
    public const int MaxEntityIdLength = 64;
    public const int MaxEntityTypeLength = 60;
    public const int MaxEntityLabelLength = 120;

    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Validates and bounds a client context. A value of the wrong shape (no object, no route string)
    /// yields <see langword="null"/>; entities of the wrong shape are dropped; long text is cut off.
    /// </summary>
    public static NhAssistantClientContext? From(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("route", out var routeElement)
            || routeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var route = NhAssistantContextText.Clean(routeElement.GetString(), MaxRouteLength);
        if (route is null)
        {
            return null;
        }
        string? title = null;
        if (element.TryGetProperty("title", out var titleElement))
        {
            if (titleElement.ValueKind == JsonValueKind.String)
            {
                title = NhAssistantContextText.Clean(titleElement.GetString(), MaxTitleLength);
            }
            else if (titleElement.ValueKind != JsonValueKind.Null)
            {
                return null;
            }
        }
        var entities = new List<NhAssistantClientEntity>();
        if (element.TryGetProperty("entities", out var entitiesElement))
        {
            if (entitiesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entitiesElement.EnumerateArray())
                {
                    if (entities.Count == MaxEntities)
                    {
                        break;
                    }
                    if (ToEntity(item) is { } entity)
                    {
                        entities.Add(entity);
                    }
                }
            }
            else if (entitiesElement.ValueKind != JsonValueKind.Null)
            {
                return null;
            }
        }
        return new NhAssistantClientContext(route, title, entities);
    }

    /// <summary>
    /// Reads a stored client context. Stored values were bounded when they were received.
    /// </summary>
    public static NhAssistantClientContext? FromStorage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return From(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToStorage()
    {
        return JsonSerializer.Serialize(
            new
            {
                route = Route,
                title = Title,
                entities = Entities.Select(entity => new { type = entity.Type, id = entity.Id, label = entity.Label })
            },
            StorageOptions);
    }

    private static NhAssistantClientEntity? ToEntity(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var type = typeElement.GetString();
        var id = idElement.GetString()?.Trim();
        if (type is null
            || type.Length > MaxEntityTypeLength
            || !DashCase().IsMatch(type)
            || string.IsNullOrEmpty(id)
            || id.Length > MaxEntityIdLength
            || id.Any(char.IsControl))
        {
            return null;
        }
        string? label = null;
        if (item.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String)
        {
            label = NhAssistantContextText.Clean(labelElement.GetString(), MaxEntityLabelLength);
        }
        return new NhAssistantClientEntity(type, NhAssistantContextText.Clean(id, MaxEntityIdLength)!, label);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex DashCase();
}

/// <summary>
/// Facts of the "Situation" block of one turn after limits were applied, and their counts.
/// </summary>
internal sealed record NhAssistantTurnFacts(IReadOnlyList<NhAssistantContextFact> Facts, int ProviderFactCount);

/// <summary>
/// Collects the situational facts of a turn: the library's own date, weekday and time in the
/// configured time zone, followed by the facts of the registered providers. A failing provider is
/// logged with its exception type only and the turn continues without its facts.
/// </summary>
internal sealed class NhAssistantTurnContextCollector(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantTurnContextCollector> logger)
{
    public const int MaxFacts = 20;
    public const int MaxTotalCharacters = 2_000;
    public const int MaxLabelLength = 60;
    public const int MaxValueLength = 300;

    public async Task<NhAssistantTurnFacts> CollectAsync(
        NhAiInvocationContext context,
        string language,
        CancellationToken cancellationToken)
    {
        var dutch = NhAssistantPromptComposer.IsDutch(language);
        var facts = new List<NhAssistantContextFact>(TimeFacts(dutch));
        var total = facts.Sum(fact => fact.Label.Length + fact.Value.Length);
        var providerFacts = 0;
        foreach (var provider in services.GetServices<INhAssistantTurnContextProvider>())
        {
            IReadOnlyList<NhAssistantContextFact> supplied;
            try
            {
                supplied = await provider.GetFactsAsync(context, cancellationToken) ?? [];
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Assistant turn context provider {ProviderType} failed with {ExceptionType}; the turn continues without its facts.",
                    provider.GetType().Name,
                    exception.GetType().Name);
                continue;
            }
            foreach (var fact in supplied)
            {
                if (facts.Count >= MaxFacts)
                {
                    break;
                }
                var label = NhAssistantContextText.Clean(fact?.Label, MaxLabelLength);
                var value = NhAssistantContextText.Clean(fact?.Value, MaxValueLength);
                if (label is null || value is null)
                {
                    continue;
                }
                var remaining = MaxTotalCharacters - total - label.Length;
                if (remaining <= 0)
                {
                    break;
                }
                if (value.Length > remaining)
                {
                    value = value[..remaining];
                }
                facts.Add(new NhAssistantContextFact(label, value));
                total += label.Length + value.Length;
                providerFacts++;
            }
        }
        return new NhAssistantTurnFacts(facts, providerFacts);
    }

    private IEnumerable<NhAssistantContextFact> TimeFacts(bool dutch)
    {
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var zone = registration.TimeZone;
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        var culture = CultureInfo.GetCultureInfo(dutch ? "nl-NL" : "en-US");
        yield return new NhAssistantContextFact(
            dutch ? "Datum" : "Date",
            local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        yield return new NhAssistantContextFact(
            dutch ? "Weekdag" : "Weekday",
            culture.DateTimeFormat.GetDayName(local.DayOfWeek));
        yield return new NhAssistantContextFact(
            dutch ? "Tijd" : "Time",
            local.ToString("HH:mm", CultureInfo.InvariantCulture) + " (" + zone.Id + ")");
    }
}

/// <summary>
/// Makes context text safe to place inside a data block: single line, no markup that could close
/// the block or start a heading, bounded length.
/// </summary>
internal static class NhAssistantContextText
{
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '<' => '‹',
                '>' => '›',
                '#' => '＃',
                _ when char.IsControl(character) => ' ',
                _ => character
            });
        }
        var cleaned = builder.ToString().Trim();
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd();
        }
        return cleaned.Length == 0 ? null : cleaned;
    }
}
