using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// MCP servers, their tools and their agent assignments for the application's own code, with the
/// checks of the administration's <c>admin/mcp-servers</c> endpoints: the same validation and host
/// guard, secrets protected with ASP.NET Data Protection and never returned, logged or audited, and the
/// same content-free audit (<c>AdminMcpServerCreated</c>, <c>AdminMcpServerUpdated</c>,
/// <c>AdminMcpServerDeleted</c>, <c>AdminMcpServerSynced</c>, <c>AdminMcpToolUpdated</c>). Assigning a
/// server to an agent changes only the assignment, never the agent's definition or version, and is
/// audited as <c>AdminMcpServerAssignmentChanged</c>. The service does not authorize the caller: the
/// application calls it for an administrator or a deployment it trusts and names that actor.
/// </summary>
public interface INhAssistantMcpServers
{
    /// <summary>
    /// Every server, ordered by id, with the agents it is assigned to and, for OAuth, the organization's
    /// connection; never a secret.
    /// </summary>
    Task<IReadOnlyList<NhAssistantMcpServerDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new server, as <c>POST admin/mcp-servers</c> does: it starts at
    /// <c>headlessUse: never</c> unless the request says otherwise, without tools until a sync, and
    /// assigned to no agent. Fails with field errors (<c>assistant-validation</c>) as the endpoint,
    /// <c>assistant-mcp-host-blocked</c> for an address the host guard refuses,
    /// <c>assistant-mcp-server-exists</c> for a taken id, and with the discovery and secret codes of the
    /// endpoint. Audited as <c>AdminMcpServerCreated</c>.
    /// </summary>
    Task<TaskResult<NhAssistantMcpServerDto>> CreateAsync(
        NhAssistantMcpServerRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the server <see cref="NhAssistantMcpServerRequest.Id"/> names, as
    /// <c>PUT admin/mcp-servers/{id}</c> does: a <see langword="null"/> secret, reference, header use,
    /// transport or set of readers keeps the stored value, an empty string clears it. Fails as
    /// <see cref="CreateAsync"/> and with <c>assistant-mcp-server-not-found</c>. Audited as
    /// <c>AdminMcpServerUpdated</c>.
    /// </summary>
    Task<TaskResult<NhAssistantMcpServerDto>> UpdateAsync(
        NhAssistantMcpServerRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a server with its tools and its agent assignments; an OAuth server's tokens are revoked
    /// first. Fails with <c>assistant-mcp-server-not-found</c>. Audited as <c>AdminMcpServerDeleted</c>.
    /// </summary>
    Task<TaskResult> DeleteAsync(string serverId, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects and lists the remote tools without storing them. A server each person connects to is
    /// tested with the connection of <paramref name="actorId"/>; a server with an organization
    /// credential records the outcome as its connection state. A connection that fails is a result with
    /// <see cref="NhAssistantMcpServerTestResultDto.Ok"/> <see langword="false"/> and its code; an unknown
    /// server fails with <c>assistant-mcp-server-not-found</c>.
    /// </summary>
    Task<TaskResult<NhAssistantMcpServerTestResultDto>> TestAsync(
        string serverId,
        string? actorId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the remote tools and stores them: new tools start disabled as mutations, a changed input
    /// schema disables a tool until it is enabled again. A tool whose settings were reviewed before any
    /// sync saw it becomes <c>available</c> with those settings and the schema this sync sees, audited as
    /// <c>AdminMcpToolSeenAfterReview</c>; one this sync does not see keeps waiting. A failed connection
    /// fails with its code. Audited as <c>AdminMcpServerSynced</c>.
    /// </summary>
    Task<TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>> SyncAsync(
        string serverId,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The tools of a server, ordered by remote name: the synced ones and those whose reviewed settings
    /// wait for a sync (<see cref="NhAssistantMcpToolDto.AwaitingSync"/>, status <c>missing</c>). Fails
    /// with <c>assistant-mcp-server-not-found</c>.
    /// </summary>
    Task<TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>> ListToolsAsync(
        string serverId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables a tool, sets its effect (<c>read-only</c> or <c>mutation</c>) and its
    /// description override, as <c>PUT admin/mcp-servers/{id}/tools/{remoteName}</c> does; enabling a
    /// synced tool accepts its current remote schema. A <paramref name="remoteName"/> that no sync has
    /// seen yet, such as a tool of a server the host cannot reach yet, stores these reviewed settings as a
    /// tool with the status <c>missing</c>, no schema and <see cref="NhAssistantMcpToolDto.AwaitingSync"/>:
    /// no turn offers it until the first sync that sees it makes it <c>available</c> with them, after which
    /// a changed schema disables it as for any tool. Such a name has at most 128 characters
    /// (<c>remoteName: too-long</c>), no control characters and no other spelling of a stored tool's name
    /// (<c>remoteName: invalid</c>), and a server has at most
    /// <see cref="NhAssistantMcpServerLimits.MaxToolsAwaitingSync"/> of them
    /// (<c>assistant-configuration-limit</c>). Fails with field errors, with
    /// <c>assistant-mcp-server-not-found</c> and with <c>assistant-mcp-tool-disabled</c> for a tool the
    /// server no longer offers. Audited as <c>AdminMcpToolUpdated</c>, or <c>AdminMcpToolReviewedBeforeSync</c>
    /// for a tool no sync has seen that gets its row.
    /// </summary>
    Task<TaskResult<NhAssistantMcpToolDto>> UpdateToolAsync(
        string serverId,
        string remoteName,
        NhAssistantMcpToolSettingsRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns the server to an agent, in code or created in the administration, so the server's
    /// enabled tools are offered in the agent's turns. Only the assignment changes: the agent's
    /// definition, version and package links stay as they are. Assigning an assigned server changes
    /// nothing and is not audited. Fails with <c>assistant-mcp-server-not-found</c>,
    /// <c>assistant-agent-not-found</c> and the field error <c>mcpServerIds: too-long</c> for an agent
    /// with as many servers as allowed (<see cref="NhAssistantMcpServerLimits.MaxServersPerAgent"/>).
    /// Audited as <c>AdminMcpServerAssignmentChanged</c> with the result <c>assigned</c>.
    /// </summary>
    Task<TaskResult<NhAssistantMcpServerDto>> AssignAsync(
        string serverId,
        string agentId,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the server's assignment to an agent; the agent's definition and version stay as they
    /// are. Removing an assignment the agent does not have changes nothing and is not audited. Fails with
    /// <c>assistant-mcp-server-not-found</c> and <c>assistant-agent-not-found</c>. Audited as
    /// <c>AdminMcpServerAssignmentChanged</c> with the result <c>unassigned</c>.
    /// </summary>
    Task<TaskResult<NhAssistantMcpServerDto>> UnassignAsync(
        string serverId,
        string agentId,
        string actorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An MCP server as the application saves it, the input of the administration's server editor.
/// <see cref="Secret"/>, <see cref="SecretReference"/> and <see cref="ClientCertificateReference"/>:
/// <see langword="null"/> keeps the stored value, an empty string clears it, any other value replaces
/// it; a stored secret and a reference exclude each other. <see cref="HeadlessUse"/>,
/// <see cref="Transport"/>, <see cref="UserName"/> and <see cref="ConnectionScope"/>:
/// <see langword="null"/> keeps the stored value; a new server starts at <c>never</c>,
/// <c>streamable-http</c> and <c>organization</c>.
/// </summary>
/// <param name="Id">The server's lowercase dash-case id, at most 40 characters; it names the server's
/// tools (<c>mcp.&lt;id&gt;.&lt;tool&gt;</c>).</param>
/// <param name="DisplayName">At most 200 characters.</param>
/// <param name="Url">The absolute https address of the MCP endpoint, at most 2,048 characters; http only
/// to loopback where loopback is allowed.</param>
/// <param name="AuthMode">One of <see cref="NhAssistantMcpAuthModes"/> or a mode the application
/// registered with <c>AddMcpCredentialProvider</c>.</param>
/// <param name="HeaderName">For <c>api-key</c>: the header that carries the key, <c>X-Api-Key</c> when
/// <see langword="null"/>.</param>
/// <param name="Secret">The write-only secret of <c>bearer</c>, <c>api-key</c> and <c>basic</c>.</param>
/// <param name="RequiredPolicy">A policy every person whose rights a call uses must pass; the
/// application registers it.</param>
/// <param name="IsEnabled">Whether agents use the server.</param>
/// <param name="HeadlessUse">One of <see cref="NhAssistantMcpHeadlessUses"/>.</param>
public sealed record NhAssistantMcpServerRequest(
    string Id,
    string DisplayName,
    string Url,
    string AuthMode,
    string? HeaderName,
    string? Secret,
    string? RequiredPolicy,
    bool IsEnabled,
    string? HeadlessUse = null)
{
    /// <summary>The user name of <c>basic</c> authentication, at most 256 characters without <c>:</c>.</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// A reference to the secret instead of a stored one: <c>config:&lt;name&gt;</c> or a prefix the
    /// application registered with <c>AddMcpSecretResolver</c>.
    /// </summary>
    public string? SecretReference { get; init; }

    /// <summary>The name of a configured client certificate for mutual TLS.</summary>
    public string? ClientCertificateReference { get; init; }

    /// <summary>One of <see cref="NhAssistantMcpTransports"/>.</summary>
    public string? Transport { get; init; }

    /// <summary>For <c>oauth</c>: one of <see cref="NhAssistantMcpConnectionScopes"/>.</summary>
    public string? ConnectionScope { get; init; }

    /// <summary>
    /// The OAuth settings of <c>oauth</c> and <c>oauth-client-credentials</c>; <see langword="null"/>
    /// keeps them.
    /// </summary>
    public NhAssistantMcpOAuthRequest? OAuth { get; init; }

    /// <summary>
    /// The readers of the server, at most <see cref="NhAssistantMcpReaders.MaxReaders"/>, each of a tool
    /// synced from the server; <see langword="null"/> keeps them, an empty list removes them all. A field
    /// error of a reader names its index, such as <c>readers[0].toolName</c>.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpReaderRequest>? Readers { get; init; }
}

/// <summary>
/// The OAuth settings of a server. <see cref="ClientId"/>, <see cref="ClientSecret"/>,
/// <see cref="ClientSecretReference"/>, <see cref="Scopes"/> and <see cref="OrganizationAccountLabel"/>:
/// <see langword="null"/> keeps the stored value, an empty string clears it; the client secret is
/// write-only. <see cref="ConfirmAuthorization"/> runs discovery again and pins the authorization server
/// and resource it finds; a new OAuth server and a changed address need it.
/// </summary>
public sealed record NhAssistantMcpOAuthRequest(
    string? ClientId,
    string? ClientSecret,
    string? ClientSecretReference,
    string? Scopes,
    string? OrganizationAccountLabel,
    bool ConfirmAuthorization = false);

/// <summary>
/// One reader of a server: the media types it reads (one to <see cref="NhAssistantMcpReaders.MaxMediaTypes"/>),
/// the remote name of a synced tool that turns a file into text, the highest classification, one of
/// <see cref="NhAssistantDataClassificationNames"/>, that may be sent to it, and its priority among the
/// MCP readers, from <see cref="NhAssistantMcpReaders.MinPriority"/> to
/// <see cref="NhAssistantMcpReaders.MaxPriority"/>, higher first.
/// </summary>
public sealed record NhAssistantMcpReaderRequest(
    IReadOnlyList<string> MediaTypes,
    string? ToolName,
    string? MaxClassification,
    int Priority = 0);

/// <summary>
/// How a tool is offered: enabled or not, its effect (<see cref="NhAssistantMcpToolEffects"/>,
/// <c>read-only</c> or <c>mutation</c>, which requires approval) and a description of at most 1,000
/// characters that replaces the server's; <see langword="null"/> or blank uses the server's. For a tool
/// that no sync has seen yet these are its reviewed settings, which the first sync that sees it keeps.
/// </summary>
public sealed record NhAssistantMcpToolSettingsRequest(
    bool IsEnabled,
    string? Effect,
    string? DescriptionOverride = null);

/// <summary>
/// The limits of <see cref="INhAssistantMcpServers"/>.
/// </summary>
public static class NhAssistantMcpServerLimits
{
    /// <summary>
    /// The most MCP servers one agent is assigned to, as the agent editor's <c>mcpServerIds</c> allows.
    /// </summary>
    public const int MaxServersPerAgent = 32;

    /// <summary>
    /// The most tools of one server whose reviewed settings wait for a sync that sees them
    /// (<see cref="NhAssistantMcpToolDto.AwaitingSync"/>); another one is refused with
    /// <c>assistant-configuration-limit</c>.
    /// </summary>
    public const int MaxToolsAwaitingSync = 100;
}

/// <summary>
/// The result codes of <see cref="NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged"/>.
/// </summary>
internal static class NhAssistantMcpAssignmentAudit
{
    public const string Assigned = "assigned";

    public const string Unassigned = "unassigned";
}

/// <summary>
/// The result codes of <see cref="NhAssistantAuditEventKind.AdminMcpToolReviewedBeforeSync"/> and
/// <see cref="NhAssistantAuditEventKind.AdminMcpToolSeenAfterReview"/>: whether the tool is enabled.
/// </summary>
internal static class NhAssistantMcpToolAudit
{
    public const string Enabled = "enabled";

    public const string Disabled = "disabled";
}

/// <inheritdoc cref="INhAssistantMcpServers"/>
internal sealed class NhAssistantMcpServerService(
    NhAssistantMcpAdministration administration,
    NhAssistantAdminStore store,
    NhAssistantMcpOAuthStore oauthStore,
    NhAssistantAgentCatalog catalog,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks) : INhAssistantMcpServers
{
    private const int MaxActorIdLength = 256;

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public async Task<IReadOnlyList<NhAssistantMcpServerDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var servers = await administration.ListAsync(cancellationToken);
        var connections = await OrganizationConnectionsAsync(cancellationToken);
        return servers
            .Select(item => NhAssistantMcpServerDtos.ToDto(item.Server, item.AgentIds, connections.GetValueOrDefault(item.Server.Id)))
            .ToArray();
    }

    public async Task<TaskResult<NhAssistantMcpServerDto>> CreateAsync(
        NhAssistantMcpServerRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateActor(actorId);
        var created = await administration.CreateAsync(InputOf(request), actorId, cancellationToken);
        if (!created.Success)
        {
            return TaskResult<NhAssistantMcpServerDto>.Failed(created);
        }

        // A new server has no agents and no organization connection yet.
        return TaskResult<NhAssistantMcpServerDto>.Succeeded(NhAssistantMcpServerDtos.ToDto(created.Data!, [], null));
    }

    public async Task<TaskResult<NhAssistantMcpServerDto>> UpdateAsync(
        NhAssistantMcpServerRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateActor(actorId);
        var updated = await administration.UpdateAsync(InputOf(request), actorId, cancellationToken);
        if (!updated.Success)
        {
            return TaskResult<NhAssistantMcpServerDto>.Failed(updated);
        }

        var server = updated.Data!;
        var assigned = await store.ListMcpAssignmentsAsync(server.Id, cancellationToken);
        var connection = await administration.FindOrganizationConnectionAsync(server.Id, cancellationToken);
        return TaskResult<NhAssistantMcpServerDto>.Succeeded(NhAssistantMcpServerDtos.ToDto(server, assigned, connection));
    }

    public Task<TaskResult> DeleteAsync(string serverId, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ValidateActor(actorId);
        return administration.DeleteAsync(serverId, actorId, cancellationToken);
    }

    public async Task<TaskResult<NhAssistantMcpServerTestResultDto>> TestAsync(
        string serverId,
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (actorId is not null)
        {
            ValidateActor(actorId);
        }

        var tested = await administration.TestAsync(serverId, actorId, cancellationToken);
        if (!tested.Success)
        {
            return TaskResult<NhAssistantMcpServerTestResultDto>.Failed(tested);
        }

        var result = tested.Data!;
        return TaskResult<NhAssistantMcpServerTestResultDto>.Succeeded(new NhAssistantMcpServerTestResultDto(result.Ok, result.Code, result.ToolCount));
    }

    public async Task<TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>> SyncAsync(
        string serverId,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ValidateActor(actorId);
        var synced = await administration.SyncAsync(serverId, actorId, cancellationToken);
        return synced.Success
            ? TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>.Succeeded(ToDtos(synced.Data!))
            : TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>.Failed(synced);
    }

    public async Task<TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>> ListToolsAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var tools = await administration.ListToolsAsync(serverId, cancellationToken);
        return tools.Success
            ? TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>.Succeeded(ToDtos(tools.Data!))
            : TaskResult<IReadOnlyList<NhAssistantMcpToolDto>>.Failed(tools);
    }

    private static IReadOnlyList<NhAssistantMcpToolDto> ToDtos(IReadOnlyList<AssistantMcpTool> tools)
    {
        return tools.Select(NhAssistantMcpServerDtos.ToDto).ToArray();
    }

    public async Task<TaskResult<NhAssistantMcpToolDto>> UpdateToolAsync(
        string serverId,
        string remoteName,
        NhAssistantMcpToolSettingsRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
        ArgumentNullException.ThrowIfNull(request);
        ValidateActor(actorId);
        var updated = await administration.UpdateToolAsync(
            serverId,
            remoteName,
            request.IsEnabled,
            request.Effect,
            request.DescriptionOverride,
            actorId,
            cancellationToken);
        return updated.Success
            ? TaskResult<NhAssistantMcpToolDto>.Succeeded(NhAssistantMcpServerDtos.ToDto(updated.Data!))
            : TaskResult<NhAssistantMcpToolDto>.Failed(updated);
    }

    public Task<TaskResult<NhAssistantMcpServerDto>> AssignAsync(
        string serverId,
        string agentId,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        return SetAssignmentAsync(serverId, agentId, assign: true, actorId, cancellationToken);
    }

    public Task<TaskResult<NhAssistantMcpServerDto>> UnassignAsync(
        string serverId,
        string agentId,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        return SetAssignmentAsync(serverId, agentId, assign: false, actorId, cancellationToken);
    }

    /// <summary>
    /// Writes the assignment row only, so the agent's version stays and neither an editor that holds
    /// it nor a package import conflicts. MCP assignments are no part of an agent package: no lock
    /// holds them, and an import keeps them.
    /// </summary>
    private async Task<TaskResult<NhAssistantMcpServerDto>> SetAssignmentAsync(
        string serverId,
        string agentId,
        bool assign,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ValidateActor(actorId);
        if (await store.FindMcpServerAsync(serverId, cancellationToken) is null)
        {
            return ServerNotFound();
        }
        // The catalog stores the agents in code before their first read.
        if (await catalog.FindAsync(agentId, includeDisabled: true, cancellationToken) is null)
        {
            return AgentNotFound();
        }

        var changed = await store.SetMcpAssignmentAsync(agentId, serverId, assign, NhAssistantMcpServerLimits.MaxServersPerAgent, cancellationToken);
        if (changed.Outcome == NhAssistantStoreOutcome.Rejected)
        {
            return TaskResult<NhAssistantMcpServerDto>.Failed(NhAssistantValidationErrors.Failed("mcpServerIds", NhAssistantFieldErrors.TooLong));
        }
        if (changed.Outcome == NhAssistantStoreOutcome.NotFound)
        {
            // The server or the agent was deleted meanwhile.
            return await store.FindMcpServerAsync(serverId, cancellationToken) is null ? ServerNotFound() : AgentNotFound();
        }

        if (changed.Value)
        {
            await AuditAssignmentAsync(serverId, agentId, assign, actorId, cancellationToken);
        }

        var server = (await administration.ListAsync(cancellationToken)).FirstOrDefault(item => item.Server.Id == serverId);
        if (server.Server is null)
        {
            return ServerNotFound();
        }

        var connection = await administration.FindOrganizationConnectionAsync(serverId, cancellationToken);
        return TaskResult<NhAssistantMcpServerDto>.Succeeded(NhAssistantMcpServerDtos.ToDto(server.Server, server.AgentIds, connection));
    }

    private async Task AuditAssignmentAsync(string serverId, string agentId, bool assigned, string actorId, CancellationToken cancellationToken)
    {
        var evt = new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.AdminMcpServerAssignmentChanged,
            Guid.Empty,
            Guid.Empty,
            actorId,
            agentId,
            0,
            DateTimeOffset.UtcNow)
        {
            ObjectId = serverId,
            ResultCode = assigned ? NhAssistantMcpAssignmentAudit.Assigned : NhAssistantMcpAssignmentAudit.Unassigned
        };
        foreach (var sink in _auditSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }

    private async Task<IReadOnlyDictionary<string, AssistantMcpConnection>> OrganizationConnectionsAsync(CancellationToken cancellationToken)
    {
        return (await oauthStore.ListTokenConnectionsAsync(NhAssistantMcpConnectionOwners.Organization, null, cancellationToken))
            .ToDictionary(connection => connection.ServerId, StringComparer.Ordinal);
    }

    /// <summary>
    /// The administration's input of a request: the same fields, the same keep and clear rules.
    /// </summary>
    private static NhAssistantMcpServerInput InputOf(NhAssistantMcpServerRequest request)
    {
        return new NhAssistantMcpServerInput(
            request.Id,
            request.DisplayName,
            request.Url,
            request.AuthMode,
            string.IsNullOrWhiteSpace(request.HeaderName) ? null : request.HeaderName,
            request.Secret,
            string.IsNullOrWhiteSpace(request.RequiredPolicy) ? null : request.RequiredPolicy,
            request.IsEnabled,
            request.HeadlessUse)
        {
            UserName = request.UserName,
            SecretReference = request.SecretReference,
            ClientCertificateReference = request.ClientCertificateReference,
            Transport = request.Transport,
            ConnectionScope = request.ConnectionScope,
            OAuth = request.OAuth is { } oauth
                ? new NhAssistantMcpOAuthInput(
                    oauth.ClientId,
                    oauth.ClientSecret,
                    oauth.ClientSecretReference,
                    oauth.Scopes,
                    oauth.OrganizationAccountLabel,
                    oauth.ConfirmAuthorization)
                : null,
            // A reader without media types is refused field by field, as the endpoint refuses one.
            Readers = request.Readers?
                .Select(reader => new NhAssistantMcpReaderInput(
                    reader?.MediaTypes ?? [],
                    reader?.ToolName,
                    reader?.MaxClassification,
                    reader?.Priority ?? 0))
                .ToArray()
        };
    }

    private static TaskResult<NhAssistantMcpServerDto> ServerNotFound()
    {
        return TaskResult<NhAssistantMcpServerDto>.Failed(NhAssistantAdminErrorCodes.McpServerNotFound, "The MCP server was not found.");
    }

    private static TaskResult<NhAssistantMcpServerDto> AgentNotFound()
    {
        return TaskResult<NhAssistantMcpServerDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
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

/// <summary>
/// The DTOs of MCP servers and tools, as the administration's endpoints and
/// <see cref="INhAssistantMcpServers"/> return them: never a secret, only whether one is stored.
/// </summary>
internal static class NhAssistantMcpServerDtos
{
    public static NhAssistantMcpServerDto ToDto(AssistantMcpServer server, IReadOnlyList<string> agentIds, AssistantMcpConnection? organizationConnection)
    {
        return new NhAssistantMcpServerDto(
            server.Id,
            server.DisplayName,
            server.Url,
            server.AuthMode,
            server.HeaderName,
            server.ProtectedSecret is not null,
            server.RequiredPolicy,
            server.IsEnabled,
            server.LastSyncAt,
            server.LastSyncStatus,
            agentIds)
        {
            HeadlessUse = server.HeadlessUse,
            LastConnectAt = server.LastConnectAt,
            LastConnectCode = server.LastConnectCode,
            UserName = server.UserName,
            SecretReference = server.SecretReference,
            ClientCertificateReference = server.ClientCertificateReference,
            Transport = server.Transport,
            ConnectionScope = server.ConnectionScope,
            OAuth = NhAssistantMcpAuthModes.IsOAuth(server.AuthMode)
                ? new NhAssistantMcpOAuthDto(
                    server.OAuthClientId,
                    server.ProtectedOAuthClientSecret is not null,
                    server.OAuthClientSecretReference,
                    server.OAuthScopes,
                    server.OrganizationAccountLabel)
                : null,
            Authorization = server.AuthorizationServerIssuer is { } issuer && server.ResourceUri is { } resource && server.AuthorizationConfirmedAt is { } confirmedAt
                ? new NhAssistantMcpAuthorizationDto(issuer, resource, confirmedAt, server.AuthorizationConfirmedBy)
                : null,
            OrganizationConnection = NhAssistantMcpAuthModes.IsOrganizationCredential(server.AuthMode, server.ConnectionScope) && NhAssistantMcpAuthModes.IsOAuth(server.AuthMode)
                ? ToDto(organizationConnection)
                : null,
            Readers = server.Readers
                .OrderByDescending(reader => reader.Priority)
                .ThenBy(reader => reader.ToolName, StringComparer.Ordinal)
                .Select(reader => new NhAssistantMcpReaderDto(
                    NhAssistantMcpReaders.Split(reader.MediaTypes),
                    reader.ToolName,
                    reader.MaxClassification,
                    reader.Priority))
                .ToArray()
        };
    }

    public static NhAssistantMcpOrganizationConnectionDto ToDto(AssistantMcpConnection? connection)
    {
        return connection is null
            ? new NhAssistantMcpOrganizationConnectionDto("not-connected", null, null, null, null, null, null)
            : new NhAssistantMcpOrganizationConnectionDto(
                connection.Status ?? "not-connected",
                connection.ConnectedAt,
                connection.ConnectedBy,
                connection.GrantedScopes,
                connection.RequiredScopes,
                connection.AccessTokenExpiresAt,
                connection.LastFailureCode);
    }

    public static NhAssistantMcpToolDto ToDto(AssistantMcpTool tool)
    {
        return new NhAssistantMcpToolDto(
            tool.RemoteName,
            tool.LocalId,
            tool.Description,
            tool.DescriptionOverride,
            tool.IsEnabled,
            tool.Effect,
            tool.Status,
            tool.ReadOnlyHint)
        {
            AwaitingSync = NhAssistantAdminStore.AwaitsSync(tool)
        };
    }
}
