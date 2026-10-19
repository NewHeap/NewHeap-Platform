using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Administrator input for an MCP server. <see cref="Secret"/>, <see cref="SecretReference"/> and
/// <see cref="ClientCertificateReference"/>: null keeps the stored value, an empty string clears it,
/// any other value replaces it; a stored secret and a reference exclude each other.
/// <see cref="HeadlessUse"/>, <see cref="Transport"/> and <see cref="UserName"/>: null keeps the stored
/// value; a new server starts at <see cref="NhAssistantMcpHeadlessUses.Never"/> and
/// <see cref="NhAssistantMcpTransports.StreamableHttp"/>.
/// </summary>
internal sealed record NhAssistantMcpServerInput(
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
    public string? UserName { get; init; }

    public string? SecretReference { get; init; }

    public string? ClientCertificateReference { get; init; }

    public string? Transport { get; init; }

    /// <summary>For <c>oauth</c>: one of <see cref="NhAssistantMcpConnectionScopes"/>; null keeps the stored value.</summary>
    public string? ConnectionScope { get; init; }

    /// <summary>The OAuth settings of <c>oauth</c> and <c>oauth-client-credentials</c>; null keeps them.</summary>
    public NhAssistantMcpOAuthInput? OAuth { get; init; }
}

/// <summary>
/// OAuth settings of a server. <see cref="ClientId"/>, <see cref="ClientSecret"/>,
/// <see cref="ClientSecretReference"/>, <see cref="Scopes"/> and <see cref="OrganizationAccountLabel"/>:
/// null keeps the stored value, an empty string clears it. <see cref="ConfirmAuthorization"/> runs
/// discovery again and pins the authorization server and resource it finds.
/// </summary>
internal sealed record NhAssistantMcpOAuthInput(
    string? ClientId,
    string? ClientSecret,
    string? ClientSecretReference,
    string? Scopes,
    string? OrganizationAccountLabel,
    bool ConfirmAuthorization);

internal sealed record NhAssistantMcpTestResult(bool Ok, string? Code, int? ToolCount);

