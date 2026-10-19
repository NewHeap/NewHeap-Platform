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

    /// <summary>
    /// <see langword="false"/> only while the agent is stopped (see <see cref="RunState"/>); kept for
    /// the reads that list and find enabled agents.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// One of <see cref="NhAssistantAgentRunStates.Active"/>, <see cref="NhAssistantAgentRunStates.Paused"/>
    /// or <see cref="NhAssistantAgentRunStates.Stopped"/>. Changing it never changes <see cref="Version"/>.
    /// An agent with <see cref="IsEnabled"/> <see langword="false"/> reads as stopped, which keeps the
    /// kill switches engaged before this column existed.
    /// </summary>
    public string RunState { get; set; } = NhAssistantAgentRunStates.Active;

    public DateTimeOffset? StateChangedAt { get; set; }

    public string? StateChangedBy { get; set; }

    /// <summary>
    /// Why the agent was paused, stopped or resumed, as the person who did it wrote it.
    /// </summary>
    public string? StateReason { get; set; }

    /// <summary>
    /// The shadow mode an administrator set at runtime; <see langword="null"/> follows the code.
    /// </summary>
    public bool? ShadowOverride { get; set; }

    public DateTimeOffset? ShadowChangedAt { get; set; }

    public string? ShadowChangedBy { get; set; }

    public string? ShadowReason { get; set; }

    /// <summary>
    /// The technical account an administrator set at runtime for an agent that answers to one;
    /// <see langword="null"/> uses the account in code.
    /// </summary>
    public string? ServiceAccountActorId { get; set; }

    public DateTimeOffset? ServiceAccountChangedAt { get; set; }

    public string? ServiceAccountChangedBy { get; set; }

    public string? ServiceAccountReason { get; set; }

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

    /// <summary>
    /// A reference to the secret instead of a stored one (<see cref="NhAssistantMcpSecretReferences"/>).
    /// </summary>
    public string? SecretReference { get; set; }

    /// <summary>
    /// The user name of <c>basic</c> authentication; the password is the server's secret.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// The name of a client certificate under <c>NewHeap:AI:Assistant:Mcp:ClientCertificates</c>,
    /// sent for mutual TLS.
    /// </summary>
    public string? ClientCertificateReference { get; set; }

    /// <summary>
    /// One of <see cref="NhAssistantMcpTransports"/>.
    /// </summary>
    public string Transport { get; set; } = NhAssistantMcpTransports.StreamableHttp;

    public string? RequiredPolicy { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether the tools are offered in headless turns: one of <see cref="NhAssistantMcpHeadlessUses"/>.
    /// </summary>
    public string HeadlessUse { get; set; } = NhAssistantMcpHeadlessUses.Never;

    public DateTimeOffset? LastSyncAt { get; set; }

    public string? LastSyncStatus { get; set; }

    /// <summary>
    /// When an administrator's test or sync, or a turn with the shared credential, last connected.
    /// </summary>
    public DateTimeOffset? LastConnectAt { get; set; }

    /// <summary>
    /// The failure code of that connection; <see langword="null"/> when it succeeded.
    /// </summary>
    public string? LastConnectCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AssistantMcpTool> Tools { get; set; } = [];
}

/// <summary>
/// A person's or the organization's relation to an MCP server. In this version it records a person's
/// consent to background use: <see cref="AllowBackgroundUse"/> applies only while
/// <see cref="ConsentHash"/> matches the server as it is configured now. One row per server, owner and
/// tenant key; <see cref="OwnerActorId"/> and <see cref="TenantKey"/> are empty instead of null so the
/// unique index behaves the same on every provider.
/// </summary>
public sealed class AssistantMcpConnection
{
    public Guid Id { get; set; }

    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="NhAssistantMcpConnectionOwners"/>.
    /// </summary>
    public string OwnerKind { get; set; } = NhAssistantMcpConnectionOwners.User;

    public string OwnerActorId { get; set; } = string.Empty;

    public string TenantKey { get; set; } = string.Empty;

    public bool AllowBackgroundUse { get; set; }

    /// <summary>
    /// The hash of the server's id, URL and authentication mode when consent was given.
    /// </summary>
    public string? ConsentHash { get; set; }

    public DateTimeOffset? ConsentedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Concurrency token, incremented with every change.
    /// </summary>
    public int Version { get; set; }
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

/// <summary>
/// The run state of one skill of an agent, next to the agent's own state. A skill without a row is
/// active.
/// </summary>
public sealed class AssistantAgentSkillState
{
    public string AgentId { get; set; } = string.Empty;

    public string SkillId { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="NhAssistantAgentRunStates.Active"/>, <see cref="NhAssistantAgentRunStates.Paused"/>
    /// or <see cref="NhAssistantAgentRunStates.Stopped"/>.
    /// </summary>
    public string RunState { get; set; } = NhAssistantAgentRunStates.Active;

    public DateTimeOffset? StateChangedAt { get; set; }

    public string? StateChangedBy { get; set; }

    public string? StateReason { get; set; }

    /// <summary>
    /// The shadow mode an administrator set at runtime; <see langword="null"/> follows the code.
    /// </summary>
    public bool? ShadowOverride { get; set; }

    public DateTimeOffset? ShadowChangedAt { get; set; }

    public string? ShadowChangedBy { get; set; }

    public string? ShadowReason { get; set; }
}
