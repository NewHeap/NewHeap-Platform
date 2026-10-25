using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// One registered entry of one agent as the administration shows it: whether it may change here, the
/// value in effect with where it comes from, and the value in code next to an override.
/// </summary>
internal sealed record NhAssistantAgentMetadataState(
    NhAssistantAgentMetadataEntry Entry,
    bool Editable,
    string? Source,
    string? Json,
    string? IndexValue,
    int? Version,
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    string? CodeJson,
    int AgentVersion)
{
    /// <summary>
    /// The searchable values of the value in effect of an entry declared with <c>IndexByEach</c>.
    /// </summary>
    public IReadOnlyList<string> IndexValues { get; init; } = [];
}

/// <summary>
/// Reads, checks and stores agent metadata (MD1 to MD3): the values in code, the values of agents
/// created at runtime, which change with the agent's next version, and the overrides of editable
/// entries of agents in code, with versions of their own. Every change is audited by entry name only.
/// </summary>
internal sealed class NhAssistantAgentMetadataService(
    NhAssistantRegistrationState registration,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentMetadataStore store,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks,
    NhAssistantPackageLockCheck locks) : INhAssistantAgentMetadata
{
    private const int MaxActorIdLength = 256;
    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public IReadOnlyList<NhAssistantAgentMetadataEntryDto> ListEntries()
    {
        return registration.AgentMetadata.Values
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.ToDto())
            .ToArray();
    }

    public async Task<NhAssistantAgentWithMetadata?> GetAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        return ToView(agent, await store.ListAsync(agentId, cancellationToken));
    }

    public async Task<IReadOnlyList<NhAssistantAgentWithMetadata>> ListAgentsAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!registration.AgentMetadata.TryGetValue(name, out var entry))
        {
            throw new ArgumentException($"Agent metadata '{name}' is not registered with AddAgentMetadata.", nameof(name));
        }
        if (!entry.Indexed)
        {
            throw new ArgumentException($"Agent metadata '{name}' declares no searchable value with IndexBy or IndexByEach.", nameof(name));
        }

        var indexValue = NhAssistantAgentMetadataEntry.NormalizeIndex(value);
        if (indexValue is null)
        {
            return [];
        }

        // Stored values (agents created at runtime and overrides) by their indexed column, or by the
        // table of searchable values of IndexByEach; values in code from the registration, matched in
        // memory. A stored override decides for its agent in code either way.
        var stored = entry.IndexedByEach
            ? await store.FindAgentsByIndexValueAsync(name, indexValue, cancellationToken)
            : (await store.FindByIndexAsync(name, indexValue, cancellationToken)).Select(row => row.AgentId).ToArray();
        var all = await agents.ListAsync(includeDisabled: true, cancellationToken);
        var candidates = stored
            .Concat(all
                .Where(agent => agent.Source == NhAssistantAgentSources.Code)
                .Where(agent => registration.CodeMetadataOf(agent.Id).TryGetValue(name, out var code) && code.Matches(indexValue))
                .Select(agent => agent.Id))
            .ToHashSet(StringComparer.Ordinal);
        if (candidates.Count == 0)
        {
            return [];
        }

        var rows = (await store.ListAsync(candidates, cancellationToken))
            .GroupBy(row => row.AgentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AssistantAgentMetadata>)group.ToArray(), StringComparer.Ordinal);
        return all
            .Where(agent => candidates.Contains(agent.Id))
            .Select(agent => ToView(agent, rows.GetValueOrDefault(agent.Id) ?? []))
            .Where(view => view.Metadata.Any(item => item.Name == name
                                                     && (item.IndexValue == indexValue || item.IndexValues.Contains(indexValue, StringComparer.Ordinal))))
            .OrderBy(view => view.AgentId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Every registered entry of the agent, ordered by name, as the administration shows it;
    /// <see langword="null"/> for an agent that does not exist.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantAgentMetadataState>?> StatesAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var rows = await store.ListAsync(agentId, cancellationToken);
        return registration.AgentMetadata.Values
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => StateOf(agent, entry, rows))
            .ToArray();
    }

    /// <summary>
    /// Checks the values an agent's definition carries, by entry name, as field errors under
    /// <c>metadata.&lt;name&gt;</c>; a JSON <c>null</c> removes the value. An agent in code gets none
    /// this way (<c>metadata: not-allowed</c>): its editable entries change one by one.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> CheckDefinition(
        IReadOnlyDictionary<string, JsonElement> values,
        bool inCode,
        NhAssistantValidationErrors errors)
    {
        var checkedValues = new Dictionary<string, NhAssistantAgentMetadataCheck?>(StringComparer.Ordinal);
        if (values.Count == 0)
        {
            return checkedValues;
        }
        if (inCode)
        {
            errors.Add("metadata", NhAssistantFieldErrors.NotAllowed);
            return checkedValues;
        }

        foreach (var (name, value) in values)
        {
            var field = "metadata." + name;
            if (!registration.AgentMetadata.TryGetValue(name, out var entry))
            {
                errors.Add(field, NhAssistantFieldErrors.NotFound);
                continue;
            }
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                checkedValues[name] = null;
                continue;
            }

            var check = entry.Check(value);
            check.AddTo(errors, field);
            checkedValues[name] = check;
        }

        return checkedValues;
    }

    /// <summary>
    /// Stores the value of one entry: for an agent created at runtime as the agent's next version when
    /// <paramref name="expectedVersion"/> is the agent's version, for an editable entry of an agent in
    /// code as an override (<see langword="null"/> for a new one, otherwise its version).
    /// </summary>
    public async Task<TaskResult<NhAssistantAgentMetadataState>> SaveAsync(
        string agentId,
        string name,
        JsonElement? value,
        int? expectedVersion,
        string actorId,
        CancellationToken cancellationToken)
    {
        ValidateActor(actorId);
        var (refused, agent, entry) = await ResolveAsync(agentId, name, cancellationToken);
        if (refused is not null)
        {
            return TaskResult<NhAssistantAgentMetadataState>.Failed(refused);
        }

        var check = value is { } json ? entry!.Check(json) : NhAssistantAgentMetadataCheck.Refused(NhAssistantFieldErrors.Required);
        var errors = new NhAssistantValidationErrors();
        check.AddTo(errors, "value");
        var dataAgent = agent!.Source == NhAssistantAgentSources.Admin;
        errors.Require(!dataAgent || expectedVersion is not null, "expectedVersion", NhAssistantFieldErrors.Required);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAgentMetadataState>.Failed(errors.ToResult());
        }

        // A value an agent package locks keeps the value it has or takes the package's (PK9).
        var current = (await store.ListAsync(agentId, cancellationToken)).FirstOrDefault(row => row.Name == name)?.ValueJson
                      ?? (dataAgent ? null : registration.CodeMetadataOf(agentId).GetValueOrDefault(name)?.Json);
        if (await locks.CheckChangeAsync(
                agentId,
                NhAssistantPackagePartKinds.Metadata,
                name,
                NhAssistantPackageFields.OfMetadata(current),
                NhAssistantPackageFields.OfMetadata(check.Json),
                cancellationToken) is { } locked)
        {
            return TaskResult<NhAssistantAgentMetadataState>.Failed(locked);
        }

        var now = DateTimeOffset.UtcNow;
        if (dataAgent)
        {
            var saved = await store.SaveDataAsync(
                agentId,
                expectedVersion!.Value,
                new Dictionary<string, NhAssistantAgentMetadataCheck?>(StringComparer.Ordinal) { [name] = check },
                actorId,
                now,
                cancellationToken);
            if (StoreFailure(saved.Outcome) is { } failure)
            {
                return TaskResult<NhAssistantAgentMetadataState>.Failed(failure);
            }

            await AuditAsync(agentId, saved.Value.Changes, actorId, cancellationToken);
        }
        else
        {
            var saved = await store.SaveOverrideAsync(agentId, name, expectedVersion, check, actorId, now, cancellationToken);
            if (!saved.Succeeded)
            {
                return TaskResult<NhAssistantAgentMetadataState>.Failed(VersionConflict());
            }

            var change = saved.Value!.Version == 1 ? NhAssistantAgentMetadataStore.Created : NhAssistantAgentMetadataStore.Changed;
            await AuditAsync(agentId, [new NhAssistantAgentMetadataChange(name, change, saved.Value.Version)], actorId, cancellationToken);
        }

        return await StateAfterChangeAsync(agentId, entry!, cancellationToken);
    }

    /// <summary>
    /// Removes the stored value of one entry: of an agent created at runtime as its next version, of an
    /// agent in code its override, so the value in code applies again. Without a stored value nothing
    /// changes.
    /// </summary>
    public async Task<TaskResult<NhAssistantAgentMetadataState>> RemoveAsync(
        string agentId,
        string name,
        int? expectedVersion,
        string actorId,
        CancellationToken cancellationToken)
    {
        ValidateActor(actorId);
        var (refused, agent, entry) = await ResolveAsync(agentId, name, cancellationToken);
        if (refused is not null)
        {
            return TaskResult<NhAssistantAgentMetadataState>.Failed(refused);
        }

        var rows = await store.ListAsync(agentId, cancellationToken);
        if (rows.All(row => row.Name != name))
        {
            return TaskResult<NhAssistantAgentMetadataState>.Succeeded(StateOf(agent!, entry!, rows));
        }
        if (expectedVersion is null)
        {
            return TaskResult<NhAssistantAgentMetadataState>.Failed(
                NhAssistantValidationErrors.Failed("expectedVersion", NhAssistantFieldErrors.Required));
        }
        if (await locks.CheckRemovalAsync(agentId, NhAssistantPackagePartKinds.Metadata, name, cancellationToken) is { } locked)
        {
            return TaskResult<NhAssistantAgentMetadataState>.Failed(locked);
        }

        if (agent!.Source == NhAssistantAgentSources.Admin)
        {
            var saved = await store.SaveDataAsync(
                agentId,
                expectedVersion.Value,
                new Dictionary<string, NhAssistantAgentMetadataCheck?>(StringComparer.Ordinal) { [name] = null },
                actorId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (StoreFailure(saved.Outcome) is { } failure)
            {
                return TaskResult<NhAssistantAgentMetadataState>.Failed(failure);
            }

            await AuditAsync(agentId, saved.Value.Changes, actorId, cancellationToken);
        }
        else
        {
            var removed = await store.RemoveOverrideAsync(agentId, name, expectedVersion, cancellationToken);
            if (!removed.Succeeded)
            {
                return TaskResult<NhAssistantAgentMetadataState>.Failed(VersionConflict());
            }
            if (removed.Value is { } row)
            {
                await AuditAsync(
                    agentId,
                    [new NhAssistantAgentMetadataChange(name, NhAssistantAgentMetadataStore.Removed, row.Version)],
                    actorId,
                    cancellationToken);
            }
        }

        return await StateAfterChangeAsync(agentId, entry!, cancellationToken);
    }

    /// <summary>
    /// Records each change content-free: the agent, the entry's name, the version and the change.
    /// </summary>
    public async Task AuditAsync(
        string agentId,
        IReadOnlyList<NhAssistantAgentMetadataChange> changes,
        string actorId,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            var evt = new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AdminAgentMetadataChanged,
                Guid.Empty,
                Guid.Empty,
                actorId,
                agentId,
                change.Version,
                DateTimeOffset.UtcNow)
            {
                ObjectId = change.Name,
                MetadataEntry = change.Name,
                ResultCode = change.Change
            };
            foreach (var sink in _auditSinks)
            {
                await sink.RecordAsync(evt, cancellationToken);
            }
        }
    }

    private async Task<TaskResult<NhAssistantAgentMetadataState>> StateAfterChangeAsync(
        string agentId,
        NhAssistantAgentMetadataEntry entry,
        CancellationToken cancellationToken)
    {
        // Another administrator may delete the agent right after the change.
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantAgentMetadataState>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        return TaskResult<NhAssistantAgentMetadataState>.Succeeded(
            StateOf(agent, entry, await store.ListAsync(agentId, cancellationToken)));
    }

    private async Task<(TaskResult? Refused, NhAssistantAgent? Agent, NhAssistantAgentMetadataEntry? Entry)> ResolveAsync(
        string agentId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!registration.AgentMetadata.TryGetValue(name, out var entry))
        {
            return (TaskResult.Failed(NhAssistantAdminErrorCodes.MetadataEntryNotFound, "No agent metadata entry with this name is registered."), null, null);
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."), null, null);
        }
        if (agent.Source != NhAssistantAgentSources.Admin && !entry.Editable)
        {
            return (TaskResult.Failed(NhAssistantAdminErrorCodes.MetadataReadOnly, "The entry of an agent in code is set in code."), null, null);
        }

        return (null, agent, entry);
    }

    /// <summary>
    /// The agent with the metadata in effect, from its stored <paramref name="rows"/>.
    /// </summary>
    public NhAssistantAgentWithMetadata ToView(NhAssistantAgent agent, IReadOnlyList<AssistantAgentMetadata> rows)
    {
        var values = registration.AgentMetadata.Values
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => StateOf(agent, entry, rows))
            .Where(state => state.Json is not null)
            .Select(state => new NhAssistantAgentMetadataValue(
                state.Entry.Name,
                Parse(state.Json!),
                state.Source!,
                state.Source == NhAssistantAgentMetadataSources.Code ? null : state.Version,
                state.IndexValue,
                state.ChangedAt,
                state.ChangedBy)
            {
                IndexValues = state.IndexValues
            })
            .ToArray();
        return new NhAssistantAgentWithMetadata(agent.Id, agent.Source, agent.Definition.Version, agent.Definition.DisplayNameKey, values);
    }

    /// <summary>
    /// The entry of the agent in effect: an agent created at runtime has its stored value; an agent in
    /// code its override when the entry is editable, otherwise its value in code.
    /// </summary>
    private NhAssistantAgentMetadataState StateOf(
        NhAssistantAgent agent,
        NhAssistantAgentMetadataEntry entry,
        IReadOnlyList<AssistantAgentMetadata> rows)
    {
        var row = rows.FirstOrDefault(item => string.Equals(item.Name, entry.Name, StringComparison.Ordinal));
        if (agent.Source == NhAssistantAgentSources.Admin)
        {
            return new NhAssistantAgentMetadataState(
                entry,
                Editable: true,
                row is null ? null : NhAssistantAgentMetadataSources.Data,
                row?.ValueJson,
                row?.IndexValue,
                agent.Definition.Version,
                row?.ChangedAt,
                row?.ChangedBy,
                CodeJson: null,
                agent.Definition.Version)
            {
                IndexValues = IndexValuesOf(row)
            };
        }

        var code = registration.CodeMetadataOf(agent.Id).GetValueOrDefault(entry.Name);
        if (entry.Editable && row is not null)
        {
            return new NhAssistantAgentMetadataState(
                entry,
                Editable: true,
                NhAssistantAgentMetadataSources.Override,
                row.ValueJson,
                row.IndexValue,
                row.Version,
                row.ChangedAt,
                row.ChangedBy,
                code?.Json,
                agent.Definition.Version)
            {
                IndexValues = IndexValuesOf(row)
            };
        }

        return new NhAssistantAgentMetadataState(
            entry,
            entry.Editable,
            code is null ? null : NhAssistantAgentMetadataSources.Code,
            code?.Json,
            code?.IndexValue,
            Version: null,
            ChangedAt: null,
            ChangedBy: null,
            code?.Json,
            agent.Definition.Version)
        {
            IndexValues = code?.IndexValues ?? []
        };
    }

    /// <summary>
    /// The stored searchable values of IndexByEach of a row, ordered as a check orders them.
    /// </summary>
    private static IReadOnlyList<string> IndexValuesOf(AssistantAgentMetadata? row)
    {
        return row is null
            ? []
            : row.IndexValues.Select(item => item.IndexValue).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// A stored value as JSON; detached from its document, so it outlives the parse.
    /// </summary>
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static TaskResult? StoreFailure(NhAssistantStoreOutcome outcome)
    {
        return outcome switch
        {
            NhAssistantStoreOutcome.Succeeded => null,
            NhAssistantStoreOutcome.NotFound => TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."),
            _ => VersionConflict()
        };
    }

    private static TaskResult VersionConflict()
    {
        return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The agent's metadata was changed by someone else.");
    }

    private static void ValidateActor(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }
    }
}
