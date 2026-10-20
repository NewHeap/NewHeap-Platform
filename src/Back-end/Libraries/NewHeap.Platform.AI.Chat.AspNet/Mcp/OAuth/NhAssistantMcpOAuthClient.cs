using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Protects OAuth material with ASP.NET Data Protection (D5): connection tokens under
/// <c>NewHeap.AI.Assistant.Mcp.Connection.v1</c> with the server and connection id as sub-purposes,
/// flows and registrations under their own purposes. A value copied to another row does not unprotect;
/// a key that left the ring unprotects to null.
/// </summary>
internal sealed class NhAssistantMcpTokenProtector(IDataProtectionProvider provider)
{
    public const string ConnectionPurpose = "NewHeap.AI.Assistant.Mcp.Connection.v1";
    public const string FlowPurpose = "NewHeap.AI.Assistant.Mcp.Flow.v1";
    public const string RegistrationPurpose = "NewHeap.AI.Assistant.Mcp.Registration.v1";

    public string ProtectConnection(string serverId, Guid connectionId, string value)
    {
        return provider.CreateProtector(ConnectionPurpose, serverId, connectionId.ToString("D")).Protect(value);
    }

    public string? UnprotectConnection(string serverId, Guid connectionId, string? value)
    {
        return Unprotect(provider.CreateProtector(ConnectionPurpose, serverId, connectionId.ToString("D")), value);
    }

    public string ProtectFlow(Guid flowId, string value)
    {
        return provider.CreateProtector(FlowPurpose, flowId.ToString("D")).Protect(value);
    }

    public string? UnprotectFlow(Guid flowId, string? value)
    {
        return Unprotect(provider.CreateProtector(FlowPurpose, flowId.ToString("D")), value);
    }

    public string ProtectRegistration(string serverId, string issuerHash, string value)
    {
        return provider.CreateProtector(RegistrationPurpose, serverId, issuerHash).Protect(value);
    }

    public string? UnprotectRegistration(string serverId, string issuerHash, string? value)
    {
        return Unprotect(provider.CreateProtector(RegistrationPurpose, serverId, issuerHash), value);
    }

