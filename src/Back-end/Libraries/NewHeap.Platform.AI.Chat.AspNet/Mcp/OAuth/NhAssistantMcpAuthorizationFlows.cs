using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The start of an authorization: the flow, the URL the owner's browser opens, and the origin of the
/// callback page whose message the browser accepts.
/// </summary>
internal sealed record NhAssistantMcpAuthorizationStart(Guid FlowId, string AuthorizationUrl, DateTimeOffset ExpiresAt, string CallbackOrigin);

/// <summary>
/// What the callback hands to the browser: the flow and its one-time completion code, or a failure
/// code; and where to: the opener of the authorization window, or <see cref="ReturnUrl"/>.
/// </summary>
internal sealed record NhAssistantMcpCallbackOutcome(Guid? FlowId, string? CompletionCode, string? FailureCode, string? ReturnUrl)
{
    public override string ToString()
    {
        return $"NhAssistantMcpCallbackOutcome {{ FlowId = {FlowId}, FailureCode = {FailureCode} }}";
    }
}

/// <summary>A completed authorization: the connection that became active.</summary>
internal sealed record NhAssistantMcpCompletion(AssistantMcpServer Server, string OwnerKind, AssistantMcpConnection Connection);

/// <summary>
/// Cookie-free, owner-bound OAuth authorizations (D7): a 256-bit <c>state</c> stored as a hash, PKCE
/// <c>S256</c>, the <c>resource</c> parameter, the RFC 9207 <c>iss</c> check, single use within the
/// flow lifetime. The callback exchanges the code; the connection becomes active only when its starter
/// presents the one-time completion code with their own session. Codes, verifiers, state, tokens and
/// remote error descriptions are never logged, returned or stored in plain text.
/// </summary>
internal sealed partial class NhAssistantMcpAuthorizationFlows(
    NhAssistantMcpOAuthStore store,
    NhAssistantAdminStore adminStore,
    NhAssistantMcpDiscovery discovery,
    NhAssistantMcpOAuthClient client,
    NhAssistantMcpTokenProtector tokens,
    NhAssistantMcpOAuthConnections connections,
    IOptionsMonitor<NhAssistantOptions> options,
    ILogger<NhAssistantMcpAuthorizationFlows> logger)
{
    public async Task<TaskResult<NhAssistantMcpAuthorizationStart>> StartAsync(
        AssistantMcpServer server,
        NhAssistantMcpConnectionOwner owner,
        string startedBy,
        bool allowBackgroundUse,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var oauth = options.CurrentValue.Mcp.OAuth;
        if (server.AuthMode != NhAssistantMcpAuthModes.OAuth)
        {
            return Failed<NhAssistantMcpAuthorizationStart>(NhAssistantAdminErrorCodes.McpConnectionRequired);
        }
        if (string.IsNullOrWhiteSpace(oauth.RedirectUri))
        {
            return Failed<NhAssistantMcpAuthorizationStart>(NhAssistantAdminErrorCodes.McpOAuthNotConfigured);
        }
        if (server.AuthorizationServerIssuer is null || server.ResourceUri is null || server.AuthorizationConfirmedAt is null)
        {
            return Failed<NhAssistantMcpAuthorizationStart>(NhAssistantAdminErrorCodes.McpAuthorizationNotConfirmed);
        }
        if (returnUrl is not null && !IsAllowedReturnUrl(returnUrl, oauth))
        {
            return TaskResult<NhAssistantMcpAuthorizationStart>.Failed(NhAssistantValidationErrors.Failed("returnUrl", NhAssistantFieldErrors.Invalid));
        }
        var now = DateTimeOffset.UtcNow;
        if (await store.CountPendingFlowsAsync(startedBy, now, cancellationToken) >= oauth.MaxPendingFlowsPerActor)
        {
            return Failed<NhAssistantMcpAuthorizationStart>(NhAssistantAdminErrorCodes.McpTooManyAuthorizations);
        }
        var metadata = await discovery.GetAuthorizationServerAsync(server.AuthorizationServerIssuer, bypassCache: false, cancellationToken);
        if (!metadata.Success)
        {
            return TaskResult<NhAssistantMcpAuthorizationStart>.Failed(metadata);
        }
        if (metadata.Data!.AuthorizationEndpoint is null)
        {
            return Failed<NhAssistantMcpAuthorizationStart>(NhAssistantAdminErrorCodes.McpDiscoveryFailed);
        }
        if (!metadata.Data.SupportsS256)
        {
            return Failed<NhAssistantMcpAuthorizationStart>(NhAssistantAdminErrorCodes.McpPkceUnsupported);
        }
        var identity = await client.ResolveClientAsync(server, metadata.Data, cancellationToken);
        if (!identity.Success)
        {
            return TaskResult<NhAssistantMcpAuthorizationStart>.Failed(identity);
        }

        var existing = await store.FindConnectionAsync(server.Id, owner.Kind, owner.ActorId, owner.TenantKey, cancellationToken);
        var scopes = RequestedScopes(server, metadata.Data, existing);
        var state = NhAssistantMcpTokenProtector.RandomToken();
        var verifier = NhAssistantMcpTokenProtector.RandomToken();
        var flowId = Guid.NewGuid();
        var flow = new AssistantMcpAuthorizationFlow
        {
            Id = flowId,
            ServerId = server.Id,
            OwnerKind = owner.Kind,
            OwnerActorId = owner.ActorId,
            TenantKey = owner.TenantKey,
            StartedBy = startedBy,
            StateHash = NhAssistantMcpTokenProtector.Hash(state),
            ProtectedCodeVerifier = tokens.ProtectFlow(flowId, verifier),
            Issuer = metadata.Data.Issuer,
            Resource = server.ResourceUri,
            ClientId = identity.Data!.ClientId,
            RedirectUri = oauth.RedirectUri,
            RequestedScopes = scopes,
            AllowBackgroundUse = allowBackgroundUse,
            ReturnUrl = returnUrl,
            Status = NhAssistantMcpOAuthStore.FlowPending,
            CreatedAt = now,
            ExpiresAt = now + oauth.FlowLifetime
        };
        await store.AddFlowAsync(flow, cancellationToken);

        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", flow.ClientId),
            new("redirect_uri", flow.RedirectUri),
            new("code_challenge", Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))),
            new("code_challenge_method", "S256"),
            new("state", state),
            new("resource", flow.Resource)
        };
        if (scopes is not null)
        {
            query.Add(new("scope", scopes));
        }
        var endpoint = metadata.Data.AuthorizationEndpoint.ToString();
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = endpoint + separator + string.Join('&', query.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)));
        return TaskResult<NhAssistantMcpAuthorizationStart>.Succeeded(new NhAssistantMcpAuthorizationStart(flowId, url, flow.ExpiresAt, OriginOf(flow.RedirectUri)));
    }

    /// <summary>
    /// Handles the redirect from the authorization server: finds the flow by its <c>state</c>, checks
    /// expiry, single use and <c>iss</c>, and exchanges the code.
    /// </summary>
    public async Task<NhAssistantMcpCallbackOutcome> CallbackAsync(
        string? state,
        string? code,
        string? issuer,
        string? error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(state))
        {
            return new NhAssistantMcpCallbackOutcome(null, null, NhAssistantAdminErrorCodes.McpAuthorizationExpired, null);
        }
        var flow = await store.FindFlowByStateHashAsync(NhAssistantMcpTokenProtector.Hash(state), cancellationToken);
        if (flow is null || flow.Status != NhAssistantMcpOAuthStore.FlowPending)
        {
            return new NhAssistantMcpCallbackOutcome(null, null, NhAssistantAdminErrorCodes.McpAuthorizationExpired, null);
        }
        if (flow.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpAuthorizationExpired, cancellationToken);
        }
        var metadata = await discovery.GetAuthorizationServerAsync(flow.Issuer, bypassCache: false, cancellationToken);
        if (!metadata.Success)
        {
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpTokenRequestFailed, cancellationToken);
        }
        // RFC 9207: a present iss must name the flow's issuer; an issuer that announces it must send it.
        if ((issuer is not null && !string.Equals(issuer, flow.Issuer, StringComparison.Ordinal))
            || (issuer is null && metadata.Data!.IssuerParameterSupported))
        {
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpAuthorizationInvalid, cancellationToken);
        }
        if (!string.IsNullOrEmpty(error))
        {
            // The remote error_description is neither shown nor stored.
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpAuthorizationFailed, cancellationToken);
        }
        if (string.IsNullOrEmpty(code))
        {
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpAuthorizationInvalid, cancellationToken);
        }
        var server = await adminStore.FindMcpServerAsync(flow.ServerId, cancellationToken);
        if (server is null || !string.Equals(server.AuthorizationServerIssuer, flow.Issuer, StringComparison.Ordinal))
        {
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpAuthorizationExpired, cancellationToken);
        }
        var identity = await client.ResolveClientAsync(server, metadata.Data!, cancellationToken);
        if (!identity.Success || !string.Equals(identity.Data!.ClientId, flow.ClientId, StringComparison.Ordinal))
        {
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpAuthorizationInvalid, cancellationToken);
        }
        var verifier = tokens.UnprotectFlow(flow.Id, flow.ProtectedCodeVerifier);
        if (verifier is null)
        {
            return await FailAsync(flow, NhAssistantAdminErrorCodes.McpAuthorizationExpired, cancellationToken);
        }
        var exchanged = await client.ExchangeCodeAsync(identity.Data, metadata.Data!, code, verifier, flow.RedirectUri, flow.Resource, cancellationToken);
        if (!exchanged.Success)
        {
            var failure = NhAssistantMcpCredentialResults.FirstCode(exchanged) == NhAssistantAdminErrorCodes.McpReauthorizationRequired
                ? NhAssistantAdminErrorCodes.McpAuthorizationFailed
                : NhAssistantMcpCredentialResults.FirstCode(exchanged) ?? NhAssistantAdminErrorCodes.McpTokenRequestFailed;
            return await FailAsync(flow, failure, cancellationToken);
        }

        var completionCode = NhAssistantMcpTokenProtector.RandomToken();
        var serialized = Serialize(exchanged.Data!);
        var moved = await store.TryTransitionFlowAsync(flow.Id, NhAssistantMcpOAuthStore.FlowPending, row =>
        {
            row.Status = NhAssistantMcpOAuthStore.FlowExchanged;
            row.CompletionCodeHash = NhAssistantMcpTokenProtector.Hash(completionCode);
            row.ProtectedTokenResponse = tokens.ProtectFlow(row.Id, serialized);
            // The verifier did its work; it is not kept.
            row.ProtectedCodeVerifier = string.Empty;
        }, cancellationToken);
        if (!moved)
        {
            return new NhAssistantMcpCallbackOutcome(flow.Id, null, NhAssistantAdminErrorCodes.McpAuthorizationExpired, flow.ReturnUrl);
        }
        return new NhAssistantMcpCallbackOutcome(flow.Id, completionCode, null, flow.ReturnUrl);
    }

    /// <summary>
    /// Activates the connection of a flow when its starter presents the completion code with their own
    /// session; an organization flow also needs the admin policy then.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpCompletion>> CompleteAsync(
        Guid flowId,
        string completionCode,
        string actorId,
        Func<Task<bool>> isAdministrator,
        CancellationToken cancellationToken)
    {
        var flow = await store.FindFlowAsync(flowId, cancellationToken);
        if (flow is null || flow.Status != NhAssistantMcpOAuthStore.FlowExchanged || flow.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return Failed<NhAssistantMcpCompletion>(NhAssistantAdminErrorCodes.McpAuthorizationExpired);
        }
        var expected = Encoding.ASCII.GetBytes(flow.CompletionCodeHash ?? string.Empty);
        var presented = Encoding.ASCII.GetBytes(NhAssistantMcpTokenProtector.Hash(completionCode ?? string.Empty));
        if (!string.Equals(flow.StartedBy, actorId, StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(expected, presented))
        {
            LogCompletionRefused(logger, flow.ServerId);
            NhAssistantMcpMetrics.Authorization(NhAssistantAdminErrorCodes.McpAuthorizationInvalid);
            return Failed<NhAssistantMcpCompletion>(NhAssistantAdminErrorCodes.McpAuthorizationInvalid);
        }
        if (flow.OwnerKind == NhAssistantMcpConnectionOwners.Organization && !await isAdministrator())
        {
            return TaskResult<NhAssistantMcpCompletion>.Failed(NhAssistantAdminErrorCodes.Forbidden, "Completing an organization connection needs the admin policy.");
        }
        var server = await adminStore.FindMcpServerAsync(flow.ServerId, cancellationToken);
        if (server is null)
        {
            return Failed<NhAssistantMcpCompletion>(NhAssistantAdminErrorCodes.McpServerNotFound);
        }
        var response = Deserialize(tokens.UnprotectFlow(flow.Id, flow.ProtectedTokenResponse));
        string? stored = null;
        var completed = await store.TryTransitionFlowAsync(flow.Id, NhAssistantMcpOAuthStore.FlowExchanged, row =>
        {
            stored = row.ProtectedTokenResponse;
            row.Status = NhAssistantMcpOAuthStore.FlowCompleted;
            row.ProtectedTokenResponse = null;
            row.CompletedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        if (!completed || response is null || stored is null)
        {
            return Failed<NhAssistantMcpCompletion>(NhAssistantAdminErrorCodes.McpAuthorizationExpired);
        }
        if (!string.Equals(server.AuthorizationServerIssuer, flow.Issuer, StringComparison.Ordinal))
        {
            return Failed<NhAssistantMcpCompletion>(NhAssistantAdminErrorCodes.McpIssuerChanged);
        }
        var owner = new NhAssistantMcpConnectionOwner(flow.OwnerKind, flow.OwnerActorId, flow.TenantKey);
        var connection = await connections.StoreAuthorizationAsync(
            server,
            owner,
            response,
            flow.Issuer,
            flow.ClientId,
            flow.Resource,
            flow.RequestedScopes,
            actorId,
            flow.OwnerKind == NhAssistantMcpConnectionOwners.User ? flow.AllowBackgroundUse : null,
            cancellationToken);
        NhAssistantMcpMetrics.Authorization("completed");
        return TaskResult<NhAssistantMcpCompletion>.Succeeded(new NhAssistantMcpCompletion(server, flow.OwnerKind, connection));
    }

    /// <summary>Whether a return URL's origin is one the callback may return to.</summary>
    public static bool IsAllowedReturnUrl(string returnUrl, NhAssistantMcpOAuthOptions oauth)
    {
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Fragment.Length > 0)
        {
            return false;
        }
        return AllowedOrigins(oauth).Contains(OriginOf(returnUrl), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The origins of the application's pages that start authorizations: the configured return
    /// origins, or the redirect URI's origin when the pages and the API share one.
    /// </summary>
    public static IReadOnlyList<string> AllowedOrigins(NhAssistantMcpOAuthOptions oauth)
    {
        if (oauth.ReturnOrigins.Count > 0)
        {
            return [.. oauth.ReturnOrigins.Select(origin => origin.TrimEnd('/'))];
        }
        return string.IsNullOrWhiteSpace(oauth.RedirectUri) ? [] : [OriginOf(oauth.RedirectUri)];
    }

    private static string OriginOf(string url)
    {
        return new Uri(url).GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// The scopes of an authorization: the administrator's scopes, with the scopes a server asked for
    /// since the last connection, and <c>offline_access</c> when the authorization server advertises
    /// it and the administrator allows it. Null leaves the choice to the authorization server.
    /// </summary>
    private static string? RequestedScopes(
        AssistantMcpServer server,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        AssistantMcpConnection? existing)
    {
        var allowed = NhAssistantMcpScopes.Split(server.OAuthScopes);
        var scopes = allowed.Union(NhAssistantMcpScopes.Split(existing?.RequiredScopes)).ToList();
        if (allowed.Count > 0)
        {
            scopes = [.. scopes.Where(scope => allowed.Contains(scope, StringComparer.Ordinal))];
        }
        if (metadata.ScopesSupported.Contains("offline_access", StringComparer.Ordinal)
            && (allowed.Count == 0 || allowed.Contains("offline_access", StringComparer.Ordinal))
            && scopes.Count > 0)
        {
            scopes.Add("offline_access");
        }
        return NhAssistantMcpScopes.Join(scopes);
    }

    private async Task<NhAssistantMcpCallbackOutcome> FailAsync(
        AssistantMcpAuthorizationFlow flow,
        string code,
        CancellationToken cancellationToken)
    {
        await store.TryTransitionFlowAsync(flow.Id, NhAssistantMcpOAuthStore.FlowPending, row =>
        {
            row.Status = NhAssistantMcpOAuthStore.FlowFailed;
            row.FailureCode = code;
            row.ProtectedCodeVerifier = string.Empty;
        }, cancellationToken);
        LogAuthorizationFailed(logger, flow.ServerId, code);
        NhAssistantMcpMetrics.Authorization(code);
        return new NhAssistantMcpCallbackOutcome(flow.Id, null, code, flow.ReturnUrl);
    }

    private static string Serialize(NhAssistantMcpTokenResponse response)
    {
        return new JsonObject
        {
            ["a"] = response.AccessToken,
            ["e"] = response.ExpiresAt?.ToUnixTimeSeconds(),
            ["r"] = response.RefreshToken,
            ["re"] = response.RefreshTokenExpiresAt?.ToUnixTimeSeconds(),
            ["s"] = response.Scope
        }.ToJsonString();
    }

    private static NhAssistantMcpTokenResponse? Deserialize(string? json)
    {
        if (json is null || JsonNode.Parse(json) is not JsonObject value || value["a"]?.GetValue<string>() is not { } access)
        {
            return null;
        }
        return new NhAssistantMcpTokenResponse(
            access,
            value["e"] is { } expires ? DateTimeOffset.FromUnixTimeSeconds(expires.GetValue<long>()) : null,
            value["r"]?.GetValue<string>(),
            value["re"] is { } refreshExpires ? DateTimeOffset.FromUnixTimeSeconds(refreshExpires.GetValue<long>()) : null,
            value["s"]?.GetValue<string>());
    }

    private static TaskResult<T> Failed<T>(string code)
    {
        return TaskResult<T>.Failed(code, "The authorization did not succeed.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "An authorization of MCP server {ServerId} ended with {Code}.")]
    private static partial void LogAuthorizationFailed(ILogger logger, string serverId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A completion of an authorization of MCP server {ServerId} was refused.")]
    private static partial void LogCompletionRefused(ILogger logger, string serverId);
}
