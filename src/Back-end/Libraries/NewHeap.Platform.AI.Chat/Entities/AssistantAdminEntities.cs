namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// A persisted agent. Code agents (<see cref="NhAssistantAgentSources.Code"/>) are upserted from
/// <c>AddAgent</c> at startup; administrators may override, disable and reset them, and create
/// their own agents (<see cref="NhAssistantAgentSources.Admin"/>).
/// </summary>
public sealed class AssistantAgent
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Optimistic concurrency version; incremented on every change.
    /// </summary>
    public int Version { get; set; } = 1;

    public string Source { get; set; } = NhAssistantAgentSources.Admin;

    public bool IsOverridden { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;

    public string InstructionsAssetId { get; set; } = string.Empty;

    public int InstructionsAssetVersion { get; set; } = 1;

    public string InstructionsHash { get; set; } = string.Empty;

    public string ToolSelectorsJson { get; set; } = "[]";

    public string? RequiredPolicy { get; set; }

    public NhAiAutonomyLevel Autonomy { get; set; } = NhAiAutonomyLevel.Observe;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Hash of the code definition last applied, so unchanged code does not bump the version.
    /// </summary>
    public string? CodeHash { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public List<AssistantAgentMcpServer> McpServers { get; set; } = [];
}

/// <summary>
/// Assignment of an MCP server to an agent.
/// </summary>
public sealed class AssistantAgentMcpServer
{
    public string AgentId { get; set; } = string.Empty;

    public string McpServerId { get; set; } = string.Empty;
}

/// <summary>
/// One version of the application context. The highest version is current; history is kept.
/// </summary>
public sealed class AssistantApplicationContext
{
    public string Id { get; set; } = NhAssistantApplicationContexts.DefaultId;

    public int Version { get; set; }

    public string Text { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

/// <summary>
/// An administrator-connected remote MCP server. The secret is stored protected and never returned.
/// </summary>
public sealed class AssistantMcpServer
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string AuthMode { get; set; } = NhAssistantMcpAuthModes.None;

    public string? HeaderName { get; set; }

    public string? ProtectedSecret { get; set; }

    public string? RequiredPolicy { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? LastSyncAt { get; set; }

    public string? LastSyncStatus { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AssistantMcpTool> Tools { get; set; } = [];
}

/// <summary>
/// A remote tool discovered by a sync. Disabled until an administrator enables it.
/// </summary>
public sealed class AssistantMcpTool
{
    public string ServerId { get; set; } = string.Empty;

    public string RemoteName { get; set; } = string.Empty;

    public string LocalId { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string InputSchemaHash { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public string Effect { get; set; } = NhAssistantMcpToolEffects.Mutation;

    public string? DescriptionOverride { get; set; }

    public string Status { get; set; } = NhAssistantMcpToolStatuses.Available;

    public bool? ReadOnlyHint { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Personal assistant preferences of one actor.
/// </summary>
public sealed class AssistantUserPreference
{
    public string ActorId { get; set; } = string.Empty;

    public string Style { get; set; } = NhAssistantStyles.Default;

    public string AddressForm { get; set; } = NhAssistantAddressForms.Informal;

    public string ResponseLength { get; set; } = NhAssistantResponseLengths.Normal;

    public string? CustomInstructions { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
