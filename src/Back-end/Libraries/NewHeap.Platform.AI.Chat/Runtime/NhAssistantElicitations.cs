using System.Collections.Concurrent;
using System.Text.Json;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// How an MCP server asks the turn's person for input while one of its tools runs.
/// </summary>
internal static class NhAssistantElicitationModes
{
    /// <summary>The server asks for a few values, which the person enters in the chat.</summary>
    public const string Form = "form";

    /// <summary>
    /// The server asks the person to finish something on a page of its own; the person opens the link
    /// themselves and says when they are done.
    /// </summary>
    public const string Url = "url";
}

/// <summary>
/// The statuses of a request for input as the conversation shows it.
/// </summary>
internal static class NhAssistantElicitationStatuses
{
    public const string Pending = "pending";

    /// <summary>The person answered, or said they finished the server's page.</summary>
    public const string Accepted = "accepted";

    public const string Declined = "declined";

    public const string Cancelled = "cancelled";

    /// <summary>
    /// Nobody answered within the server's wait time, or within what was left of the turn's time for
    /// its person.
    /// </summary>
    public const string Expired = "expired";
}

/// <summary>
/// What the person does with a request for input, as the MCP protocol names it.
/// </summary>
internal static class NhAssistantElicitationActions
{
    public const string Accept = "accept";

    public const string Decline = "decline";

    public const string Cancel = "cancel";

    public static bool IsKnown(string? action)
    {
        return action is Accept or Decline or Cancel;
    }
}

/// <summary>
/// The kinds of values a form of an MCP server may ask for.
/// </summary>
internal static class NhAssistantElicitationFieldTypes
{
    public const string String = "string";

    public const string Number = "number";

    public const string Integer = "integer";

    public const string Boolean = "boolean";

    /// <summary>One value of a fixed list.</summary>
    public const string Enum = "enum";
}

/// <summary>
/// The content-free reasons why a request for input was answered without asking anyone; the audit
/// records them, never what the server asked.
/// </summary>
internal static class NhAssistantElicitationReasons
{
    /// <summary>No person can answer: a run, a consultation, a rule turn or a call outside a turn.</summary>
    public const string Unanswerable = "unanswerable";

    /// <summary>The request asks for something the chat cannot show safely, such as a nested value, a
    /// link that is not https or more than the limits allow.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>
    /// Nobody answered within the server's wait time, or the turn had no time for its person left.
    /// </summary>
    public const string Expired = "expired";

    /// <summary>The turn or the tool call ended while it waited.</summary>
    public const string Stopped = "stopped";
}

/// <summary>
/// The bounds of what an MCP server may show the person and ask of them.
/// </summary>
internal static class NhAssistantElicitationLimits
{
    public const int MaxMessageLength = 1_000;

    public const int MaxFields = 8;

    public const int MaxFieldNameLength = 64;

    public const int MaxTitleLength = 120;

    public const int MaxDescriptionLength = 300;

    public const int MaxOptions = 20;

    public const int MaxOptionLength = 120;

    /// <summary>The longest text a person may enter in one field.</summary>
    public const int MaxStringLength = 2_000;

    public const int MaxUrlLength = 2_048;

    /// <summary>The shortest wait time of a server, in minutes.</summary>
    public const int MinWaitMinutes = 1;

    /// <summary>The wait time of a server that sets none, in minutes.</summary>
    public const int DefaultWaitMinutes = 5;

    /// <summary>The longest wait time of a server, in minutes.</summary>
    public const int MaxWaitMinutes = 10;

    /// <summary>
    /// The longest a tool call waits for the person: the longest wait time of a server. The tool's own
    /// timeout and the turn's stand still meanwhile.
    /// </summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(MaxWaitMinutes);

    /// <summary>The most minutes one turn stands still for its person in all.</summary>
    public const int MaxTurnPauseMinutes = 10;

    /// <summary>
    /// The most time one turn stands still for its person in all, over every request for input it
    /// shows: a request waits no longer than what is left of it. One full wait of a server always fits.
    /// </summary>
    public static readonly TimeSpan MaxTurnPause = TimeSpan.FromMinutes(MaxTurnPauseMinutes);

