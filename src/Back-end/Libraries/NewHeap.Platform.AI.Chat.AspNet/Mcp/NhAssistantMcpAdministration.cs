using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Administrator input for an MCP server. <see cref="Secret"/>: null keeps the stored secret,
/// an empty string clears it, any other value replaces it.
/// </summary>
internal sealed record NhAssistantMcpServerInput(
    string Id,
    string DisplayName,
    string Url,
    string AuthMode,
    string? HeaderName,
    string? Secret,
    string? RequiredPolicy,
    bool IsEnabled);

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
    IAuthorizationPolicyProvider policies,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks)
{
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
        var validation = await ValidateAsync(input, hasStoredSecret: false, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<AssistantMcpServer>.Failed(validation);
        }
        var now = DateTimeOffset.UtcNow;
        var server = new AssistantMcpServer
        {
            Id = input.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        Apply(server, input);
        if (!await store.AddMcpServerAsync(server, cancellationToken))
        {
            return TaskResult<AssistantMcpServer>.Failed(
                NhAssistantAdminErrorCodes.McpServerExists,
                "An MCP server with this id already exists.");
        }
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerCreated, server.Id, actorId, cancellationToken);
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
        var validation = await ValidateAsync(input, existing.ProtectedSecret is not null, cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<AssistantMcpServer>.Failed(validation);
        }
        var updated = await store.UpdateMcpServerAsync(input.Id, server => Apply(server, input), cancellationToken);
        await cache.InvalidateAsync(input.Id);
        await AuditAsync(NhAssistantAuditEventKind.AdminMcpServerUpdated, input.Id, actorId, cancellationToken);
        return TaskResult<AssistantMcpServer>.Succeeded(updated!);
    }

    public async Task<TaskResult> DeleteAsync(string id, string actorId, CancellationToken cancellationToken)
    {
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
            return TaskResult<NhAssistantMcpTestResult>.Succeeded(new NhAssistantMcpTestResult(false, FirstCode(lease), null));
        }
        await using (lease.Data)
        {
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

    private async Task<TaskResult> ValidateAsync(
        NhAssistantMcpServerInput input,
        bool hasStoredSecret,
        CancellationToken cancellationToken)
    {
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
                input.AuthMode == NhAssistantMcpAuthModes.ApiKey
                    ? input.HeaderName is null || HeaderPattern().IsMatch(input.HeaderName)
                    : input.HeaderName is null,
                "headerName",
                NhAssistantFieldErrors.Invalid)
            .Require(input.Secret is not { Length: > 2_000 }, "secret", NhAssistantFieldErrors.TooLong)
            .Require(input.RequiredPolicy is not { Length: 0 or > 256 }, "requiredPolicy", NhAssistantFieldErrors.Invalid);
        if (!errors.IsEmpty)
        {
            return errors.ToResult();
        }
        if (guard.CheckUri(new Uri(input.Url)) is { } blocked)
        {
            return TaskResult.Failed(blocked, "The MCP server address is not allowed.");
        }
        var needsSecret = input.AuthMode is NhAssistantMcpAuthModes.Bearer or NhAssistantMcpAuthModes.ApiKey;
        var willHaveSecret = input.Secret is null ? hasStoredSecret : input.Secret.Length > 0;
        if (needsSecret && !willHaveSecret)
        {
            return NhAssistantValidationErrors.Failed("secret", NhAssistantFieldErrors.Required);
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
        server.HeaderName = input.AuthMode == NhAssistantMcpAuthModes.ApiKey
            ? input.HeaderName ?? NhAssistantMcpConnectionPlanner.DefaultApiKeyHeader
            : null;
        server.RequiredPolicy = string.IsNullOrWhiteSpace(input.RequiredPolicy) ? null : input.RequiredPolicy;
        server.IsEnabled = input.IsEnabled;
        if (input.AuthMode is NhAssistantMcpAuthModes.None or NhAssistantMcpAuthModes.ForwardUserToken)
        {
            server.ProtectedSecret = null;
        }
        else if (input.Secret is not null)
        {
            server.ProtectedSecret = input.Secret.Length == 0 ? null : secrets.Protect(input.Secret);
        }
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
