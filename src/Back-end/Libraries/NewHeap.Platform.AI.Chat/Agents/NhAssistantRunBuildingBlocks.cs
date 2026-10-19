using System.Text.RegularExpressions;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Configures a trigger event: an application event that data-defined triggers can start a skill of
/// an agent on; see <see cref="NhAssistantBuilder.AddTriggerEvent{TEvent}"/>. Its functions decide
/// what every trigger on the event receives, as those of a trigger in code do.
/// </summary>
public sealed class NhAssistantTriggerEventBuilder<TEvent>
    where TEvent : class
{
    private string _description = string.Empty;
    private Func<TEvent, NhAiSubjectRef>? _subject;
    private string? _subjectType;
    private Func<TEvent, string?> _initiator = _ => null;
    private Func<TEvent, string>? _message;
    private Func<TEvent, string?> _dedupe = _ => null;
    private Func<TEvent, bool>? _when;
    private Func<TEvent, object?>? _payload;

    internal NhAssistantTriggerEventBuilder()
    {
    }

    /// <summary>
    /// What the event means, at most 300 characters; the administration shows it.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> Description(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > NhAssistantSkillBuilder.MaxDescriptionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(description));
        }

        _description = description.Trim();
        return this;
    }

    /// <summary>
    /// Whether an event concerns any trigger at all, evaluated before every other function. Keep it
    /// cheap and free of I/O.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> When(Func<TEvent, bool> filter)
    {
        _when = filter ?? throw new ArgumentNullException(nameof(filter));
        return this;
    }

    /// <summary>
    /// The subject of an event, whose type is known only when a trigger on it fires; a start on a type
    /// the agent does not work on is refused then with <c>assistant-agent-does-not-work-on</c>. Prefer
    /// <see cref="Subject(string, Func{TEvent, string})"/> when the type is fixed.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> Subject(Func<TEvent, NhAiSubjectRef> subject)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _subjectType = null;
        return this;
    }

    /// <summary>
    /// The subject of an event as a fixed type and the id the event names. The type is known up
    /// front: the run catalog shows it, the administration offers the event only to agents that work
    /// on the type, and saving a data trigger for another agent is refused.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> Subject(string subjectType, Func<TEvent, string> id)
    {
        NhAssistantTriggerSubjects.ValidateType(subjectType);
        ArgumentNullException.ThrowIfNull(id);
        _subject = evt => new NhAiSubjectRef(subjectType, id(evt));
        _subjectType = subjectType;
        return this;
    }

    /// <summary>
    /// The person who caused the event, as an actor id, or <see langword="null"/> for an event
    /// from outside.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> Initiator(Func<TEvent, string?> initiator)
    {
        _initiator = initiator ?? throw new ArgumentNullException(nameof(initiator));
        return this;
    }

    /// <summary>
    /// The event message a run receives as its input.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> Message(Func<TEvent, string> message)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
        return this;
    }

    /// <summary>
    /// A key that identifies a repeated delivery of the same event, so it starts at most one run per
    /// trigger.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> Dedupe(Func<TEvent, string?> key)
    {
        _dedupe = key ?? throw new ArgumentNullException(nameof(key));
        return this;
    }

    /// <summary>
    /// A small typed payload stored with each event for rule handlers, at most 4,000 characters of
    /// JSON.
    /// </summary>
    public NhAssistantTriggerEventBuilder<TEvent> Payload(Func<TEvent, object?> payload)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        return this;
    }

    internal NhAssistantTriggerEventDefinition Build(string name)
    {
        if (_subject is null || _message is null)
        {
            throw new InvalidOperationException($"Trigger event '{name}' needs Subject and Message.");
        }

        var subject = _subject;
        var initiator = _initiator;
        var message = _message;
        var dedupe = _dedupe;
        var payload = _payload;
        var when = _when;
        return new NhAssistantTriggerEventDefinition(
            name,
            _description,
            typeof(TEvent),
            when is null ? null : evt => when((TEvent)evt),
            evt => subject((TEvent)evt),
            evt => initiator((TEvent)evt),
            evt => message((TEvent)evt),
            evt => dedupe((TEvent)evt),
            payload is null ? null : evt => payload((TEvent)evt))
        {
            SubjectType = _subjectType
        };
    }
}

/// <summary>
/// An application event that data-defined triggers name, with what every trigger on it receives.
/// </summary>
internal sealed record NhAssistantTriggerEventDefinition(
    string Name,
    string Description,
    Type EventType,
    Func<object, bool>? When,
    Func<object, NhAiSubjectRef> Subject,
    Func<object, string?> Initiator,
    Func<object, string> Message,
    Func<object, string?> Dedupe,
    Func<object, object?>? Payload)
{
    /// <summary>
    /// The subject type the event names up front; <see langword="null"/> when it is known only when a
    /// trigger on it fires.
    /// </summary>
    public string? SubjectType { get; init; }
}

/// <summary>
/// Checks the subject type a trigger names up front.
/// </summary>
internal static class NhAssistantTriggerSubjects
{
    public static void ValidateType(string subjectType)
    {
        if (!NhAiSubjectRef.IsValidType(subjectType))
        {
            throw new ArgumentException("A subject type is a dash-case identifier.", nameof(subjectType));
        }
        if (NhAiSubjectRef.IsReservedType(subjectType))
        {
            throw new ArgumentException($"'{subjectType}' belongs to the platform; a trigger cannot name it.", nameof(subjectType));
        }
    }
}

/// <summary>
/// The subjects a data-defined schedule starts a skill for on every tick, read in a scope of their own.
/// </summary>
internal sealed record NhAssistantSubjectSetDefinition(
    string Name,
    string Description,
    Func<IServiceProvider, CancellationToken, IAsyncEnumerable<NhAiSubjectRef>> Subjects);

/// <summary>
/// A rule handler registered under a name, for data-defined skills that work by rules.
/// </summary>
internal sealed record NhAssistantRuleHandlerDefinition(string Name, string Description, Type HandlerType);

/// <summary>
/// Names of the building blocks data refers to: dash-case segments, joined by dots, at most 100
/// characters, such as <c>project.task-completed</c>.
/// </summary>
internal static partial class NhAssistantBlockNames
{
    public const int MaxLength = 100;

    public static bool IsValid(string? name)
    {
        return name is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(name);
    }

    public static void Validate(string name, string parameterName)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException(
                "A name is made of dash-case segments joined by dots, at most 100 characters.",
                parameterName);
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
