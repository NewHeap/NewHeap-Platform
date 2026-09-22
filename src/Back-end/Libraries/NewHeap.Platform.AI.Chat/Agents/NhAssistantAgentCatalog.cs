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
}

/// <summary>
/// Reads the persisted agents. Code agents registered with <c>AddAgent</c> are upserted once
/// per process before the first read (and at host start), together with the application
/// context seed.
/// </summary>
internal sealed class NhAssistantAgentCatalog(
    NhAssistantAdminStore store,
    NhAssistantAgentRegistry registry,
    NhAssistantSeeder seeder)
{
    public async Task<IReadOnlyList<NhAssistantAgent>> ListAsync(
        bool includeDisabled,
        CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var rows = await store.ListAgentsAsync(cancellationToken);
        return rows
            .Select(ToAgent)
            .Where(agent => includeDisabled || agent.IsEnabled)
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
        return includeDisabled || agent.IsEnabled ? agent : null;
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
            row.UpdatedAt);
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
/// Upserts code agents and seeds the application context once per process.
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
                state.Agents.Select(agent => NhAssistantAgentCatalog.ToCodeRow(agent, now)).ToArray(),
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
}
