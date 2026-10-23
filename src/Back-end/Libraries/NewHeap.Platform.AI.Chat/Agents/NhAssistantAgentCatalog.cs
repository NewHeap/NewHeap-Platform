using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// An agent as it is used at runtime: its persisted definition, MCP assignments and state.
/// </summary>
internal sealed record NhAssistantAgent(
    NhAssistantAgentDefinition Definition,
    IReadOnlyList<string> McpServerIds,
    bool IsEnabled,
    string Source,
    bool IsOverridden,
    DateTimeOffset UpdatedAt)
{
    public string Id => Definition.Id;

    /// <summary>
    /// The stored run state: active, paused, stopped or off.
    /// </summary>
    public string RunState { get; init; } = NhAssistantAgentRunStates.Active;

    public DateTimeOffset? StateChangedAt { get; init; }

    public string? StateChangedBy { get; init; }

    public string? StateReason { get; init; }

    /// <summary>
    /// The agent is stopped and was off before: releasing the kill switch switches it off again.
    /// </summary>
    public bool OffBeforeStop { get; init; }

    /// <summary>
    /// The shadow mode an administrator set at runtime; <see langword="null"/> follows the code.
    /// </summary>
    public bool? ShadowOverride { get; init; }

    public DateTimeOffset? ShadowChangedAt { get; init; }

    public string? ShadowChangedBy { get; init; }

    public string? ShadowReason { get; init; }

    /// <summary>
    /// The technical account an administrator set at runtime; <see langword="null"/> uses the one in
    /// code.
    /// </summary>
    public string? ServiceAccountOverride { get; init; }

    public DateTimeOffset? ServiceAccountChangedAt { get; init; }

    public string? ServiceAccountChangedBy { get; init; }

    public string? ServiceAccountReason { get; init; }

    /// <summary>
    /// The routes of the agent's consultations an administrator switched at runtime; each
    /// <see langword="null"/> route follows the code.
    /// </summary>
    public NhAssistantConsultationRouteOverrides ConsultationRoutes { get; init; } = NhAssistantConsultationRouteOverrides.None;

    public DateTimeOffset? ConsultationRoutesChangedAt { get; init; }

    public string? ConsultationRoutesChangedBy { get; init; }

    public string? ConsultationRoutesReason { get; init; }

    /// <summary>
    /// Whether people can choose the agent in the chat; see <c>HideFromChat()</c>.
    /// </summary>
    public bool OfferedInChat { get; init; } = true;

    /// <summary>
    /// What the agent works on: the statement in code for an agent in code, the stored one for an
    /// agent created in the administration.
    /// </summary>
    public NhAssistantAgentWorksOn WorksOn { get; init; } = NhAssistantAgentWorksOn.Undeclared;

    /// <summary>
    /// The classification of the data the agent's model calls carry: <c>DataClassification(...)</c>
    /// in code for an agent in code, the stored one for an agent created in the administration;
    /// <see langword="null"/> for <see cref="NhAiDataClassification.Internal"/>. A skill's own wins.
    /// </summary>
    public NhAiDataClassification? DataClassification { get; init; }

    /// <summary>
    /// The live agent an agent created in the administration is a data shadow variant of, as stored
    /// when it was created, whether or not its run settings are in effect; <see langword="null"/> for
    /// every other agent. A variant in code says it in its run configuration (<c>ShadowOf</c>).
    /// </summary>
    public string? ShadowOfAgentId { get; init; }
}