    private static string? Unprotect(IDataProtector protector, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 of a value, lowercase hex: state and completion codes are stored only so.</summary>
    public static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>A random URL-safe token of 256 bits.</summary>
    public static string RandomToken()
    {
        return Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    }
}

/// <summary>
/// How the host identifies itself at an authorization server for one MCP server (Q6, D17): a
/// pre-registered client, a Client ID Metadata Document or a dynamic registration, and the token
/// endpoint authentication: <c>client_secret_basic</c>, <c>client_secret_post</c>,
/// <c>private_key_jwt</c> with the server's client certificate, or <c>none</c> for a public client.
/// Never logged.
/// </summary>
internal sealed record NhAssistantMcpClientIdentity(
    string ClientId,
    string Method,
    string AuthMethod,
    string? Secret,
    X509Certificate2? SigningCertificate)
{
    /// <summary>
    /// For a dynamic registration: the server it was registered for, so a registration the
    /// authorization server no longer knows (<c>invalid_client</c>) is removed and registered again.
    /// </summary>
    public string? RegisteredFor { get; init; }

    public override string ToString()
    {
        return $"NhAssistantMcpClientIdentity {{ Method = {Method}, AuthMethod = {AuthMethod} }}";
    }
}

/// <summary>A token response. Never logged.</summary>
internal sealed record NhAssistantMcpTokenResponse(string AccessToken, DateTimeOffset? ExpiresAt, string? RefreshToken, DateTimeOffset? RefreshTokenExpiresAt, string? Scope)
{
    public override string ToString()
    {
        return "NhAssistantMcpTokenResponse { [redacted] }";
    }
}

/// <summary>
/// The host as OAuth client of MCP authorization servers: resolves its client identity per server
/// and issuer (Q6), sends token requests with the <c>resource</c> parameter (RFC 8707) and revokes
/// tokens (RFC 7009). Errors become stable codes; remote error descriptions are never kept.
/// </summary>
internal sealed partial class NhAssistantMcpOAuthClient(
    NhAssistantMcpOAuthHttp http,
    NhAssistantMcpOAuthStore store,
    NhAssistantMcpTokenProtector tokens,
    NhAssistantMcpSecretSource secretSource,
    NhAssistantMcpClientCertificates certificates,
    IOptionsMonitor<NhAssistantOptions> options,
    ILogger<NhAssistantMcpOAuthClient> logger)
{
    public const string PreRegistered = "pre-registered";
    public const string MetadataDocument = "metadata-document";
    public const string Dynamic = "dynamic";

    /// <summary>
    /// A dynamic registration whose client secret expires within this time is registered again.
    /// </summary>
    private static readonly TimeSpan RegistrationExpirySkew = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The client identity for a server at its authorization server. Client credentials need a
    /// pre-registered confidential client. A dynamic registration whose client secret expired, or
    /// whose secret no longer decrypts, is registered again.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpClientIdentity>> ResolveClientAsync(
        AssistantMcpServer server,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        CancellationToken cancellationToken)
    {
        var clientCredentials = server.AuthMode == NhAssistantMcpAuthModes.OAuthClientCredentials;
        if (!string.IsNullOrWhiteSpace(server.OAuthClientId))
        {
            string? secret = null;
            if (server.OAuthClientSecretReference is { } reference)
            {
                var resolved = await secretSource.ResolveReferenceAsync(reference, cancellationToken);
                if (!resolved.Success)
                {
                    return TaskResult<NhAssistantMcpClientIdentity>.Failed(resolved);
                }
                secret = resolved.Data;
            }
            else if (server.ProtectedOAuthClientSecret is not null)
            {
                // A secret the key ring lost is the host's to fix, not the authorization server's.
                var stored = secretSource.UnprotectStored(server.Id, server.ProtectedOAuthClientSecret);
                if (!stored.Success)
                {
                    return TaskResult<NhAssistantMcpClientIdentity>.Failed(stored);
                }
                secret = stored.Data;
            }
            if (secret is not null)
            {
                var method = metadata.SupportsAuthMethod("client_secret_basic") || !metadata.SupportsAuthMethod("client_secret_post")
                    ? "client_secret_basic"
                    : "client_secret_post";
                return Succeeded(new NhAssistantMcpClientIdentity(server.OAuthClientId, PreRegistered, method, secret, null));
            }
            if (server.ClientCertificateReference is { } name && metadata.SupportsAuthMethod("private_key_jwt"))
            {
                var certificate = certificates.Load(name);
                if (!certificate.Success)
                {
                    return TaskResult<NhAssistantMcpClientIdentity>.Failed(certificate);
                }
                return Succeeded(new NhAssistantMcpClientIdentity(server.OAuthClientId, PreRegistered, "private_key_jwt", null, certificate.Data));
            }
            return clientCredentials
                ? Failed<NhAssistantMcpClientIdentity>(NhAssistantAdminErrorCodes.McpClientRegistrationRequired)
                : Succeeded(new NhAssistantMcpClientIdentity(server.OAuthClientId, PreRegistered, "none", null, null));
        }
        if (clientCredentials)
        {
            return Failed<NhAssistantMcpClientIdentity>(NhAssistantAdminErrorCodes.McpClientRegistrationRequired);
        }

        var oauth = options.CurrentValue.Mcp.OAuth;
        if (metadata.ClientIdMetadataDocumentSupported && !string.IsNullOrWhiteSpace(oauth.ClientMetadataDocumentUri))
        {
            return Succeeded(new NhAssistantMcpClientIdentity(oauth.ClientMetadataDocumentUri, MetadataDocument, "none", null, null));
        }

        var issuerHash = NhAssistantMcpTokenProtector.Hash(metadata.Issuer);
        var registration = await store.FindRegistrationAsync(server.Id, issuerHash, cancellationToken);
        var registeredSecret = registration is null
            ? null
            : tokens.UnprotectRegistration(server.Id, issuerHash, registration.ProtectedClientSecret);
        if (registration is not null && !IsUsable(registration, registeredSecret))
        {
            // An expired client secret, or one the key ring lost, is of no use: register again.
            LogRegistrationRenewed(logger, server.Id);
            await store.DeleteRegistrationsAsync(server.Id, cancellationToken);
            registration = null;
        }
        if (registration is null)
        {
            if (metadata.RegistrationEndpoint is null || string.IsNullOrWhiteSpace(oauth.RedirectUri))
            {
                return Failed<NhAssistantMcpClientIdentity>(NhAssistantAdminErrorCodes.McpClientRegistrationRequired);
            }
            var registered = await RegisterAsync(server, metadata, issuerHash, cancellationToken);
            if (!registered.Success)
            {
                return TaskResult<NhAssistantMcpClientIdentity>.Failed(registered);
            }
            registration = registered.Data!;
            registeredSecret = tokens.UnprotectRegistration(server.Id, issuerHash, registration.ProtectedClientSecret);
        }
        return Succeeded(new NhAssistantMcpClientIdentity(registration.ClientId, Dynamic, registration.TokenEndpointAuthMethod, registeredSecret, null)
        {
            RegisteredFor = server.Id
        });
    }