    /// <summary>The wait time of a server that sets none.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(DefaultWaitMinutes);

    /// <summary>
    /// Whether <paramref name="minutes"/> is a wait time a server may have: whole minutes from
    /// <see cref="MinWaitMinutes"/> to <see cref="MaxWaitMinutes"/>.
    /// </summary>
    public static bool IsValidWait(int minutes)
    {
        return minutes is >= MinWaitMinutes and <= MaxWaitMinutes;
    }

    /// <summary>
    /// The wait time of a server as stored: <see cref="DefaultWait"/> when it sets none, and never
    /// outside the bounds, also for a value stored by hand.
    /// </summary>
    public static TimeSpan WaitOf(int? minutes)
    {
        return minutes is { } value
            ? TimeSpan.FromMinutes(Math.Clamp(value, MinWaitMinutes, MaxWaitMinutes))
            : DefaultWait;
    }
}

/// <summary>
/// One value of a list field: what the server receives and what the person reads.
/// </summary>
internal sealed record NhAssistantElicitationOption(string Value, string Label);

/// <summary>
/// One value a form of an MCP server asks for, already checked against
/// <see cref="NhAssistantElicitationLimits"/>.
/// </summary>
/// <param name="Name">The property name the server receives the value under.</param>
/// <param name="Type">One of <see cref="NhAssistantElicitationFieldTypes"/>.</param>
/// <param name="Title">What the person reads as the field's label.</param>
internal sealed record NhAssistantElicitationField(string Name, string Type, string Title, bool Required)
{
    public string? Description { get; init; }

    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    /// <summary>The values of an <see cref="NhAssistantElicitationFieldTypes.Enum"/> field.</summary>
    public IReadOnlyList<NhAssistantElicitationOption>? Options { get; init; }
}

/// <summary>
/// What an MCP server asks the turn's person while its tool runs, as the MCP layer checked it: its
/// message is the server's untrusted text, shown as the server's and never given to the model.
/// </summary>
/// <param name="ServerId">The administrator-connected server that asks.</param>
/// <param name="ServerName">The server's display name, which the person reads as the sender.</param>
/// <param name="Mode">One of <see cref="NhAssistantElicitationModes"/>.</param>
internal sealed record NhAssistantElicitationRequest(string ServerId, string ServerName, string Mode, string Message)
{
    public IReadOnlyList<NhAssistantElicitationField> Fields { get; init; } = [];

    /// <summary>The https page of a <see cref="NhAssistantElicitationModes.Url"/> request.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// How long the server lets its request wait for the person: the server's wait time, at most
    /// <see cref="NhAssistantElicitationLimits.MaxWait"/>; <see langword="null"/> waits
    /// <see cref="NhAssistantElicitationLimits.DefaultWait"/>. What is left of the turn's
    /// <see cref="NhAssistantElicitationLimits.MaxTurnPause"/> ends the wait earlier.
    /// </summary>
    public TimeSpan? Within { get; init; }
}

/// <summary>
/// The person's answer to a request for input, as the server receives it.
/// </summary>
/// <param name="Action">One of <see cref="NhAssistantElicitationActions"/>.</param>
internal sealed record NhAssistantElicitationOutcome(string Action)
{
    /// <summary>The values the person entered; only for an accepted form.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Content { get; init; }

    /// <summary>
    /// One of <see cref="NhAssistantElicitationReasons"/> when nobody was asked or nobody answered.
    /// </summary>
    public string? Reason { get; init; }

    public static NhAssistantElicitationOutcome Declined(string reason)
    {
        return new NhAssistantElicitationOutcome(NhAssistantElicitationActions.Decline) { Reason = reason };
    }

    public static NhAssistantElicitationOutcome Cancelled(string reason)
    {
        return new NhAssistantElicitationOutcome(NhAssistantElicitationActions.Cancel) { Reason = reason };
    }
}

