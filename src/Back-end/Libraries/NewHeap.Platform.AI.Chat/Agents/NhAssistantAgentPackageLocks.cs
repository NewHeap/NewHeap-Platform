namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The parts of an agent in an agent package (PK8 of the agent packages plan) and their keys: an
/// agent's definition (<c>agent</c>), its run settings (<c>runSettings</c>), and one key per data
/// skill (<c>skill:{id}</c>), data trigger (<c>trigger:{id}</c>), consultation defined as data
/// (<c>consultation:{subjectType}</c>), hand-off defined as data (<c>handOff:{subjectType}</c>) and
/// entry of agent metadata (<c>metadata:{name}</c>). A package locks parts by these keys, the
/// preview reports an outcome per key and an administrator unlinks one key at a time.
/// </summary>
public static class NhAssistantPackagePartKinds
{
    /// <summary>The agent's definition, or for an agent in code its overrides.</summary>
    public const string Agent = "agent";

    /// <summary>The run settings of an agent created in the administration, without the technical account.</summary>
    public const string RunSettings = "runSettings";

    /// <summary>A data skill, by skill id.</summary>
    public const string Skill = "skill";

    /// <summary>A data trigger, by trigger id.</summary>
    public const string Trigger = "trigger";

    /// <summary>A consultation defined as data, by subject type.</summary>
    public const string Consultation = "consultation";

    /// <summary>A hand-off defined as data that the agent accepts, by subject type.</summary>
    public const string HandOff = "handOff";

    /// <summary>The agent's value of an entry of agent metadata, by entry name.</summary>
    public const string Metadata = "metadata";

    /// <summary>
    /// The part key of every agent part a package names: the whole agent when an administrator
    /// unlinks it, never a key of a package's locks.
    /// </summary>
    public const string WholeAgent = "*";

    /// <summary>The kinds whose part key names an id, type or name after a colon.</summary>
    public static IReadOnlyList<string> KeyedKinds { get; } = [Skill, Trigger, Consultation, HandOff, Metadata];

    /// <summary>
    /// The part key of a kind and its id, type or name: <c>agent</c> and <c>runSettings</c> without
    /// one, the other kinds as <c>{kind}:{key}</c>.
    /// </summary>
    public static string PartOf(string kind, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return kind is Agent or RunSettings ? kind : kind + ":" + key;
    }

    /// <summary>
    /// Reads a part key into its kind and its id, type or name; <see langword="false"/> for a key of
    /// no known kind or a keyed kind without a valid dash-case key.
    /// </summary>
    public static bool TryParse(string? part, out string kind, out string key)
    {
        kind = string.Empty;
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(part))
        {
            return false;
        }
        if (part is Agent or RunSettings)
        {
            kind = part;
            return true;
        }

        var colon = part.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == part.Length - 1)
        {
            return false;
        }

        var partKind = part[..colon];
        var partKey = part[(colon + 1)..];
        if (!KeyedKinds.Contains(partKind, StringComparer.Ordinal) || !NhAssistantNames.IsSegment(partKey))
        {
            return false;
        }

        kind = partKind;
        key = partKey;
        return true;
    }
}

/// <summary>
/// The fields an agent package may lock per part kind (PK8): a lock names <see cref="All"/> for the
/// whole part or some of these fields. Every save path refuses a change of a locked field with the
/// field error <c>{field}: locked</c> (<see cref="NhAssistantFieldErrors.Locked"/>), named as in this
/// list; removing a part with locks, or deleting or resetting an agent linked to a package, fails with
/// <c>409 assistant-package-locked</c> until an administrator unlinks it. Operations stay free: run
/// state, pauses, the kill switch, shadow mode, the technical account and groups.
/// </summary>
public static class NhAssistantPackageLockFields
{
    /// <summary>The whole part: every field of its kind.</summary>
    public const string All = "*";

    /// <summary>
    /// The fields of an agent's definition. An agent in code carries only the first six, the ones an
    /// administrator may override on it.
    /// </summary>
    public static IReadOnlyList<string> Agent { get; } =
    [
        "displayName",
        "description",
        "instructions",
        "toolSelectors",
        "requiredPolicy",
        "autonomy",
        "offeredInChat",
        "worksOn",
        "profile",
        "dataClassification",
        "compositions",
        "scripts"
    ];

    /// <summary>
    /// The fields of the run settings of an agent created in the administration. The technical
    /// account is not one: it never travels and stays free.
    /// </summary>
    public static IReadOnlyList<string> RunSettings { get; } =
    [
        "accountability",
        "delegateFromInitiator",
        "allowGatewayReads",
        "elevatedReads",
        "approvalPolicy",
        "retryCount",
        "dailyBudget",
        "maxConcurrentRuns",
        "maxRunsPerSubjectPerDay",
        "maxTokensPerRun",
        "triageProfile",
        "ruleHandler",
        "files"
    ];

    /// <summary>The fields of a data skill.</summary>
    public static IReadOnlyList<string> Skill { get; } =
    [
        "description",
        "instructions",
        "tools",
        "profile",
        "ruleHandler",
        "maxTokensPerRun",
        "dailyBudget",
        "approvalPolicy",
        "dataClassification",
        "files"
    ];

    /// <summary>The fields of a data trigger.</summary>
    public static IReadOnlyList<string> Trigger { get; } =
    [
        "skill",
        "source",
        "event",
        "filters",
        "cron",
        "timeZone",
        "subjectSet",
        "message",
        "conflict",
        "debounceSeconds"
    ];

    /// <summary>The fields of a consultation defined as data.</summary>
    public static IReadOnlyList<string> Consultation { get; } =
    [
        "description",
        "instructions",
        "minimumRole",
        "askerPolicy",
        "routes",
        "proposalRole",
        "proposalPolicy",
        "toolSelectors",
        "limits",
        "allowRequests",
        "exposeToMcp",
        "dataClassification"
    ];

    /// <summary>The fields of a hand-off defined as data.</summary>
    public static IReadOnlyList<string> HandOff { get; } =
    [
        "sources",
        "description",
        "notifySource",
        "maxPerSubjectPerDay"
    ];

    /// <summary>The field of an entry of agent metadata.</summary>
    public static IReadOnlyList<string> Metadata { get; } = ["value"];

    /// <summary>
    /// The lockable fields of a part kind of <see cref="NhAssistantPackagePartKinds"/>; empty for an
    /// unknown kind.
    /// </summary>
    public static IReadOnlyList<string> Of(string kind)
    {
        return kind switch
        {
            NhAssistantPackagePartKinds.Agent => Agent,
            NhAssistantPackagePartKinds.RunSettings => RunSettings,
            NhAssistantPackagePartKinds.Skill => Skill,
            NhAssistantPackagePartKinds.Trigger => Trigger,
            NhAssistantPackagePartKinds.Consultation => Consultation,
            NhAssistantPackagePartKinds.HandOff => HandOff,
            NhAssistantPackagePartKinds.Metadata => Metadata,
            _ => []
        };
    }
}