    /// <summary>
    /// Whether a dynamic registration can still authenticate: its client secret, when it has one,
    /// decrypts and has not expired.
    /// </summary>
    private static bool IsUsable(AssistantMcpClientRegistration registration, string? registeredSecret)
    {
        if (registration.ProtectedClientSecret is not null && registeredSecret is null)
        {
            return false;
        }
        return registration.ClientSecretExpiresAt is not { } expires
            || expires > DateTimeOffset.UtcNow + RegistrationExpirySkew;
    }

    public Task<TaskResult<NhAssistantMcpTokenResponse>> ExchangeCodeAsync(
        NhAssistantMcpClientIdentity client,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        string code,
        string codeVerifier,
        string redirectUri,
        string resource,
        CancellationToken cancellationToken)
    {
        return RequestTokenAsync(client, metadata, [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("code_verifier", codeVerifier),
            new("redirect_uri", redirectUri),
            new("resource", resource)
        ], cancellationToken);
    }

    public Task<TaskResult<NhAssistantMcpTokenResponse>> RefreshAsync(
        NhAssistantMcpClientIdentity client,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        string refreshToken,
        string resource,
        CancellationToken cancellationToken)
    {
        return RequestTokenAsync(client, metadata, [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("resource", resource)
        ], cancellationToken);
    }

    public Task<TaskResult<NhAssistantMcpTokenResponse>> ClientCredentialsAsync(
        NhAssistantMcpClientIdentity client,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        string resource,
        string? scope,
        CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> form = [new("grant_type", "client_credentials"), new("resource", resource)];
        if (!string.IsNullOrWhiteSpace(scope))
        {
            form.Add(new("scope", scope));
        }
        return RequestTokenAsync(client, metadata, form, cancellationToken);
    }

    /// <summary>Revokes a token at the authorization server, best effort: failures are only logged by code.</summary>
    public async Task RevokeAsync(
        NhAssistantMcpClientIdentity client,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        string token,
        string tokenTypeHint,
        CancellationToken cancellationToken)
    {
        if (metadata.RevocationEndpoint is null)
        {
            return;
        }
        List<KeyValuePair<string, string>> form = [new("token", token), new("token_type_hint", tokenTypeHint)];
        var authorization = Authenticate(client, metadata, form);
        var response = await http.PostFormAsync(metadata.RevocationEndpoint, form, authorization, cancellationToken);
        if (!response.Success || response.Data!.Status != HttpStatusCode.OK)
        {
            LogRevocationFailed(logger, response.Success ? (int)response.Data!.Status : 0);
        }
    }

    private async Task<TaskResult<NhAssistantMcpTokenResponse>> RequestTokenAsync(
        NhAssistantMcpClientIdentity client,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        List<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken)
    {
        var grantType = form.First(item => item.Key == "grant_type").Value;
        var result = await RequestTokenCoreAsync(client, metadata, form, cancellationToken);
        NhAssistantMcpMetrics.TokenRequest(
            grantType,
            result.Success ? NhAssistantMcpMetrics.Ok : NhAssistantResults.CodeOf(result, NhAssistantAdminErrorCodes.McpTokenRequestFailed));
        return result;
    }