/// <summary>
/// Manages MCP servers and their tools. Secrets are protected at rest and never returned, logged
/// or audited; every change is reported as a content-free audit event.
/// </summary>
internal sealed partial class NhAssistantMcpAdministration(
    NhAssistantAdminStore store,
    NhAssistantMcpSecretProtector secrets,
    NhAssistantMcpConnectionPlanner planner,
    NhAssistantMcpConnectionCache cache,
    NhAssistantMcpHostGuard guard,
    NhAssistantMcpSecretSource secretSource,
    NhAssistantMcpClientCertificates certificates,
    IAuthorizationPolicyProvider policies,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks,
    NhAssistantMcpDiscovery discovery,
    IOptionsMonitor<NhAssistantOptions> options,
    IServiceProvider services)
{
    public const string AdministratorReason = "administrator";
    public const string ServerChangedReason = "server-changed";

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public Task<IReadOnlyList<(AssistantMcpServer Server, IReadOnlyList<string> AgentIds)>> ListAsync(
        CancellationToken cancellationToken)
    {
        return store.ListMcpServersAsync(cancellationToken);
    }

    public async Task<TaskResult<AssistantMcpServer>> CreateAsync(
        NhAssistantMcpServerInput input,
        string actorId,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(input, existing: null, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<AssistantMcpServer>.Failed(validation);
        }
        var confirmation = await ConfirmAsync(input, existing: null, cancellationToken);
        if (!confirmation.Success)
        {
            return TaskResult<AssistantMcpServer>.Failed(confirmation);
        }
        var now = DateTimeOffset.UtcNow;
        var server = new AssistantMcpServer
        {
            Id = input.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        Apply(server, input);
        ApplyOAuth(server, input, confirmation.Data, actorId, now);
        if (!await store.AddMcpServerAsync(server, cancellationToken))
        {
            return TaskResult<AssistantMcpServer>.Failed(
                NhAssistantAdminErrorCodes.McpServerExists,
                "An MCP server with this id already exists.");
        }
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerCreated, server.Id, actorId, cancellationToken);
        if (confirmation.Data is not null)
        {
            await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerAuthorizationConfirmed, server.Id, actorId, cancellationToken);
        }
        return TaskResult<AssistantMcpServer>.Succeeded(server);
    }

    public async Task<TaskResult<AssistantMcpServer>> UpdateAsync(
        NhAssistantMcpServerInput input,
        string actorId,
        CancellationToken cancellationToken)
    {
        var existing = await store.FindMcpServerAsync(input.Id, cancellationToken);
        if (existing is null)
        {
            return NotFound<AssistantMcpServer>();
        }
        var validation = await ValidateAsync(input, existing, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<AssistantMcpServer>.Failed(validation);
        }
        var confirmation = await ConfirmAsync(input, existing, cancellationToken);
        if (!confirmation.Success)
        {
            return TaskResult<AssistantMcpServer>.Failed(confirmation);
        }
        var now = DateTimeOffset.UtcNow;
        var updated = await store.UpdateMcpServerAsync(input.Id, server =>
        {
            Apply(server, input);
            ApplyOAuth(server, input, confirmation.Data, actorId, now);
        }, cancellationToken);
        await cache.InvalidateAsync(input.Id);
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerUpdated, input.Id, actorId, cancellationToken);
        if (confirmation.Data is not null)
        {
            await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerAuthorizationConfirmed, input.Id, actorId, cancellationToken);
        }
        if (NhAssistantMcpAuthModes.IsOAuth(existing.AuthMode) && OAuthChanged(existing, updated!))
        {
            // D18: tokens of another address, issuer, scope set or client are not used again.
            await services.GetRequiredService<NhAssistantMcpOAuthConnections>()
                .DisconnectAsync(existing, null, null, ServerChangedReason, actorId, cancellationToken);
        }
        else if (confirmation.Data is not null)
        {
            // Confirming the same authorization server again lifts a suspension after a challenge.
            await services.GetRequiredService<NewHeap.Platform.AI.Chat.Persistence.NhAssistantMcpOAuthStore>()
                .SetServerConnectionStatusAsync(input.Id, NhAssistantMcpConnectionStatuses.Active, NhAssistantMcpConnectionStatuses.IssuerChanged, now, cancellationToken);
        }
        return TaskResult<AssistantMcpServer>.Succeeded(updated!);
    }

    public async Task<TaskResult> DeleteAsync(string id, string actorId, CancellationToken cancellationToken)
    {
        if (await store.FindMcpServerAsync(id, cancellationToken) is { } existing && NhAssistantMcpAuthModes.IsOAuth(existing.AuthMode))
        {
            // Revoke at the authorization server before the rows go with the server.
            await services.GetRequiredService<NhAssistantMcpOAuthConnections>()
                .DisconnectAsync(existing, null, null, AdministratorReason, actorId, cancellationToken);
        }
        if (!await store.DeleteMcpServerAsync(id, cancellationToken))
        {
            return TaskResult.Failed(NhAssistantAdminErrorCodes.McpServerNotFound, "The MCP server was not found.");
        }
        await cache.InvalidateAsync(id);
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerDeleted, id, actorId, cancellationToken);
        return TaskResult.Succeeded();
    }

    public async Task<TaskResult<NhAssistantMcpTestResult>> TestAsync(string id, CancellationToken cancellationToken)
    {
        var server = await store.FindMcpServerAsync(id, cancellationToken);
        if (server is null)
        {
            return NotFound<NhAssistantMcpTestResult>();
        }
        var lease = await ConnectAsync(server, cancellationToken);
        if (!lease.Success)
        {
            var code = FirstCode(lease);
            await RecordConnectAsync(server.Id, code ?? NhAssistantAdminErrorCodes.McpUnreachable);
            return TaskResult<NhAssistantMcpTestResult>.Succeeded(new NhAssistantMcpTestResult(false, code, null));
        }
        await using (lease.Data)
        {
            await RecordConnectAsync(server.Id, null);
            return TaskResult<NhAssistantMcpTestResult>.Succeeded(new NhAssistantMcpTestResult(true, null, lease.Data.Tools.Count));
        }
    }

    /// <summary>
    /// Lists the remote tools and records them. New tools start disabled as mutations; a changed input
    /// schema disables a tool until an administrator activates it again.
    /// </summary>
    public async Task<TaskResult<IReadOnlyList<AssistantMcpTool>>> SyncAsync(
        string id,
        string actorId,
        CancellationToken cancellationToken)
    {
        var server = await store.FindMcpServerAsync(id, cancellationToken);
        if (server is null)
        {
            return NotFound<IReadOnlyList<AssistantMcpTool>>();
        }
        var lease = await ConnectAsync(server, cancellationToken);
        if (!lease.Success)
        {
            await store.UpdateMcpServerAsync(id, item =>
            {
                item.LastSyncAt = DateTimeOffset.UtcNow;
                item.LastSyncStatus = "failed";
                item.LastConnectAt = item.LastSyncAt;
                item.LastConnectCode = FirstCode(lease) ?? NhAssistantAdminErrorCodes.McpUnreachable;
            }, cancellationToken);
            return TaskResult<IReadOnlyList<AssistantMcpTool>>.Failed(lease);
        }

        IReadOnlyList<AssistantMcpTool> tools;
        await using (lease.Data)
        {
            var discovered = lease.Data.Tools
                .GroupBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .Where(tool => tool.Name.Length <= 128)
                .Select(tool => new AssistantMcpTool
                {
                    RemoteName = tool.Name,
                    LocalId = NhAssistantMcpToolSource.LocalId(id, tool.Name),
                    Description = Bound(tool.Description ?? string.Empty, 1_000),
                    InputSchemaHash = NhAssistantMcpToolSource.SchemaHash(tool),
                    ReadOnlyHint = tool.ProtocolTool.Annotations?.ReadOnlyHint
                })
                .ToArray();
            tools = await store.ApplySyncAsync(id, discovered, cancellationToken);
        }
        await store.UpdateMcpServerAsync(id, item =>
        {
            item.LastSyncAt = DateTimeOffset.UtcNow;
            item.LastSyncStatus = "ok";
            item.LastConnectAt = item.LastSyncAt;
            item.LastConnectCode = null;
        }, cancellationToken);
        await cache.InvalidateAsync(id);
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerSynced, id, actorId, cancellationToken);
        return TaskResult<IReadOnlyList<AssistantMcpTool>>.Succeeded(tools);
    }

    public async Task<TaskResult<IReadOnlyList<AssistantMcpTool>>> ListToolsAsync(string id, CancellationToken cancellationToken)
    {
        if (await store.FindMcpServerAsync(id, cancellationToken) is null)
        {
            return NotFound<IReadOnlyList<AssistantMcpTool>>();
        }
        return TaskResult<IReadOnlyList<AssistantMcpTool>>.Succeeded(await store.ListMcpToolsAsync(id, cancellationToken));
    }

    public async Task<TaskResult<AssistantMcpTool>> UpdateToolAsync(
        string serverId,
        string remoteName,
        bool isEnabled,
        string? effect,
        string? descriptionOverride,
        string actorId,
        CancellationToken cancellationToken)
    {
        var errors = new NhAssistantValidationErrors()
            .Require(effect is NhAssistantMcpToolEffects.ReadOnly or NhAssistantMcpToolEffects.Mutation, "effect", NhAssistantFieldErrors.Invalid)
            .Require(descriptionOverride is not { Length: > 1_000 }, "descriptionOverride", NhAssistantFieldErrors.TooLong);
        if (!errors.IsEmpty)
        {
            return TaskResult<AssistantMcpTool>.Failed(errors.ToResult());
        }
        var tools = await store.ListMcpToolsAsync(serverId, cancellationToken);
        var current = tools.FirstOrDefault(tool => tool.RemoteName == remoteName);
        if (current is null)
        {
            return TaskResult<AssistantMcpTool>.Failed(NhAssistantAdminErrorCodes.McpToolNotFound, "The MCP tool was not found.");
        }
        if (isEnabled && current.Status == NhAssistantMcpToolStatuses.Missing)
        {
            return TaskResult<AssistantMcpTool>.Failed(
                NhAssistantAdminErrorCodes.McpToolDisabled,
                "A tool that the server no longer offers cannot be enabled.");
        }
        var updated = await store.UpdateMcpToolAsync(serverId, remoteName, tool =>
        {
            tool.IsEnabled = isEnabled;
            tool.Effect = effect;
            tool.DescriptionOverride = string.IsNullOrWhiteSpace(descriptionOverride) ? null : descriptionOverride.Trim();
            if (isEnabled)
            {
                // Explicit re-activation accepts the current remote schema.
                tool.Status = NhAssistantMcpToolStatuses.Available;
            }
        }, cancellationToken);
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpToolUpdated, serverId + "/" + remoteName, actorId, cancellationToken);
        return TaskResult<AssistantMcpTool>.Succeeded(updated!);
    }

    private async Task<TaskResult<NhAssistantMcpLease>> ConnectAsync(AssistantMcpServer server, CancellationToken cancellationToken)
    {
        var plan = await planner.PlanAsync(server, cancellationToken);
        if (!plan.Success)
        {
            return TaskResult<NhAssistantMcpLease>.Failed(plan);
        }
        var perCall = plan.Data with { IsPerCaller = true };
        return await cache.ConnectAsync(server, perCall, bypassCache: true, cancellationToken);
    }

    private async Task RecordConnectAsync(string serverId, string? code)
    {
        await store.RecordMcpConnectAsync(serverId, DateTimeOffset.UtcNow, code, CancellationToken.None);
    }

    private async Task<TaskResult> ValidateAsync(
        NhAssistantMcpServerInput input,
        AssistantMcpServer? existing,
        CancellationToken cancellationToken)
    {
        var headlessUse = input.HeadlessUse ?? existing?.HeadlessUse ?? NhAssistantMcpHeadlessUses.Never;
        var transport = input.Transport ?? existing?.Transport ?? NhAssistantMcpTransports.StreamableHttp;
        var userName = input.UserName ?? existing?.UserName;
        var reference = Effective(input.SecretReference, existing?.SecretReference);
        var certificate = Effective(input.ClientCertificateReference, existing?.ClientCertificateReference);
        var connectionScope = input.ConnectionScope ?? existing?.ConnectionScope ?? NhAssistantMcpConnectionScopes.Organization;
        var oauth = input.OAuth;
        var oauthScopes = Effective(oauth?.Scopes, existing?.OAuthScopes);
        var oauthClientId = Effective(oauth?.ClientId, existing?.OAuthClientId);
        var oauthLabel = Effective(oauth?.OrganizationAccountLabel, existing?.OrganizationAccountLabel);
        var oauthReference = Effective(oauth?.ClientSecretReference, existing?.OAuthClientSecretReference);
        var urlValid = !string.IsNullOrWhiteSpace(input.Url)
            && Uri.TryCreate(input.Url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        var errors = new NhAssistantValidationErrors()
            .Require(ServerIdPattern().IsMatch(input.Id ?? string.Empty), "id", NhAssistantFieldErrors.Invalid)
            .Require(input.Id is not { Length: > 40 }, "id", NhAssistantFieldErrors.TooLong)
            .Require(!string.IsNullOrWhiteSpace(input.DisplayName), "displayName", NhAssistantFieldErrors.Required)
            .Require(input.DisplayName is not { Length: > 200 }, "displayName", NhAssistantFieldErrors.TooLong)
            .Require(!string.IsNullOrWhiteSpace(input.Url), "url", NhAssistantFieldErrors.Required)
            .Require(input.Url is not { Length: > 2_048 }, "url", NhAssistantFieldErrors.TooLong)
            .Require(string.IsNullOrWhiteSpace(input.Url) || urlValid, "url", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantMcpAuthModes.IsValid(input.AuthMode), "authMode", NhAssistantFieldErrors.Invalid)
            .Require(
                input.AuthMode == NhAssistantMcpAuthModes.ApiKeyHeader
                    ? input.HeaderName is null || HeaderPattern().IsMatch(input.HeaderName)
                    : input.HeaderName is null,
                "headerName",
                NhAssistantFieldErrors.Invalid)
            .Require(input.Secret is not { Length: > 2_000 }, "secret", NhAssistantFieldErrors.TooLong)
            .Require(input.Secret is not { Length: > 0 } || input.SecretReference is not { Length: > 0 }, "secretReference", NhAssistantFieldErrors.Invalid)
            .Require(reference is null || NhAssistantMcpSecretReferences.TryParse(reference, out _, out _), "secretReference", NhAssistantFieldErrors.Invalid)
            .Require(
                input.AuthMode != NhAssistantMcpAuthModes.Basic || !string.IsNullOrWhiteSpace(userName),
                "userName",
                NhAssistantFieldErrors.Required)
            .Require(userName is not { Length: > 256 } && userName?.Contains(':', StringComparison.Ordinal) != true, "userName", NhAssistantFieldErrors.Invalid)
            .Require(certificate is null || NhAssistantMcpClientCertificates.IsValidName(certificate), "clientCertificateReference", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantMcpTransports.IsValid(transport), "transport", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantMcpConnectionScopes.IsValid(connectionScope), "connectionScope", NhAssistantFieldErrors.Invalid)
            .Require(input.AuthMode == NhAssistantMcpAuthModes.OAuth || connectionScope == NhAssistantMcpConnectionScopes.Organization, "connectionScope", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantMcpScopes.IsValid(oauthScopes), "oauth.scopes", NhAssistantFieldErrors.Invalid)
            .Require(oauthClientId is not { Length: > 512 }, "oauth.clientId", NhAssistantFieldErrors.TooLong)
            .Require(oauth?.ClientSecret is not { Length: > 2_000 }, "oauth.clientSecret", NhAssistantFieldErrors.TooLong)
            .Require(oauth?.ClientSecret is not { Length: > 0 } || oauth.ClientSecretReference is not { Length: > 0 }, "oauth.clientSecretReference", NhAssistantFieldErrors.Invalid)
            .Require(oauthReference is null || NhAssistantMcpSecretReferences.TryParse(oauthReference, out _, out _), "oauth.clientSecretReference", NhAssistantFieldErrors.Invalid)
            .Require(oauthLabel is not { Length: > 200 }, "oauth.organizationAccountLabel", NhAssistantFieldErrors.TooLong)
            .Require(
                input.AuthMode != NhAssistantMcpAuthModes.OAuth || connectionScope != NhAssistantMcpConnectionScopes.Organization || !string.IsNullOrWhiteSpace(oauthLabel),
                "oauth.organizationAccountLabel",
                NhAssistantFieldErrors.Required)
            .Require(input.AuthMode != NhAssistantMcpAuthModes.OAuthClientCredentials || !string.IsNullOrWhiteSpace(oauthClientId), "oauth.clientId", NhAssistantFieldErrors.Required)
            .Require(input.RequiredPolicy is not { Length: 0 or > 256 }, "requiredPolicy", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantMcpHeadlessUses.IsValid(headlessUse), "headlessUse", NhAssistantFieldErrors.Invalid)
            .Require(
                !NhAssistantMcpHeadlessUses.IsValid(headlessUse)
                    || !NhAssistantMcpAuthModes.IsValid(input.AuthMode)
                    || NhAssistantMcpHeadlessUses.FitsAuthMode(headlessUse, input.AuthMode, connectionScope),
                "headlessUse",
                NhAssistantFieldErrors.Invalid);
        if (!errors.IsEmpty)
        {
            return errors.ToResult();
        }
        if (guard.CheckUri(new Uri(input.Url)) is { } blocked)
        {
            return TaskResult.Failed(blocked, "The MCP server address is not allowed.");
        }
        if (NhAssistantMcpAuthModes.UsesSecret(input.AuthMode))
        {
            // A new stored secret replaces a reference, a new reference replaces a stored secret.
            var willHaveStoredSecret = input.Secret is null
                ? existing?.ProtectedSecret is not null && input.SecretReference is not { Length: > 0 }
                : input.Secret.Length > 0;
            var willHaveReference = reference is not null && input.Secret is not { Length: > 0 };
            if (!willHaveStoredSecret && !willHaveReference)
            {
                return NhAssistantValidationErrors.Failed("secret", NhAssistantFieldErrors.Required);
            }
            if (willHaveReference && !(await secretSource.ResolveReferenceAsync(reference!, cancellationToken)).Success)
            {
                return TaskResult.Failed(
                    NhAssistantAdminErrorCodes.McpSecretReferenceInvalid,
                    "The secret reference names no available secret.");
            }
        }
        if (certificate is not null && !certificates.Load(certificate).Success)
        {
            return TaskResult.Failed(
                NhAssistantAdminErrorCodes.McpCertificateReferenceInvalid,
                "The client certificate is not configured or has no private key.");
        }
        if (input.AuthMode == NhAssistantMcpAuthModes.OAuth && string.IsNullOrWhiteSpace(options.CurrentValue.Mcp.OAuth.RedirectUri))
        {
            return TaskResult.Failed(
                NhAssistantAdminErrorCodes.McpOAuthNotConfigured,
                "The host has no OAuth redirect URI.");
        }
        if (input.AuthMode == NhAssistantMcpAuthModes.OAuthClientCredentials)
        {
            // A confidential client: a stored secret, a reference or the client certificate.
            var willHaveSecret = oauth?.ClientSecret is null
                ? existing?.ProtectedOAuthClientSecret is not null && oauth?.ClientSecretReference is not { Length: > 0 }
                : oauth.ClientSecret.Length > 0;
            if (!willHaveSecret && oauthReference is null && certificate is null)
            {
                return NhAssistantValidationErrors.Failed("oauth.clientSecret", NhAssistantFieldErrors.Required);
            }
        }
        if (NhAssistantMcpAuthModes.IsOAuth(input.AuthMode)
            && oauthReference is not null
            && oauth?.ClientSecret is not { Length: > 0 }
            && !(await secretSource.ResolveReferenceAsync(oauthReference, cancellationToken)).Success)
        {
            return TaskResult.Failed(
                NhAssistantAdminErrorCodes.McpSecretReferenceInvalid,
                "The secret reference names no available secret.");
        }
        if (input.RequiredPolicy is { } policy && await policies.GetPolicyAsync(policy) is null)
        {
            return NhAssistantValidationErrors.Failed("requiredPolicy", NhAssistantFieldErrors.NotFound);
        }
        return TaskResult.Succeeded();
    }

    private void Apply(AssistantMcpServer server, NhAssistantMcpServerInput input)
    {
        server.DisplayName = input.DisplayName.Trim();
        server.Url = input.Url.Trim();
        server.AuthMode = input.AuthMode;
        server.HeaderName = input.AuthMode == NhAssistantMcpAuthModes.ApiKeyHeader
            ? input.HeaderName ?? NhAssistantMcpConnectionPlanner.DefaultApiKeyHeader
            : null;
        server.RequiredPolicy = string.IsNullOrWhiteSpace(input.RequiredPolicy) ? null : input.RequiredPolicy;
        server.IsEnabled = input.IsEnabled;
        if (input.HeadlessUse is not null)
        {
            server.HeadlessUse = input.HeadlessUse;
        }
        if (input.Transport is not null)
        {
            server.Transport = input.Transport;
        }
        server.UserName = input.AuthMode == NhAssistantMcpAuthModes.Basic
            ? (input.UserName ?? server.UserName)?.Trim()
            : null;
        server.ClientCertificateReference = Effective(input.ClientCertificateReference, server.ClientCertificateReference);
        if (!NhAssistantMcpAuthModes.UsesSecret(input.AuthMode))
        {
            server.ProtectedSecret = null;
            server.SecretReference = null;
            return;
        }
        if (input.Secret is { Length: > 0 })
        {
            server.ProtectedSecret = secrets.Protect(input.Secret);
            server.SecretReference = null;
        }
        else if (input.Secret is not null)
        {
            server.ProtectedSecret = null;
        }
        if (input.SecretReference is { Length: > 0 })
        {
            server.SecretReference = input.SecretReference;
            server.ProtectedSecret = null;
        }
        else if (input.SecretReference is not null)
        {
            server.SecretReference = null;
        }
    }

    /// <summary>
    /// Discovery for an OAuth server when the administrator confirms it, or when its address changed:
    /// the protected resource and authorization server are found again and pinned (D8). A server whose
    /// confirmed authorization server stays valid needs no discovery.
    /// </summary>
    private async Task<TaskResult<NhAssistantMcpDiscoveryResult?>> ConfirmAsync(
        NhAssistantMcpServerInput input,
        AssistantMcpServer? existing,
        CancellationToken cancellationToken)
    {
        if (!NhAssistantMcpAuthModes.IsOAuth(input.AuthMode))
        {
            return TaskResult<NhAssistantMcpDiscoveryResult?>.Succeeded(null);
        }
        var urlChanged = existing is null || !string.Equals(existing.Url, input.Url.Trim(), StringComparison.Ordinal);
        var pinned = existing is not null
            && NhAssistantMcpAuthModes.IsOAuth(existing.AuthMode)
            && existing.AuthorizationServerIssuer is not null
            && existing.ResourceUri is not null
            && existing.AuthorizationConfirmedAt is not null;
        if (input.OAuth?.ConfirmAuthorization != true)
        {
            return pinned && !urlChanged
                ? TaskResult<NhAssistantMcpDiscoveryResult?>.Succeeded(null)
                : TaskResult<NhAssistantMcpDiscoveryResult?>.Failed(NhAssistantValidationErrors.Failed("oauth.confirmAuthorization", NhAssistantFieldErrors.Required));
        }
        var discovered = await discovery.DiscoverAsync(input.Url.Trim(), urlChanged ? null : existing?.AuthorizationServerIssuer, cancellationToken);
        if (!discovered.Success)
        {
            return TaskResult<NhAssistantMcpDiscoveryResult?>.Failed((TaskResult)discovered);
        }
        var metadata = discovered.Data!.AuthorizationServer;
        if (input.AuthMode == NhAssistantMcpAuthModes.OAuth)
        {
            if (metadata.AuthorizationEndpoint is null)
            {
                return TaskResult<NhAssistantMcpDiscoveryResult?>.Failed(NhAssistantAdminErrorCodes.McpDiscoveryFailed, "The authorization server has no authorization endpoint.");
            }
            if (!metadata.SupportsS256)
            {
                return TaskResult<NhAssistantMcpDiscoveryResult?>.Failed(NhAssistantAdminErrorCodes.McpPkceUnsupported, "The authorization server does not support PKCE with S256.");
            }
        }
        else if (metadata.GrantTypes.Count > 0 && !metadata.GrantTypes.Contains("client_credentials", StringComparer.Ordinal))
        {
            return TaskResult<NhAssistantMcpDiscoveryResult?>.Failed(NhAssistantAdminErrorCodes.McpDiscoveryFailed, "The authorization server does not offer client credentials.");
        }
        return TaskResult<NhAssistantMcpDiscoveryResult?>.Succeeded(discovered.Data);
    }

    private void ApplyOAuth(
        AssistantMcpServer server,
        NhAssistantMcpServerInput input,
        NhAssistantMcpDiscoveryResult? confirmation,
        string actorId,
        DateTimeOffset now)
    {
        if (!NhAssistantMcpAuthModes.IsOAuth(input.AuthMode))
        {
            server.ConnectionScope = NhAssistantMcpConnectionScopes.Organization;
            server.ResourceUri = null;
            server.AuthorizationServerIssuer = null;
            server.OAuthScopes = null;
            server.OAuthClientId = null;
            server.ProtectedOAuthClientSecret = null;
            server.OAuthClientSecretReference = null;
            server.OrganizationAccountLabel = null;
            server.AuthorizationConfirmedAt = null;
            server.AuthorizationConfirmedBy = null;
            return;
        }
        server.ConnectionScope = input.AuthMode == NhAssistantMcpAuthModes.OAuthClientCredentials
            ? NhAssistantMcpConnectionScopes.Organization
            : input.ConnectionScope ?? server.ConnectionScope;
        var oauth = input.OAuth;
        server.OAuthScopes = NhAssistantMcpScopes.Join(NhAssistantMcpScopes.Split(Effective(oauth?.Scopes, server.OAuthScopes)));
        server.OAuthClientId = Effective(oauth?.ClientId, server.OAuthClientId)?.Trim();
        server.OrganizationAccountLabel = input.AuthMode == NhAssistantMcpAuthModes.OAuth && server.ConnectionScope == NhAssistantMcpConnectionScopes.Organization
            ? Effective(oauth?.OrganizationAccountLabel, server.OrganizationAccountLabel)?.Trim()
            : null;
        if (oauth?.ClientSecret is { Length: > 0 })
        {
            server.ProtectedOAuthClientSecret = secrets.Protect(oauth.ClientSecret);
            server.OAuthClientSecretReference = null;
        }
        else if (oauth?.ClientSecret is not null)
        {
            server.ProtectedOAuthClientSecret = null;
        }
        if (oauth?.ClientSecretReference is { Length: > 0 })
        {
            server.OAuthClientSecretReference = oauth.ClientSecretReference;
            server.ProtectedOAuthClientSecret = null;
        }
        else if (oauth?.ClientSecretReference is not null)
        {
            server.OAuthClientSecretReference = null;
        }
        if (server.OAuthClientId is null)
        {
            // A secret without a pre-registered client means nothing.
            server.ProtectedOAuthClientSecret = null;
            server.OAuthClientSecretReference = null;
        }
        if (confirmation is not null)
        {
            server.ResourceUri = confirmation.Resource;
            server.AuthorizationServerIssuer = confirmation.AuthorizationServer.Issuer;
            server.AuthorizationConfirmedAt = now;
            server.AuthorizationConfirmedBy = actorId;
        }
    }

    /// <summary>Whether a change makes the server's stored OAuth tokens unusable (D18).</summary>
    private static bool OAuthChanged(AssistantMcpServer before, AssistantMcpServer after)
    {
        return !string.Equals(before.Url, after.Url, StringComparison.Ordinal)
            || before.AuthMode != after.AuthMode
            || before.ConnectionScope != after.ConnectionScope
            || !string.Equals(before.AuthorizationServerIssuer, after.AuthorizationServerIssuer, StringComparison.Ordinal)
            || !string.Equals(before.OAuthScopes, after.OAuthScopes, StringComparison.Ordinal)
            || !string.Equals(before.OAuthClientId, after.OAuthClientId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Discovery for a URL the administrator typed, without storing anything.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpDiscoveryResult>> DiscoverAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > 2_048
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return TaskResult<NhAssistantMcpDiscoveryResult>.Failed(NhAssistantValidationErrors.Failed("url", NhAssistantFieldErrors.Invalid));
        }
        if (guard.CheckUri(uri) is { } blocked)
        {
            return TaskResult<NhAssistantMcpDiscoveryResult>.Failed(blocked, "The MCP server address is not allowed.");
        }
        return await discovery.DiscoverAsync(url.Trim(), null, cancellationToken);
    }

    /// <summary>The organization's OAuth connection of a server, or null when there is none.</summary>
    public async Task<TaskResult<(AssistantMcpServer Server, AssistantMcpConnection? Connection)>> GetOrganizationConnectionAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var server = await store.FindMcpServerAsync(id, cancellationToken);
        if (server is null)
        {
            return NotFound<(AssistantMcpServer, AssistantMcpConnection?)>();
        }
        var connection = await services.GetRequiredService<NewHeap.Platform.AI.Chat.Persistence.NhAssistantMcpOAuthStore>().FindConnectionAsync(
            id,
            NhAssistantMcpConnectionOwners.Organization,
            string.Empty,
            string.Empty,
            cancellationToken);
        return TaskResult<(AssistantMcpServer, AssistantMcpConnection?)>.Succeeded((server, connection?.Status is null ? null : connection));
    }

    /// <summary>Starts the administrator's authorization of the organization connection.</summary>
    public async Task<TaskResult<NhAssistantMcpAuthorizationStart>> AuthorizeOrganizationAsync(
        string id,
        string actorId,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var server = await store.FindMcpServerAsync(id, cancellationToken);
        if (server is null)
        {
            return NotFound<NhAssistantMcpAuthorizationStart>();
        }
        if (server.AuthMode != NhAssistantMcpAuthModes.OAuth || server.ConnectionScope != NhAssistantMcpConnectionScopes.Organization)
        {
            return TaskResult<NhAssistantMcpAuthorizationStart>.Failed(NhAssistantValidationErrors.Failed("authMode", NhAssistantFieldErrors.Invalid));
        }
        return await services.GetRequiredService<NhAssistantMcpAuthorizationFlows>().StartAsync(
            server,
            NhAssistantMcpConnectionOwner.Organization,
            actorId,
            allowBackgroundUse: false,
            returnUrl,
            cancellationToken);
    }

    /// <summary>Removes the organization connection and revokes its tokens.</summary>
    public async Task<TaskResult> DisconnectOrganizationAsync(string id, string actorId, CancellationToken cancellationToken)
    {
        var server = await store.FindMcpServerAsync(id, cancellationToken);
        if (server is null)
        {
            return TaskResult.Failed(NhAssistantAdminErrorCodes.McpServerNotFound, "The MCP server was not found.");
        }
        await services.GetRequiredService<NhAssistantMcpOAuthConnections>().DisconnectAsync(
            server,
            NhAssistantMcpConnectionOwners.Organization,
            string.Empty,
            AdministratorReason,
            actorId,
            cancellationToken);
        await cache.InvalidateAsync(id);
        return TaskResult.Succeeded();
    }

    /// <summary>The personal connections of a server per status.</summary>
    public async Task<TaskResult<IReadOnlyDictionary<string, int>>> SummarizePersonalConnectionsAsync(string id, CancellationToken cancellationToken)
    {
        if (await store.FindMcpServerAsync(id, cancellationToken) is null)
        {
            return NotFound<IReadOnlyDictionary<string, int>>();
        }
        return TaskResult<IReadOnlyDictionary<string, int>>.Succeeded(
            await services.GetRequiredService<NewHeap.Platform.AI.Chat.Persistence.NhAssistantMcpOAuthStore>()
                .CountTokenConnectionsAsync(id, NhAssistantMcpConnectionOwners.User, cancellationToken));
    }

    /// <summary>Revokes every personal connection of a server; each person connects again.</summary>
    public async Task<TaskResult<int>> RevokePersonalConnectionsAsync(string id, string actorId, CancellationToken cancellationToken)
    {
        var server = await store.FindMcpServerAsync(id, cancellationToken);
        if (server is null)
        {
            return NotFound<int>();
        }
        var revoked = await services.GetRequiredService<NhAssistantMcpOAuthConnections>().DisconnectAsync(
            server,
            NhAssistantMcpConnectionOwners.User,
            null,
            AdministratorReason,
            actorId,
            cancellationToken);
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpConnectionsRevoked, id, actorId, cancellationToken);
        return TaskResult<int>.Succeeded(revoked);
    }

    /// <summary>
    /// The value after the input: null keeps the stored value and an empty string clears it.
    /// </summary>
    private static string? Effective(string? input, string? stored)
    {
        if (input is null)
        {
            return stored;
        }
        return input.Length == 0 ? null : input;
    }

    private async Task AuditAsync(NhAssistantAuditEventKind kind, string objectId, string actorId, CancellationToken cancellationToken)
    {
        await NhAssistantAdminAudit.RecordAsync(_auditSinks, kind, objectId, actorId, cancellationToken);
    }

    private static TaskResult<T> NotFound<T>()
    {
        return TaskResult<T>.Failed(NhAssistantAdminErrorCodes.McpServerNotFound, "The MCP server was not found.");
    }

    private static string? FirstCode(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static string Bound(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerIdPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();
}