/// <summary>
/// Reads the persisted agents. Code agents registered with <c>AddAgent</c> are upserted once
/// per process before the first read (and at host start), together with the application
/// context seed. Without <c>includeDisabled</c>, stopped agents and agents disabled in code with
/// <c>Enabled(false)</c> are left out.
/// </summary>
internal sealed class NhAssistantAgentCatalog(
    NhAssistantAdminStore store,
    NhAssistantAgentRegistry registry,
    NhAssistantSeeder seeder,
    NhAssistantRegistrationState registration)
{
    public async Task<IReadOnlyList<NhAssistantAgent>> ListAsync(
        bool includeDisabled,
        CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var rows = await store.ListAgentsAsync(cancellationToken);
        return rows
            .Select(ToAgent)
            .Where(agent => includeDisabled || IsWorking(agent))
            .ToArray();
    }

    public async Task<NhAssistantAgent?> FindAsync(
        string agentId,
        bool includeDisabled,
        CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var row = await store.FindAgentAsync(agentId, cancellationToken);
        if (row is null)
        {
            return null;
        }
        var agent = ToAgent(row);
        return includeDisabled || IsWorking(agent) ? agent : null;
    }

    /// <summary>
    /// Whether the agent is neither stopped nor disabled in code; a paused agent works for people.
    /// </summary>
    private bool IsWorking(NhAssistantAgent agent)
    {
        return agent.IsEnabled
            && !(registration.AgentRuns.TryGetValue(agent.Id, out var runOptions) && !runOptions.Enabled);
    }

    /// <summary>
    /// The stored run state of an agent without reading its definition, or <see langword="null"/>
    /// when the agent does not exist.
    /// </summary>
    public async Task<string?> FindRunStateAsync(string agentId, CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var state = await store.FindAgentRunStateAsync(agentId, cancellationToken);
        return state is null || NhAssistantAgentRunStates.IsStored(state) ? state : NhAssistantAgentRunStates.Active;
    }

    public NhAssistantAgent ToAgent(AssistantAgent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var selectors = JsonSerializer.Deserialize<string[]>(row.ToolSelectorsJson) ?? [];
        NhAiTextAsset instructions;
        if (row.Source == NhAssistantAgentSources.Code
            && !row.IsOverridden
            && registry.TryGet(row.Id, out var code)
            && string.Equals(code.Instructions.Manifest.ContentHash, row.InstructionsHash, StringComparison.Ordinal))
        {
            instructions = code.Instructions;
        }
        else
        {
            instructions = CreateAsset(row.InstructionsAssetId, row.InstructionsAssetVersion, row.Instructions, row.Id);
        }

        var definition = new NhAssistantAgentDefinition(
            row.Id,
            row.Version,
            row.DisplayName,
            row.Description,
            row.ProfileName,
            instructions,
            selectors,
            row.Autonomy,
            row.RequiredPolicy);
        return new NhAssistantAgent(
            definition,
            row.McpServers.Select(server => server.McpServerId).Order(StringComparer.Ordinal).ToArray(),
            row.IsEnabled,
            row.Source,
            row.IsOverridden,
            row.UpdatedAt)
        {
            RunState = !row.IsEnabled
                ? NhAssistantAgentRunStates.Stopped
                : NhAssistantAgentRunStates.IsStored(row.RunState) ? row.RunState : NhAssistantAgentRunStates.Active,
            StateChangedAt = row.StateChangedAt,
            StateChangedBy = row.StateChangedBy,
            StateReason = row.StateReason,
            OffBeforeStop = !row.IsEnabled && row.OffBeforeStop,
            ShadowOverride = row.ShadowOverride,
            ShadowChangedAt = row.ShadowChangedAt,
            ShadowChangedBy = row.ShadowChangedBy,
            ShadowReason = row.ShadowReason,
            ServiceAccountOverride = row.ServiceAccountActorId,
            ServiceAccountChangedAt = row.ServiceAccountChangedAt,
            ServiceAccountChangedBy = row.ServiceAccountChangedBy,
            ServiceAccountReason = row.ServiceAccountReason,
            ConsultationRoutes = new NhAssistantConsultationRouteOverrides(
                row.ConsultAskerDecides,
                row.ConsultSubjectAgentRights,
                row.ConsultSubjectAskerRights),
            ConsultationRoutesChangedAt = row.ConsultRoutesChangedAt,
            ConsultationRoutesChangedBy = row.ConsultRoutesChangedBy,
            ConsultationRoutesReason = row.ConsultRoutesReason,
            OfferedInChat = row.Source == NhAssistantAgentSources.Code
                ? !(registration.AgentRuns.TryGetValue(row.Id, out var runOptions) && runOptions.HiddenFromChat)
                : !row.HiddenFromChat,
            WorksOn = row.Source == NhAssistantAgentSources.Code
                ? registration.CodeAgentRuns.TryGetValue(row.Id, out var codeOptions) ? codeOptions.WorksOn : NhAssistantAgentWorksOn.Undeclared
                : NhAssistantAgentWorksOn.FromRow(row),
            DataClassification = ClassificationOf(row),
            ShadowOfAgentId = row.Source == NhAssistantAgentSources.Admin ? row.ShadowOfAgentId : null
        };
    }

    /// <summary>
    /// The agent's own classification: from its code for an agent in code, stored for an agent created
    /// in the administration.
    /// </summary>
    private NhAiDataClassification? ClassificationOf(AssistantAgent row)
    {
        if (row.Source == NhAssistantAgentSources.Code)
        {
            return registration.CodeAgentRuns.TryGetValue(row.Id, out var options) ? options.DataClassification : null;
        }

        return NhAssistantDataClassificationNames.TryParse(row.DataClassification, out var classification)
            ? classification
            : null;
    }

    /// <summary>
    /// Maps a code definition to its persisted row.
    /// </summary>
    public static AssistantAgent ToCodeRow(NhAssistantAgentDefinition definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var row = new AssistantAgent
        {
            Id = definition.Id,
            Version = 1,
            Source = NhAssistantAgentSources.Code,
            IsOverridden = false,
            DisplayName = definition.DisplayNameKey,
            Description = definition.DescriptionKey,
            ProfileName = definition.ProfileName,
            Instructions = definition.Instructions.Content,
            InstructionsAssetId = definition.Instructions.Manifest.Id,
            InstructionsAssetVersion = definition.Instructions.Manifest.Version,
            InstructionsHash = definition.Instructions.Manifest.ContentHash,
            ToolSelectorsJson = JsonSerializer.Serialize(definition.ToolSelectors),
            RequiredPolicy = definition.RequiredPolicy,
            Autonomy = definition.Autonomy,
            IsEnabled = true,
            UpdatedAt = now
        };
        row.CodeHash = ComputeCodeHash(row);
        return row;
    }

    /// <summary>
    /// Creates the instruction asset of an administrator-edited agent.
    /// </summary>
    public static NhAiTextAsset CreateAsset(string assetId, int assetVersion, string content, string agentId)
    {
        return NhAiTextAssetFactory.Create(
            assetId,
            assetVersion,
            content,
            "assistant-admin:" + agentId,
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            $"{agentId}-v{assetVersion.ToString(CultureInfo.InvariantCulture)}");
    }

    public static string ComputeHash(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static string ComputeCodeHash(AssistantAgent row)
    {
        return ComputeHash(JsonSerializer.Serialize(new
        {
            row.DisplayName,
            row.Description,
            row.ProfileName,
            row.InstructionsAssetId,
            row.InstructionsAssetVersion,
            row.InstructionsHash,
            row.ToolSelectorsJson,
            row.RequiredPolicy,
            Autonomy = row.Autonomy.ToString()
        }));
    }
}

/// <summary>
/// Upserts code agents, records the skills in code and seeds the application context once per
/// process. An agent or skill with <c>StartsOff()</c> is stored off when it is seen for the first
/// time; after that its stored state wins.
/// </summary>
internal sealed class NhAssistantSeeder(
    NhAssistantDbContextFactory contextFactory,
    NhAssistantRegistrationState state)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _seeded;

    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (_seeded)
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_seeded)
            {
                return;
            }
            var store = new NhAssistantAdminStore(contextFactory);
            var now = DateTimeOffset.UtcNow;
            await store.UpsertCodeAgentsAsync(
                state.Agents.Select(agent => FirstRow(agent, now)).ToArray(),
                cancellationToken);
            var agentIds = state.Agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
            await store.RecordCodeSkillsAsync(
                state.CodeAgentRuns.Values
                    .Where(options => agentIds.Contains(options.AgentId))
                    .SelectMany(options => options.Skills.Values)
                    .Select(skill => (skill.AgentId, skill.Id, skill.StartsOff))
                    .ToArray(),
                cancellationToken);
            // Threads of agents that work in shadow by their code stay with administrators.
            await store.MarkShadowThreadsAsync(
                state.AgentRuns.Values.Where(options => options.ShadowOfAgentId is not null).Select(options => options.AgentId).ToArray(),
                state.AgentRuns.Values.Where(options => options.ShadowOfAgentId is null && options.Shadow).Select(options => options.AgentId).ToArray(),
                cancellationToken);
            if (state.DefaultApplicationContext is { } seed)
            {
                await store.SeedApplicationContextAsync(
                    seed.Content,
                    NhAssistantAgentCatalog.ComputeHash(seed.Content),
                    cancellationToken);
            }
            _seeded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The row a code agent gets when the application sees it for the first time: off with
    /// <c>StartsOff()</c>, otherwise active. An existing row keeps its state.
    /// </summary>
    private AssistantAgent FirstRow(NhAssistantAgentDefinition agent, DateTimeOffset now)
    {
        var row = NhAssistantAgentCatalog.ToCodeRow(agent, now);
        if (state.CodeAgentRuns.TryGetValue(agent.Id, out var options) && options.StartsOff)
        {
            row.RunState = NhAssistantAgentRunStates.Off;
        }

        return row;
    }
}