    private async Task<TaskResult<NhAssistantMcpTokenResponse>> RequestTokenCoreAsync(
        NhAssistantMcpClientIdentity client,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        List<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken)
    {
        var authorization = Authenticate(client, metadata, form);
        var sent = await http.PostFormAsync(metadata.TokenEndpoint, form, authorization, cancellationToken);
        if (!sent.Success)
        {
            return Failed<NhAssistantMcpTokenResponse>(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
        }
        var response = sent.Data!;
        if (response.Status == HttpStatusCode.OK)
        {
            var accessToken = response.String("access_token");
            var tokenType = response.String("token_type");
            if (string.IsNullOrEmpty(accessToken) || !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return Failed<NhAssistantMcpTokenResponse>(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
            }
            var now = DateTimeOffset.UtcNow;
            var expiresIn = response.Number("expires_in");
            var refreshExpiresIn = response.Number("refresh_token_expires_in");
            return Succeeded(new NhAssistantMcpTokenResponse(
                accessToken,
                expiresIn is > 0 ? now.AddSeconds(expiresIn.Value) : null,
                response.String("refresh_token"),
                refreshExpiresIn is > 0 ? now.AddSeconds(refreshExpiresIn.Value) : null,
                response.String("scope")));
        }
        var error = response.String("error");
        // Only the standard codes are logged: the remote text is not trusted.
        LogTokenRequestFailed(
            logger,
            (int)response.Status,
            error is "invalid_request" or "invalid_client" or "invalid_grant" or "unauthorized_client" or "unsupported_grant_type" or "invalid_scope"
                ? error
                : "other");
        if (error == "invalid_client" && client.RegisteredFor is { } serverId)
        {
            // The authorization server no longer knows the dynamic registration: it is removed, and
            // the next authorization registers again.
            LogRegistrationRenewed(logger, serverId);
            await store.DeleteRegistrationsAsync(serverId, cancellationToken);
            return Failed<NhAssistantMcpTokenResponse>(NhAssistantAdminErrorCodes.McpClientRegistrationRequired);
        }
        return error switch
        {
            "invalid_grant" => Failed<NhAssistantMcpTokenResponse>(NhAssistantAdminErrorCodes.McpReauthorizationRequired),
            "invalid_scope" => Failed<NhAssistantMcpTokenResponse>(NhAssistantAdminErrorCodes.McpScopeNotAllowed),
            _ => Failed<NhAssistantMcpTokenResponse>(NhAssistantAdminErrorCodes.McpTokenRequestFailed)
        };
    }

    /// <summary>Adds the client authentication to a token or revocation request (D17).</summary>
    private static AuthenticationHeaderValue? Authenticate(
        NhAssistantMcpClientIdentity client,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        List<KeyValuePair<string, string>> form)
    {
        switch (client.AuthMethod)
        {
            case "client_secret_basic" when client.Secret is not null:
                // RFC 6749 section 2.3.1: the id and secret are form-encoded before base64.
                var credentials = Uri.EscapeDataString(client.ClientId) + ":" + Uri.EscapeDataString(client.Secret);
                return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
            case "client_secret_post" when client.Secret is not null:
                form.Add(new("client_id", client.ClientId));
                form.Add(new("client_secret", client.Secret));
                return null;
            case "private_key_jwt" when client.SigningCertificate is not null:
                form.Add(new("client_id", client.ClientId));
                form.Add(new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
                form.Add(new("client_assertion", CreateAssertion(client, metadata)));
                return null;
            default:
                form.Add(new("client_id", client.ClientId));
                return null;
        }
    }

    /// <summary>
    /// A <c>private_key_jwt</c> client assertion (RFC 7523): signed with the client certificate's
    /// private key, the client as issuer and subject, the token endpoint as audience, valid one minute.
    /// </summary>
    private static string CreateAssertion(NhAssistantMcpClientIdentity client, NhAssistantMcpAuthorizationServerMetadata metadata)
    {
        var certificate = client.SigningCertificate!;
        var credentials = certificate.GetECDsaPrivateKey() is not null
            ? new X509SigningCredentials(certificate, SecurityAlgorithms.EcdsaSha256)
            : new X509SigningCredentials(certificate, SecurityAlgorithms.RsaSha256);
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            client.ClientId,
            metadata.TokenEndpoint.ToString(),
            [new Claim("sub", client.ClientId), new Claim("jti", Guid.NewGuid().ToString("N"))],
            now,
            now.AddMinutes(1),
            credentials));
    }

    private async Task<TaskResult<AssistantMcpClientRegistration>> RegisterAsync(
        AssistantMcpServer server,
        NhAssistantMcpAuthorizationServerMetadata metadata,
        string issuerHash,
        CancellationToken cancellationToken)
    {
        var oauth = options.CurrentValue.Mcp.OAuth;
        // A confidential web client when the server can authenticate it with a secret, else public.
        var authMethod = metadata.SupportsAuthMethod("client_secret_basic") ? "client_secret_basic" : "none";
        var request = new JsonObject
        {
            ["client_name"] = oauth.ClientName,
            ["redirect_uris"] = new JsonArray(oauth.RedirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = authMethod,
            ["application_type"] = "web"
        };
        if (!string.IsNullOrWhiteSpace(oauth.ClientUri))
        {
            request["client_uri"] = oauth.ClientUri;
        }
        var sent = await http.PostJsonAsync(metadata.RegistrationEndpoint!, request.ToJsonString(), cancellationToken);
        if (!sent.Success)
        {
            // The registration endpoint was not reached: its code, such as unreachable or blocked.
            var code = NhAssistantResults.CodeOf(sent, NhAssistantAdminErrorCodes.McpUnreachable);
            LogRegistrationFailed(logger, server.Id, 0, code);
            return Failed<AssistantMcpClientRegistration>(code);
        }
        var response = sent.Data!;
        if (response.Status is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            // A refusal (4xx) means the server registers no clients for the host: an administrator
            // enters a client id. Another answer is the remote server's failure.
            var code = (int)response.Status is >= 400 and < 500
                ? NhAssistantAdminErrorCodes.McpClientRegistrationRequired
                : NhAssistantAdminErrorCodes.McpTokenRequestFailed;
            LogRegistrationFailed(logger, server.Id, (int)response.Status, code);
            return Failed<AssistantMcpClientRegistration>(code);
        }
        var clientId = response.String("client_id");
        if (string.IsNullOrEmpty(clientId))
        {
            LogRegistrationFailed(logger, server.Id, (int)response.Status, NhAssistantAdminErrorCodes.McpTokenRequestFailed);
            return Failed<AssistantMcpClientRegistration>(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
        }
        var secret = response.String("client_secret");
        var accessToken = response.String("registration_access_token");
        var secretExpires = response.Number("client_secret_expires_at");
        var registration = new AssistantMcpClientRegistration
        {
            ServerId = server.Id,
            IssuerHash = issuerHash,
            Issuer = metadata.Issuer,
            Method = Dynamic,
            ClientId = clientId,
            ProtectedClientSecret = secret is null ? null : tokens.ProtectRegistration(server.Id, issuerHash, secret),
            ProtectedRegistrationAccessToken = accessToken is null ? null : tokens.ProtectRegistration(server.Id, issuerHash, accessToken),
            TokenEndpointAuthMethod = secret is null ? "none" : response.String("token_endpoint_auth_method") ?? authMethod,
            ClientSecretExpiresAt = secretExpires is > 0 ? DateTimeOffset.FromUnixTimeSeconds(secretExpires.Value) : null,
            RegisteredAt = DateTimeOffset.UtcNow
        };
        return TaskResult<AssistantMcpClientRegistration>.Succeeded(await store.AddRegistrationAsync(registration, cancellationToken));
    }

    private static TaskResult<T> Succeeded<T>(T value)
    {
        return TaskResult<T>.Succeeded(value);
    }

    private static TaskResult<T> Failed<T>(string code)
    {
        return TaskResult<T>.Failed(code, "The OAuth request did not succeed.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An MCP token request failed with status {Status} ({Error}).")]
    private static partial void LogTokenRequestFailed(ILogger logger, int status, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Revoking an MCP token did not succeed (status {Status}).")]
    private static partial void LogRevocationFailed(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The dynamic client registration for MCP server {ServerId} did not succeed (status {Status}, {Code}).")]
    private static partial void LogRegistrationFailed(ILogger logger, string serverId, int status, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "The dynamic client registration for MCP server {ServerId} is no longer usable and is registered again.")]
    private static partial void LogRegistrationRenewed(ILogger logger, string serverId);
}
