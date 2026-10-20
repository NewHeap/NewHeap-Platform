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
    /// <see langword="true"/> when an administrator kept an agent created in the administration out of
    /// the chat's agent picker. Agents in code take it from <c>HideFromChat()</c> instead.
    /// </summary>
    public bool HiddenFromChat { get; set; }

    /// <summary>
    /// The object types an agent created in the administration works on, as a JSON array of type
    /// ids; empty when it works on all object types or only on the application. Agents in code take
    /// what they work on from <c>WorksOn</c> instead.
    /// </summary>
    public string WorksOnSubjectTypesJson { get; set; } = "[]";

    /// <summary>
    /// The agent works on every object type. Rows that existed before agents said what they work on
    /// read <see langword="true"/>.
    /// </summary>
    public bool WorksOnAllSubjects { get; set; } = true;

    /// <summary>
    /// The agent works on the whole application, the platform's <c>nh-system</c> subject.
    /// </summary>
    public bool WorksOnApplication { get; set; }

    /// <summary>
    /// The agent works per division instead of app-wide.
    /// </summary>
    public bool PerDivision { get; set; }

    /// <summary>
    /// The authorization policy a person needs to run the agent for the whole application;
    /// <see langword="null"/> uses the assistant's administration policy.
    /// </summary>
    public string? RunPolicy { get; set; }

    /// <summary>
    /// The authorization policy a person needs to see the agent's results for the whole application;
    /// <see langword="null"/> uses the agent's required policy.
    /// </summary>
    public string? ViewPolicy { get; set; }

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

    /// <summary>
    /// For <c>oauth</c>: one of <see cref="NhAssistantMcpConnectionScopes"/>. Other modes are
    /// <see cref="NhAssistantMcpConnectionScopes.Organization"/>.
    /// </summary>
    public string ConnectionScope { get; set; } = NhAssistantMcpConnectionScopes.Organization;

    /// <summary>
    /// The confirmed protected resource (RFC 9728) of an OAuth server: its canonical URI.
    /// </summary>
    public string? ResourceUri { get; set; }

    /// <summary>
    /// The confirmed authorization server of an OAuth server; tokens of another issuer are refused.
    /// </summary>
    public string? AuthorizationServerIssuer { get; set; }

    /// <summary>
    /// The scopes an administrator allows, separated by spaces. Empty lets the server's challenge and
    /// metadata decide.
    /// </summary>
    public string? OAuthScopes { get; set; }

    /// <summary>A pre-registered client id; empty uses a Client ID Metadata Document or registration.</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>The pre-registered client's secret, protected with Data Protection.</summary>
    public string? ProtectedOAuthClientSecret { get; set; }

    /// <summary>A reference to the client secret instead of storing it.</summary>
    public string? OAuthClientSecretReference { get; set; }

    /// <summary>
    /// For an organization connection made by signing in: the remote account everyone using the
    /// server acts as.
    /// </summary>
    public string? OrganizationAccountLabel { get; set; }

    /// <summary>When an administrator confirmed the authorization server and resource.</summary>
    public DateTimeOffset? AuthorizationConfirmedAt { get; set; }

    /// <summary>The administrator who confirmed them.</summary>
    public string? AuthorizationConfirmedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AssistantMcpTool> Tools { get; set; } = [];
}

/// <summary>
/// A person's or the organization's relation to an MCP server: a person's consent to background use,
/// which applies only while <see cref="ConsentHash"/> matches the server as it is configured now, and
/// the OAuth tokens of a connection, protected with Data Protection. One row per server, owner and
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

    /// <summary>
    /// For a row with OAuth tokens: one of <see cref="NhAssistantMcpConnectionStatuses"/>; null for a
    /// row that only records consent.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>The authorization server that issued the tokens.</summary>
    public string? Issuer { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The resource the tokens are for.</summary>
    public string? Resource { get; set; }

    /// <summary>The granted scopes, separated by spaces.</summary>
    public string? GrantedScopes { get; set; }

    /// <summary>Scopes a server asked for that the connection lacks, for the next authorization.</summary>
    public string? RequiredScopes { get; set; }

    public string? ProtectedAccessToken { get; set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    public string? ProtectedRefreshToken { get; set; }

    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }

    /// <summary>Who completed the authorization: the person, or the administrator for the organization.</summary>
    public string? ConnectedBy { get; set; }

    public DateTimeOffset? ConnectedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? LastRefreshedAt { get; set; }

    public string? LastFailureCode { get; set; }

    /// <summary>Until when one instance holds the right to renew the tokens.</summary>
    public DateTimeOffset? RefreshLeaseUntil { get; set; }

    public string? RefreshLeaseOwner { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Concurrency token, incremented with every change.
    /// </summary>
    public int Version { get; set; }
}

/// <summary>
/// One authorization of an OAuth connection, from its start to its completion: single use, valid for
/// <c>FlowLifetime</c>, found by the hash of its <c>state</c> and completed only by its starter with
/// the one-time completion code that the callback page handed to the starter's browser.
/// </summary>
public sealed class AssistantMcpAuthorizationFlow
{
    public Guid Id { get; set; }

    public string ServerId { get; set; } = string.Empty;

    public string OwnerKind { get; set; } = NhAssistantMcpConnectionOwners.User;

    public string OwnerActorId { get; set; } = string.Empty;

    public string TenantKey { get; set; } = string.Empty;

    /// <summary>The person who started the flow and alone may complete it.</summary>
    public string StartedBy { get; set; } = string.Empty;

    /// <summary>The SHA-256 of <c>state</c>, lowercase hex.</summary>
    public string StateHash { get; set; } = string.Empty;

    public string ProtectedCodeVerifier { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Resource { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    public string? RequestedScopes { get; set; }

    public bool AllowBackgroundUse { get; set; }

    /// <summary>Where a redirect-mode flow returns to; null for a popup.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>The SHA-256 of the one-time completion code, lowercase hex.</summary>
    public string? CompletionCodeHash { get; set; }

    /// <summary>The token response between the callback and the completion, protected.</summary>
    public string? ProtectedTokenResponse { get; set; }

    /// <summary><c>pending</c>, <c>exchanged</c>, <c>completed</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";

    public string? FailureCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// The dynamic client registration of the host at one authorization server for one MCP server.
/// The issuer is part of the key through its hash, so a registration never serves another issuer.
/// </summary>
public sealed class AssistantMcpClientRegistration
{
    public string ServerId { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the issuer, lowercase hex.</summary>
    public string IssuerHash { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    /// <summary><c>dynamic</c>.</summary>
    public string Method { get; set; } = "dynamic";

    public string ClientId { get; set; } = string.Empty;

    public string? ProtectedClientSecret { get; set; }

    public string? ProtectedRegistrationAccessToken { get; set; }

    /// <summary>The token endpoint authentication method the registration agreed on.</summary>
    public string TokenEndpointAuthMethod { get; set; } = "none";

    public DateTimeOffset? ClientSecretExpiresAt { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }
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