/// <summary>
/// Asks the person of the tool call in progress for what an MCP server needs.
/// </summary>
internal interface INhAssistantElicitor
{
    /// <summary>
    /// Asks the person and waits for their answer. While they are asked, the clock of the waiting
    /// <paramref name="call"/> and the turn's own stand still and the call lets its concurrency slot go;
    /// the call's caller ending it, such as a person's stop, ends the wait at once.
    /// </summary>
    /// <param name="call">The timeout of the governed call that waits, or <see langword="null"/> for a
    /// call outside the invoker.</param>
    Task<NhAssistantElicitationOutcome> ElicitAsync(
        NhAssistantElicitationRequest request,
        NhAiToolCallTimeout? call,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a request the MCP layer declined without asking anyone, with one of
    /// <see cref="NhAssistantElicitationReasons"/>.
    /// </summary>
    Task RecordDeclinedAsync(NhAssistantElicitationRequest request, string reason);
}

/// <summary>
/// The elicitor of the governed tool call in progress in this asynchronous flow: set by the turn's
/// interceptor around each call, <see langword="null"/> outside a turn's call, for example in a call of
/// a tool a host exports to its own MCP clients. An MCP server's request for input that finds none is
/// declined.
/// </summary>
internal static class NhAssistantElicitationScope
{
    private static readonly AsyncLocal<INhAssistantElicitor?> CurrentElicitor = new();

    public static INhAssistantElicitor? Current => CurrentElicitor.Value;

    public static IDisposable Enter(INhAssistantElicitor? elicitor)
    {
        var previous = CurrentElicitor.Value;
        CurrentElicitor.Value = elicitor;
        return new Scope(previous);
    }

    private sealed class Scope(INhAssistantElicitor? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentElicitor.Value = previous;
        }
    }
}

/// <summary>
/// A request for input as the conversation shows it, attributed to the server that asks. The
/// person's answer is never part of it.
/// </summary>
/// <param name="ElicitationId">The request; the answer names it.</param>
/// <param name="InvocationId">The tool call that waits for it.</param>
/// <param name="AddresseeActorId">The person whose turn it is, who alone answers.</param>
/// <param name="ExpiresAt">The end of the wait: the server's wait time after the request, or the end of
/// the time the turn may still stand still for its person when that comes first.</param>
/// <param name="Status">One of <see cref="NhAssistantElicitationStatuses"/>.</param>
internal sealed record NhAssistantElicitationView(
    Guid ElicitationId,
    Guid InvocationId,
    string ServerId,
    string ServerName,
    string Mode,
    string Message,
    string AddresseeActorId,
    DateTimeOffset ExpiresAt,
    string Status) : NhAssistantPartView
{
    public IReadOnlyList<NhAssistantElicitationField> Fields { get; init; } = [];

    public string? Url { get; init; }

    /// <summary>The host of <see cref="Url"/>, which the person checks before opening it.</summary>
    public string? UrlHost => Url is not null && Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.IdnHost : null;

    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>
    /// The request as a stored part shows it; a request that still waits after its wait ended reads as
    /// expired.
    /// </summary>
    public static NhAssistantElicitationView? From(NhAssistantStoredPart part, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part is not
            {
                Type: NhAssistantStoredPart.ElicitationType,
                ElicitationId: { } elicitationId,
                InvocationId: { } invocationId,
                ServerId: { } serverId,
                Mode: { } mode,
                Text: { } message,
                AddresseeActorId: { } addressee,
                ExpiresAt: { } expiresAt,
                Status: { } status
            })
        {
            return null;
        }

        var effective = status == NhAssistantElicitationStatuses.Pending && now >= expiresAt
            ? NhAssistantElicitationStatuses.Expired
            : status;
        return new NhAssistantElicitationView(
            elicitationId,
            invocationId,
            serverId,
            part.DisplayName ?? serverId,
            mode,
            message,
            addressee,
            expiresAt,
            effective)
        {
            Fields = part.Fields ?? [],
            Url = part.LinkUrl,
            ClosedAt = part.ClosedAt
        };
    }
}

/// <summary>
/// An MCP server asked the turn's person for input while its tool runs; the tool call waits for the
/// answer.
/// </summary>
internal sealed record NhAssistantElicitationRequestedEvent(NhAssistantElicitationView Elicitation) : NhAssistantTurnEvent;

