using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Collaboration;

/// <summary>
/// The names the model sees in a shared conversation. Names are normalized, so they cannot imitate
/// the <c>[name]</c> speaker marker, and are distinct per person.
/// </summary>
internal sealed class NhAssistantSpeakers
{
    private readonly Dictionary<string, string> _names;
    private readonly bool _dutch;

    private NhAssistantSpeakers(
        Dictionary<string, string> names,
        IReadOnlyList<string> people,
        string ownerActorId,
        string currentActorId,
        bool dutch)
    {
        _names = names;
        People = people;
        OwnerActorId = ownerActorId;
        CurrentActorId = currentActorId;
        _dutch = dutch;
    }

    public string OwnerActorId { get; }

    public string CurrentActorId { get; }

    /// <summary>
    /// The owner followed by the participants, in joining order.
    /// </summary>
    public IReadOnlyList<string> People { get; }

    public string CurrentName => NameOf(CurrentActorId);

    public static NhAssistantSpeakers Create(
        AssistantConversation conversation,
        IReadOnlyList<AssistantConversationParticipant> participants,
        string currentActorId,
        string language)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(participants);
        var dutch = language.StartsWith("nl", StringComparison.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var people = new List<string>(participants.Count + 1);

        Add(conversation.OwnerActorId, conversation.OwnerDisplayName, dutch ? "Eigenaar" : "Owner");
        var number = 1;
        foreach (var participant in participants)
        {
            Add(participant.ActorId, participant.DisplayName, (dutch ? "Deelnemer " : "Participant ") + number);
            number++;
        }
        return new NhAssistantSpeakers(names, people, conversation.OwnerActorId, currentActorId, dutch);

        void Add(string actorId, string? displayName, string fallback)
        {
            if (names.ContainsKey(actorId))
            {
                return;
            }
            var name = NhAssistantParticipantNames.Normalize(displayName);
            if (name.Length == 0)
            {
                name = fallback;
            }
            var distinct = name;
            var suffix = 2;
            while (!used.Add(distinct))
            {
                distinct = $"{name} ({suffix++})";
            }
            names[actorId] = distinct;
            people.Add(distinct);
        }
    }

    /// <summary>
    /// The name of a person; people who left the conversation get a neutral label.
    /// </summary>
    public string NameOf(string actorId)
    {
        return _names.TryGetValue(actorId, out var name)
            ? name
            : _dutch ? "Voormalige deelnemer" : "Former participant";
    }

    /// <summary>
    /// Prefixes a user message with its writer's name.
    /// </summary>
    public string Mark(string actorId, string text)
    {
        return "[" + NameOf(actorId) + "] " + text;
    }
}
