using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What an agent works on: object types by id, all object types, the whole application (the
/// platform's <see cref="NhAiSubjectRef.SystemType"/> subject) or a combination, app-wide or per
/// division, and the policies of its runs and results for the whole application. Triggers, manual
/// starts, hand-offs, consultations and requests start the agent only on what it works on. An agent
/// that does not say works on all object types and not on the application (<see cref="Undeclared"/>);
/// "all object types" is also a choice of its own.
/// </summary>
public sealed record NhAssistantAgentWorksOn
{
    /// <summary>
    /// The most object types one agent names.
    /// </summary>
    public const int MaxSubjectTypes = 20;

    /// <summary>
    /// The longest run or view policy name.
    /// </summary>
    public const int MaxPolicyLength = 256;

    /// <summary>
    /// The longest stored list of object types, as JSON: <see cref="MaxSubjectTypes"/> type ids of
    /// at most <see cref="NhAiSubjectRef.MaxTypeLength"/> characters.
    /// </summary>
    public const int MaxStoredSubjectTypeCharacters = 2_100;

    /// <summary>
    /// What an agent works on that does not say: every object type, not the application, app-wide.
    /// </summary>
    public static NhAssistantAgentWorksOn Undeclared { get; } = new() { AllSubjects = true };

    /// <summary>
    /// The object types the agent works on, ordered by id; empty with <see cref="AllSubjects"/> or
    /// when the agent works on the application only.
    /// </summary>
    public IReadOnlyList<string> SubjectTypes { get; init; } = [];

    /// <summary>
    /// The agent works on every object type the application registers.
    /// </summary>
    public bool AllSubjects { get; init; }

    /// <summary>
    /// The agent works on the whole application.
    /// </summary>
    public bool Application { get; init; }

    /// <summary>
    /// The agent works per division instead of app-wide; offered only when the application lists its
    /// divisions.
    /// </summary>
    public bool PerDivision { get; init; }

    /// <summary>
    /// The authorization policy a person needs to run the agent for the whole application;
    /// <see langword="null"/> uses the assistant's administration policy.
    /// </summary>
    public string? RunPolicy { get; init; }

    /// <summary>
    /// The authorization policy a person needs to see the agent's results for the whole application;
    /// <see langword="null"/> uses the agent's required policy.
    /// </summary>
    public string? ViewPolicy { get; init; }

    /// <summary>
    /// Whether the agent said what it works on: in code with <c>WorksOn</c>,
    /// <c>WorksOnAllObjects</c> or <c>WorksOnApplication</c>, or as data. <see langword="false"/> for
    /// an agent in code that did not; it works on all object types.
    /// </summary>
    public bool Declared { get; init; }

    /// <summary>
    /// Whether the agent works on subjects of <paramref name="subjectType"/>. Platform types other
    /// than the whole application are never covered.
    /// </summary>
    public bool Covers(string subjectType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        if (string.Equals(subjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal))
        {
            return Application;
        }
        if (NhAiSubjectRef.IsReservedType(subjectType))
        {
            return false;
        }

        return AllSubjects || SubjectTypes.Contains(subjectType, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether the agent works on <paramref name="subject"/>: on its type, and for the whole
    /// application on the scope that fits how the agent works: <c>nh-system/global</c> app-wide,
    /// <c>nh-system/division-{id}</c> per division.
    /// </summary>
    public bool Covers(NhAiSubjectRef subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!Covers(subject.Type))
        {
            return false;
        }
        if (!NhAiSystemSubjects.IsSystem(subject))
        {
            return true;
        }

        return PerDivision
            ? NhAiSystemSubjects.TryGetDivision(subject, out _)
            : NhAiSystemSubjects.IsGlobal(subject);
    }

    /// <summary>
    /// Whether two statements say the same: equal types, choices and policies.
    /// </summary>
    public bool IsEquivalentTo(NhAssistantAgentWorksOn other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SubjectTypes.SequenceEqual(other.SubjectTypes, StringComparer.Ordinal)
               && AllSubjects == other.AllSubjects
               && Application == other.Application
               && PerDivision == other.PerDivision
               && string.Equals(RunPolicy, other.RunPolicy, StringComparison.Ordinal)
               && string.Equals(ViewPolicy, other.ViewPolicy, StringComparison.Ordinal)
               && Declared == other.Declared;
    }

    /// <summary>
    /// What an agent created in the administration works on, from its row. An unreadable list reads
    /// as no object types.
    /// </summary>
    internal static NhAssistantAgentWorksOn FromRow(AssistantAgent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new NhAssistantAgentWorksOn
        {
            SubjectTypes = ReadTypes(row.WorksOnSubjectTypesJson),
            AllSubjects = row.WorksOnAllSubjects,
            Application = row.WorksOnApplication,
            PerDivision = row.PerDivision,
            RunPolicy = row.RunPolicy,
            ViewPolicy = row.ViewPolicy,
            Declared = true
        };
    }

    /// <summary>
    /// Stores what an agent created in the administration works on in its row.
    /// </summary>
    internal void ApplyTo(AssistantAgent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.WorksOnSubjectTypesJson = JsonSerializer.Serialize(SubjectTypes);
        row.WorksOnAllSubjects = AllSubjects;
        row.WorksOnApplication = Application;
        row.PerDivision = PerDivision;
        row.RunPolicy = RunPolicy;
        row.ViewPolicy = ViewPolicy;
    }

    /// <summary>
    /// What an administrator or an import says an agent works on, or <see langword="null"/> when it
    /// says nothing, which keeps what an existing agent works on and lets a new one work on every
    /// object type. Without object types, all objects or the application the agent works on all
    /// object types; a missing choice reads as not chosen. Types are trimmed, distinct and ordered;
    /// blank policies read as none.
    /// </summary>
    internal static NhAssistantAgentWorksOn? FromInput(
        IReadOnlyList<string>? subjectTypes,
        bool? allSubjects,
        bool? application,
        bool? perDivision,
        string? runPolicy,
        string? viewPolicy)
    {
        if (subjectTypes is null && allSubjects is null && application is null
            && perDivision is null && runPolicy is null && viewPolicy is null)
        {
            return null;
        }

        var nothingChosen = subjectTypes is null && allSubjects is null && application is null;
        return new NhAssistantAgentWorksOn
        {
            SubjectTypes = Normalize(subjectTypes ?? []),
            AllSubjects = nothingChosen || allSubjects == true,
            Application = application == true,
            PerDivision = perDivision == true,
            RunPolicy = string.IsNullOrWhiteSpace(runPolicy) ? null : runPolicy.Trim(),
            ViewPolicy = string.IsNullOrWhiteSpace(viewPolicy) ? null : viewPolicy.Trim(),
            Declared = true
        };
    }

    internal static IReadOnlyList<string> Normalize(IEnumerable<string?> subjectTypes)
    {
        return subjectTypes
            .Select(type => type?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadTypes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<string[]>(json) ?? []);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// An authorization policy the application lists with <c>AddAgentPolicy</c>, so administrators can
/// choose it as the run or view policy of an agent created in the administration.
/// </summary>
internal sealed record NhAssistantAgentPolicyDefinition(string Name, string Description);

/// <summary>
/// The one rule of what an agent works on, for agents in code and as data alike: the statement in
/// code, or the one an administrator stored for an agent created in the administration, which the
/// run configuration snapshot carries. Triggers (events, schedules, webhooks, follow-ups), manual
/// starts, hand-offs, consultations and requests ask it; runs for the whole application and proposals
/// placed at other objects will too.
/// </summary>
internal sealed class NhAssistantWorksOnRule(
    NhAssistantRegistrationState registration,
    IServiceProviderIsService? services = null)
{
    /// <summary>
    /// Whether the application lists its divisions with an <see cref="INhDivisionDirectory"/>, as
    /// NewHeap Identity does, so agents may work per division. Without one everything runs app-wide.
    /// </summary>
    public bool DivisionsListed => services?.IsService(typeof(INhDivisionDirectory)) == true;

    /// <summary>
    /// What the agent works on in effect: its run configuration's statement, or
    /// <see cref="NhAssistantAgentWorksOn.Undeclared"/> for an agent without runs.
    /// </summary>
    public NhAssistantAgentWorksOn Of(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return registration.AgentRuns.TryGetValue(agentId, out var options) ? options.WorksOn : NhAssistantAgentWorksOn.Undeclared;
    }

    /// <summary>
    /// Whether the agent works on subjects of <paramref name="subjectType"/>.
    /// </summary>
    public bool Allows(string agentId, string subjectType)
    {
        return Of(agentId).Covers(subjectType);
    }

    /// <summary>
    /// <see cref="NhAssistantErrorCodes.AgentDoesNotWorkOn"/> when the agent does not work on subjects
    /// of <paramref name="subjectType"/>, otherwise <see langword="null"/>.
    /// </summary>
    public string? RefusalOf(string agentId, string subjectType)
    {
        return Allows(agentId, subjectType) ? null : NhAssistantErrorCodes.AgentDoesNotWorkOn;
    }

    /// <summary>
    /// Whether the agent works on <paramref name="subject"/>; for the whole application also on its
    /// scope: app-wide agents on <c>nh-system/global</c>, per-division agents on a division.
    /// </summary>
    public bool Allows(string agentId, NhAiSubjectRef subject)
    {
        return Of(agentId).Covers(subject);
    }

    /// <summary>
    /// <see cref="NhAssistantErrorCodes.AgentDoesNotWorkOn"/> when the agent does not work on
    /// <paramref name="subject"/>, otherwise <see langword="null"/>.
    /// </summary>
    public string? RefusalOf(string agentId, NhAiSubjectRef subject)
    {
        return Allows(agentId, subject) ? null : NhAssistantErrorCodes.AgentDoesNotWorkOn;
    }

    /// <summary>
    /// Checks what an administrator or an import says an agent works on: at least one object type,
    /// all objects or the application, not types next to all objects, at most
    /// <see cref="NhAssistantAgentWorksOn.MaxSubjectTypes"/> registered application types, per
    /// division only while the application lists divisions, and run and view policies from those the
    /// application lists with <c>AddAgentPolicy</c>.
    /// </summary>
    public void Validate(
        NhAssistantAgentWorksOn worksOn,
        NhAssistantSubjectAccessService subjects,
        NhAssistantValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(worksOn);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(errors);
        errors
            .Require(worksOn.SubjectTypes.Count > 0 || worksOn.AllSubjects || worksOn.Application, "worksOn", NhAssistantFieldErrors.Required)
            .Require(!(worksOn.AllSubjects && worksOn.SubjectTypes.Count > 0), "worksOn", NhAssistantFieldErrors.Invalid)
            .Require(worksOn.SubjectTypes.Count <= NhAssistantAgentWorksOn.MaxSubjectTypes, "worksOnSubjectTypes", NhAssistantFieldErrors.TooLong)
            .Require(worksOn.SubjectTypes.All(NhAiSubjectRef.IsValidType), "worksOnSubjectTypes", NhAssistantFieldErrors.Invalid)
            .Require(
                worksOn.SubjectTypes.Where(NhAiSubjectRef.IsValidType).All(type => !NhAiSubjectRef.IsReservedType(type) && subjects.IsKnownType(type)),
                "worksOnSubjectTypes",
                NhAssistantFieldErrors.NotFound)
            .Require(!worksOn.PerDivision || DivisionsListed, "perDivision", NhAssistantFieldErrors.Invalid)
            .Require(worksOn.RunPolicy is null || registration.AgentPolicies.ContainsKey(worksOn.RunPolicy), "runPolicy", NhAssistantFieldErrors.NotFound)
            .Require(worksOn.ViewPolicy is null || registration.AgentPolicies.ContainsKey(worksOn.ViewPolicy), "viewPolicy", NhAssistantFieldErrors.NotFound);
    }
}

/// <summary>
/// The object types agents are known to work on, as a hint next to "all object types": the types
/// they accept hand-offs and consultations about, the types their triggers name up front (in code
/// and as data) and the types of their existing threads.
/// </summary>
internal sealed class NhAssistantAgentKnownSubjectTypes(
    NhAssistantRegistrationState registration,
    NhAssistantRunStore runs)
{
    /// <summary>
    /// The most agent and thread type pairs one hint reads.
    /// </summary>
    internal const int MaxThreadPairs = 2_000;

    /// <summary>
    /// The known types by agent id, ordered by id; <paramref name="agentIds"/> limits the agents,
    /// <see langword="null"/> reads every agent. Platform types are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListAsync(
        IReadOnlyCollection<string>? agentIds,
        CancellationToken cancellationToken)
    {
        var known = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string agentId, string? subjectType)
        {
            if (subjectType is null
                || NhAiSubjectRef.IsReservedType(subjectType)
                || (agentIds is not null && !agentIds.Contains(agentId, StringComparer.Ordinal)))
            {
                return;
            }
            if (!known.TryGetValue(agentId, out var types))
            {
                known[agentId] = types = new SortedSet<string>(StringComparer.Ordinal);
            }
            types.Add(subjectType);
        }

        foreach (var options in registration.AgentRuns.Values)
        {
            foreach (var handOff in options.HandOffs)
            {
                Add(options.AgentId, handOff.SubjectType);
            }
            foreach (var consultation in options.Consultations)
            {
                Add(options.AgentId, consultation.SubjectType);
            }
        }
        foreach (var trigger in registration.Triggers.Values)
        {
            Add(trigger.AgentId, trigger.SubjectType);
        }
        foreach (var (agentId, subjectType) in await runs.ListThreadSubjectTypesAsync(agentIds, MaxThreadPairs, cancellationToken))
        {
            Add(agentId, subjectType);
        }

        return known.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.ToArray(),
            StringComparer.Ordinal);
    }
}