/// <summary>
/// The request for input was answered, declined, cancelled or expired; the tool call continues.
/// </summary>
internal sealed record NhAssistantElicitationClosedEvent(NhAssistantElicitationView Elicitation) : NhAssistantTurnEvent;

/// <summary>
/// Checks a person's answer to a form of an MCP server against its fields.
/// </summary>
internal static class NhAssistantElicitationAnswers
{
    /// <summary>
    /// Whether <paramref name="content"/> fits <paramref name="fields"/>: no unknown names, every required
    /// field and every value of its type within its bounds.
    /// </summary>
    public static bool Fits(
        IReadOnlyList<NhAssistantElicitationField> fields,
        IReadOnlyDictionary<string, JsonElement>? content)
    {
        ArgumentNullException.ThrowIfNull(fields);
        content ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (content.Keys.Any(name => !fields.Any(field => string.Equals(field.Name, name, StringComparison.Ordinal))))
        {
            return false;
        }

        foreach (var field in fields)
        {
            if (!content.TryGetValue(field.Name, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (field.Required)
                {
                    return false;
                }

                continue;
            }

            if (!FitsField(field, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FitsField(NhAssistantElicitationField field, JsonElement value)
    {
        switch (field.Type)
        {
            case NhAssistantElicitationFieldTypes.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var text = value.GetString()!;
                return text.Length <= NhAssistantElicitationLimits.MaxStringLength
                    && (field.MinLength is not { } min || text.Length >= min)
                    && (field.MaxLength is not { } max || text.Length <= max);
            case NhAssistantElicitationFieldTypes.Number:
            case NhAssistantElicitationFieldTypes.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    return false;
                }

                return (field.Type != NhAssistantElicitationFieldTypes.Integer || Math.Floor(number) == number)
                    && (field.Minimum is not { } minimum || number >= minimum)
                    && (field.Maximum is not { } maximum || number <= maximum);
            case NhAssistantElicitationFieldTypes.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case NhAssistantElicitationFieldTypes.Enum:
                return value.ValueKind == JsonValueKind.String
                    && field.Options is { } options
                    && options.Any(option => string.Equals(option.Value, value.GetString(), StringComparison.Ordinal));
            default:
                return false;
        }
    }
}

/// <summary>
/// The requests for input that wait for a person in this process. A request lives only while its tool
/// call waits: the answer goes straight back to the waiting call and is never stored. As for
/// <c>POST cancel</c>, an answer reaches only a call that runs in the same process, also when the
/// server's wait time is minutes long.
/// </summary>
internal sealed class NhAssistantElicitationRegistry
{
    private readonly ConcurrentDictionary<Guid, Waiter> _waiting = new();

    /// <summary>
    /// Registers a request that waits for <paramref name="addresseeActorId"/> until the returned waiter
    /// is disposed.
    /// </summary>
    public Waiter Register(
        Guid elicitationId,
        Guid conversationId,
        string addresseeActorId,
        string mode,
        IReadOnlyList<NhAssistantElicitationField> fields)
    {
        var waiter = new Waiter(this, elicitationId, conversationId, addresseeActorId, mode, fields);
        if (!_waiting.TryAdd(elicitationId, waiter))
        {
            throw new InvalidOperationException("A request for input with this id already waits.");
        }

        return waiter;
    }

    /// <summary>
    /// Hands the person's answer to the waiting call. Returns <see langword="null"/> when it was handed
    /// over, otherwise the stable code why not.
    /// </summary>
    public string? Answer(
        Guid conversationId,
        Guid elicitationId,
        string actorId,
        string? action,
        IReadOnlyDictionary<string, JsonElement>? content)
    {
        if (!_waiting.TryGetValue(elicitationId, out var waiter))
        {
            return NhAssistantErrorCodes.ElicitationNotPending;
        }
        if (waiter.ConversationId != conversationId)
        {
            return NhAssistantErrorCodes.ElicitationNotFound;
        }
        if (!string.Equals(waiter.AddresseeActorId, actorId, StringComparison.Ordinal))
        {
            // Only the person whose turn the tool runs in answers: an answer is given on their behalf.
            return NhAssistantErrorCodes.ElicitationForbidden;
        }
        if (!NhAssistantElicitationActions.IsKnown(action))
        {
            return NhAssistantErrorCodes.ElicitationAnswerInvalid;
        }

        IReadOnlyDictionary<string, JsonElement>? accepted = null;
        if (action == NhAssistantElicitationActions.Accept)
        {
            if (waiter.Mode == NhAssistantElicitationModes.Url)
            {
                // Done on the server's own page: the server learns nothing but that.
                if (content is { Count: > 0 })
                {
                    return NhAssistantErrorCodes.ElicitationAnswerInvalid;
                }
            }
            else
            {
                if (!NhAssistantElicitationAnswers.Fits(waiter.Fields, content))
                {
                    return NhAssistantErrorCodes.ElicitationAnswerInvalid;
                }

                accepted = content is null
                    ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    : content.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);
            }
        }

        var outcome = new NhAssistantElicitationOutcome(action!) { Content = accepted };
        return waiter.TryComplete(outcome) ? null : NhAssistantErrorCodes.ElicitationNotPending;
    }

    private void Remove(Waiter waiter)
    {
        _waiting.TryRemove(new KeyValuePair<Guid, Waiter>(waiter.ElicitationId, waiter));
    }

    /// <summary>
    /// One request that waits; disposing it ends the wait for answers.
    /// </summary>
    internal sealed class Waiter(
        NhAssistantElicitationRegistry registry,
        Guid elicitationId,
        Guid conversationId,
        string addresseeActorId,
        string mode,
        IReadOnlyList<NhAssistantElicitationField> fields) : IDisposable
    {
        private readonly TaskCompletionSource<NhAssistantElicitationOutcome> _answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid ElicitationId { get; } = elicitationId;

        public Guid ConversationId { get; } = conversationId;

        public string AddresseeActorId { get; } = addresseeActorId;

        public string Mode { get; } = mode;

        public IReadOnlyList<NhAssistantElicitationField> Fields { get; } = fields;

        public Task<NhAssistantElicitationOutcome> Answer => _answer.Task;

        public bool TryComplete(NhAssistantElicitationOutcome outcome)
        {
            return _answer.TrySetResult(outcome);
        }

        public void Dispose()
        {
            _answer.TrySetCanceled();
            registry.Remove(this);
        }
    }
}

/// <summary>
/// Answers the requests for input of the MCP servers whose tools a person's turn runs, for the
/// endpoint and the application's own code.
/// </summary>
internal sealed class NhAssistantElicitationAnswerService(
    Persistence.INhAssistantStore store,
    NhAssistantElicitationRegistry registry)
{
    /// <summary>
    /// Hands the answer of <paramref name="actorId"/> to the tool call that waits for it. Fails with
    /// <c>assistant-conversation-not-found</c> for a conversation the person cannot open, and with the
    /// codes of <see cref="NhAssistantElicitationRegistry.Answer"/>.
    /// </summary>
    public async Task<NewHeap.Platform.Common.Models.TaskResult> AnswerAsync(
        Guid conversationId,
        Guid elicitationId,
        string actorId,
        string? tenantId,
        string? action,
        IReadOnlyDictionary<string, JsonElement>? content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var access = await store.FindAccessAsync(conversationId, actorId, tenantId, cancellationToken);
        if (access is null)
        {
            return NewHeap.Platform.Common.Models.TaskResult.Failed(
                NhAssistantErrorCodes.ConversationNotFound,
                "The assistant conversation was not found.");
        }

        var code = registry.Answer(conversationId, elicitationId, actorId, action, content);
        return code is null
            ? NewHeap.Platform.Common.Models.TaskResult.Succeeded()
            : NewHeap.Platform.Common.Models.TaskResult.Failed(code, MessageOf(code));
    }

    private static string MessageOf(string code)
    {
        return code switch
        {
            NhAssistantErrorCodes.ElicitationForbidden => "Only the person whose turn the tool runs in answers the request.",
            NhAssistantErrorCodes.ElicitationAnswerInvalid => "The answer does not fit the request.",
            NhAssistantErrorCodes.ElicitationNotFound => "The request for input was not found.",
            _ => "The request for input no longer waits for an answer."
        };
    }
}
