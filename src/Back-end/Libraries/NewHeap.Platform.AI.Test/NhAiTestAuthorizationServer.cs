using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace NewHeap.Platform.AI.Test;

/// <summary>
/// What the test authorization server answers to the next authorization request.
/// </summary>
public enum NhAiTestAuthorizationOutcome
{
    /// <summary>The person approves; the redirect carries a code.</summary>
    Approve,

    /// <summary>The person denies; the redirect carries <c>error=access_denied</c>.</summary>
    Deny
}

/// <summary>
/// Behavior of <see cref="NhAiTestAuthorizationServer"/>. Every property can change while the server
/// runs, so a test can script the next answer.
/// </summary>
public sealed class NhAiTestAuthorizationServerOptions
{
    /// <summary>An issuer path such as <c>/tenant-a</c>; empty for an issuer at the origin.</summary>
    public string IssuerPath { get; set; } = string.Empty;

    /// <summary>Whether the metadata advertises PKCE with <c>S256</c> and authorization requires it.</summary>
    public bool SupportsPkce { get; set; } = true;

    /// <summary>Whether dynamic client registration (RFC 7591) is offered.</summary>
    public bool SupportsDynamicRegistration { get; set; } = true;

    /// <summary>Whether a URL client id is accepted as a Client ID Metadata Document, fetched on use.</summary>
    public bool SupportsClientIdMetadataDocuments { get; set; }

    /// <summary>Whether redirects carry <c>iss</c> (RFC 9207).</summary>
    public bool SendsIssuerInResponse { get; set; } = true;

    /// <summary>Overrides the <c>iss</c> a redirect carries, to test mix-up protection.</summary>
    public string? ResponseIssuerOverride { get; set; }

    /// <summary>Whether a refresh issues a new refresh token and retires the old one.</summary>
    public bool RotateRefreshTokens { get; set; } = true;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public NhAiTestAuthorizationOutcome NextOutcome { get; set; } = NhAiTestAuthorizationOutcome.Approve;

    /// <summary>The remote account that signs in at the next authorization.</summary>
    public string Subject { get; set; } = "remote-user";

    public IList<string> ScopesSupported { get; set; } = ["mcp:read", "mcp:write"];

    /// <summary>Makes every token request fail with this OAuth error, such as <c>invalid_grant</c>.</summary>
    public string? FailTokenRequestsWith { get; set; }

    /// <summary>Delays every token response, to exercise concurrent renewals.</summary>
    public TimeSpan TokenResponseDelay { get; set; }

    public bool SupportsRevocation { get; set; } = true;
}

/// <summary>A token request the server received: never a secret, code or token.</summary>
public sealed record NhAiTestTokenRequest(string GrantType, string? ClientId, string? Resource, string? Scope, string? ClientAuthentication);

/// <summary>
/// A real OAuth 2.1 authorization server on loopback for tests of MCP connections: metadata
/// (RFC 8414), authorization with PKCE <c>S256</c> and scripted outcomes, the token endpoint with
/// authorization code, refresh token rotation with reuse detection and client credentials, dynamic
/// client registration, Client ID Metadata Documents, revocation (RFC 7009) and JWT access tokens
/// whose audience is the resource (RFC 8707). Protect a test resource with
/// <see cref="NhAiTestProtectedResource"/>.
/// </summary>
public sealed class NhAiTestAuthorizationServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RsaSecurityKey _key;
    private readonly ConcurrentDictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RefreshToken> _refreshTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _revokedFamilies = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<NhAiTestTokenRequest> _tokenRequests = new();
    private readonly ConcurrentQueue<string> _revocations = new();
    private readonly HttpClient _documents = new();

    private NhAiTestAuthorizationServer(WebApplication app, RsaSecurityKey key, string origin, NhAiTestAuthorizationServerOptions options)
    {
        _app = app;
        _key = key;
        Origin = origin;
        Options = options;
    }

    public NhAiTestAuthorizationServerOptions Options { get; }

    /// <summary>The server's origin, for example <c>http://127.0.0.1:5123</c>.</summary>
    public string Origin { get; }

    public string Issuer => Origin + Options.IssuerPath;

    public string AuthorizationEndpoint => Issuer + "/authorize";

    public string TokenEndpoint => Issuer + "/token";

    public string RegistrationEndpoint => Issuer + "/register";

    public string RevocationEndpoint => Issuer + "/revoke";

    public string JwksUri => Issuer + "/jwks";

    public IReadOnlyList<NhAiTestTokenRequest> TokenRequests => [.. _tokenRequests];

    /// <summary>The number of revocation requests, per token type hint or <c>unknown</c>.</summary>
    public IReadOnlyList<string> Revocations => [.. _revocations];

    /// <summary>The ids of the clients registered dynamically.</summary>
    public IReadOnlyList<string> DynamicClients => [.. _clients.Values.Where(client => client.Dynamic).Select(client => client.Id)];

    public static async Task<NhAiTestAuthorizationServer> StartAsync(Action<NhAiTestAuthorizationServerOptions>? configure = null)
    {
        var options = new NhAiTestAuthorizationServerOptions();
        configure?.Invoke(options);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };
        NhAiTestAuthorizationServer? server = null;
        app.Run(async context => await server!.HandleAsync(context));
        await app.StartAsync();
        server = new NhAiTestAuthorizationServer(app, key, app.Urls.First().TrimEnd('/'), options);
        return server;
    }

    /// <summary>
    /// Registers a client before any authorization, as an administrator registers the host at an
    /// authorization server. A client without a secret is public.
    /// </summary>
    public void RegisterClient(string clientId, string? clientSecret, params string[] redirectUris)
    {
        _clients[clientId] = new Client(clientId, clientSecret, [.. redirectUris], Dynamic: false, PublicKey: null);
    }

    /// <summary>Registers a client that authenticates with a <c>private_key_jwt</c> assertion.</summary>
    public void RegisterClient(string clientId, AsymmetricSecurityKey publicKey, params string[] redirectUris)
    {
        _clients[clientId] = new Client(clientId, null, [.. redirectUris], Dynamic: false, PublicKey: publicKey);
    }

    /// <summary>Issues an access token directly, for tests of a resource server.</summary>
    public string IssueAccessToken(string subject, string resource, string scope, string clientId = "test-client", TimeSpan? lifetime = null)
    {
        return CreateAccessToken(subject, resource, scope, clientId, lifetime ?? Options.AccessTokenLifetime);
    }

    /// <summary>
    /// Validates an access token of this server for <paramref name="resource"/>: signature, issuer,
    /// audience and lifetime. Returns the principal, or null.
    /// </summary>
    public ClaimsPrincipal? ValidateAccessToken(string token, string resource)
    {
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
                token,
                new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = resource,
                    IssuerSigningKey = _key,
                    ClockSkew = TimeSpan.Zero
                },
                out var validated);
            var jti = (validated as JwtSecurityToken)?.Id;
            var family = principal.FindFirst("family")?.Value;
            return family is not null && _revokedFamilies.ContainsKey(family) || jti is not null && _revokedFamilies.ContainsKey(jti)
                ? null
                : principal;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _documents.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var issuerPath = Options.IssuerPath;
        if (path == "/.well-known/oauth-authorization-server" + issuerPath
            || path == "/.well-known/openid-configuration" + issuerPath
            || path == issuerPath + "/.well-known/openid-configuration")
        {
            await WriteJsonAsync(context, 200, Metadata());
            return;
        }
        if (!path.StartsWith(issuerPath + "/", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 404;
            return;
        }
        switch (path[issuerPath.Length..])
        {
            case "/authorize" when HttpMethods.IsGet(context.Request.Method):
                await AuthorizeAsync(context);
                return;
            case "/token" when HttpMethods.IsPost(context.Request.Method):
                await TokenAsync(context);
                return;
            case "/register" when HttpMethods.IsPost(context.Request.Method) && Options.SupportsDynamicRegistration:
                await RegisterAsync(context);
                return;
            case "/revoke" when HttpMethods.IsPost(context.Request.Method) && Options.SupportsRevocation:
                await RevokeAsync(context);
                return;
            case "/jwks":
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["keys"] = new JsonArray(JsonNode.Parse(JsonSerializer.Serialize(JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_key.Rsa.ExportParameters(false)) { KeyId = _key.KeyId }))))
                });
                return;
            default:
                context.Response.StatusCode = 404;
                return;
        }
    }

    private JsonObject Metadata()
    {
        var metadata = new JsonObject
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = AuthorizationEndpoint,
            ["token_endpoint"] = TokenEndpoint,
            ["jwks_uri"] = JwksUri,
            ["response_types_supported"] = new JsonArray("code"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token", "client_credentials"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_basic", "client_secret_post", "private_key_jwt", "none"),
            ["scopes_supported"] = new JsonArray([.. Options.ScopesSupported.Select(scope => (JsonNode)scope)]),
            ["authorization_response_iss_parameter_supported"] = Options.SendsIssuerInResponse,
            ["client_id_metadata_document_supported"] = Options.SupportsClientIdMetadataDocuments
        };
        if (Options.SupportsPkce)
        {
            metadata["code_challenge_methods_supported"] = new JsonArray("S256");
        }
        if (Options.SupportsDynamicRegistration)
        {
            metadata["registration_endpoint"] = RegistrationEndpoint;
        }
        if (Options.SupportsRevocation)
        {
            metadata["revocation_endpoint"] = RevocationEndpoint;
        }
        return metadata;
    }

    private async Task AuthorizeAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();
        var client = await FindClientAsync(clientId);
        if (client is null || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal) || query["response_type"] != "code")
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("invalid request");
            return;
        }
        var state = query["state"].ToString();
        var responseIssuer = Options.ResponseIssuerOverride ?? Issuer;
        var issuerParameter = Options.SendsIssuerInResponse ? "&iss=" + Uri.EscapeDataString(responseIssuer) : string.Empty;
        if (Options.SupportsPkce && (query["code_challenge_method"] != "S256" || string.IsNullOrEmpty(query["code_challenge"])))
        {
            context.Response.Redirect(redirectUri + "?error=invalid_request&state=" + Uri.EscapeDataString(state) + issuerParameter);
            return;
        }
        if (Options.NextOutcome == NhAiTestAuthorizationOutcome.Deny)
        {
            context.Response.Redirect(redirectUri + "?error=access_denied&error_description=" + Uri.EscapeDataString("The person said no.") + "&state=" + Uri.EscapeDataString(state) + issuerParameter);
            return;
        }
        var code = RandomToken();
        _codes[code] = new AuthorizationCode(
            clientId,
            redirectUri,
            query["code_challenge"].ToString(),
            query["resource"].ToString(),
            query["scope"].ToString(),
            Options.Subject,
            DateTimeOffset.UtcNow.AddMinutes(1));
        context.Response.Redirect(redirectUri + "?code=" + Uri.EscapeDataString(code) + "&state=" + Uri.EscapeDataString(state) + issuerParameter);
    }

    private async Task TokenAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        var grantType = form["grant_type"].ToString();
        var (client, authentication) = await AuthenticateClientAsync(context, form);
        _tokenRequests.Enqueue(new NhAiTestTokenRequest(grantType, client?.Id ?? form["client_id"].ToString(), form["resource"].ToString(), form["scope"].ToString(), authentication));
        if (Options.TokenResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(Options.TokenResponseDelay);
        }
        if (client is null)
        {
            await WriteErrorAsync(context, 401, "invalid_client");
            return;
        }
        if (Options.FailTokenRequestsWith is { } failure)
        {
            await WriteErrorAsync(context, 400, failure);
            return;
        }
        switch (grantType)
        {
            case "authorization_code":
                await ExchangeCodeAsync(context, form, client);
                return;
            case "refresh_token":
                await RefreshAsync(context, form, client);
                return;
            case "client_credentials" when client.Secret is not null || client.PublicKey is not null:
                var scope = form["scope"].ToString();
                await WriteTokensAsync(context, client.Id, client.Id, form["resource"].ToString(), scope, family: null);
                return;
            default:
                await WriteErrorAsync(context, 400, "unsupported_grant_type");
                return;
        }
    }

    private async Task ExchangeCodeAsync(HttpContext context, IFormCollection form, Client client)
    {
        if (!_codes.TryRemove(form["code"].ToString(), out var code)
            || code.ExpiresAt < DateTimeOffset.UtcNow
            || code.ClientId != client.Id
            || code.RedirectUri != form["redirect_uri"].ToString()
            || code.Resource != form["resource"].ToString())
        {
            await WriteErrorAsync(context, 400, "invalid_grant");
            return;
        }
        if (Options.SupportsPkce && Challenge(form["code_verifier"].ToString()) != code.Challenge)
        {
            await WriteErrorAsync(context, 400, "invalid_grant");
            return;
        }
        await WriteTokensAsync(context, client.Id, code.Subject, code.Resource, code.Scope, family: RandomToken());
    }

    private async Task RefreshAsync(HttpContext context, IFormCollection form, Client client)
    {
        var presented = form["refresh_token"].ToString();
        if (!_refreshTokens.TryGetValue(presented, out var refresh) || refresh.ClientId != client.Id || _revokedFamilies.ContainsKey(refresh.Family))
        {
            await WriteErrorAsync(context, 400, "invalid_grant");
            return;
        }
        if (refresh.Retired)
        {
            // Reuse of a rotated refresh token: the whole family is revoked.
            _revokedFamilies[refresh.Family] = true;
            await WriteErrorAsync(context, 400, "invalid_grant");
            return;
        }
        if (Options.RotateRefreshTokens)
        {
            _refreshTokens[presented] = refresh with { Retired = true };
        }
        var resource = string.IsNullOrEmpty(form["resource"]) ? refresh.Resource : form["resource"].ToString();
        var scope = string.IsNullOrEmpty(form["scope"]) ? refresh.Scope : form["scope"].ToString();
        await WriteTokensAsync(context, client.Id, refresh.Subject, resource, scope, refresh.Family, reuseRefresh: Options.RotateRefreshTokens ? null : presented);
    }

    private async Task WriteTokensAsync(
        HttpContext context,
        string clientId,
        string subject,
        string resource,
        string scope,
        string? family,
        string? reuseRefresh = null)
    {
        var lifetime = Options.AccessTokenLifetime;
        var response = new JsonObject
        {
            ["access_token"] = CreateAccessToken(subject, resource, scope, clientId, lifetime, family),
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)lifetime.TotalSeconds,
            ["scope"] = scope
        };
        if (family is not null)
        {
            var refreshToken = reuseRefresh ?? RandomToken();
            _refreshTokens[refreshToken] = new RefreshToken(clientId, subject, resource, scope, family, Retired: false);
            response["refresh_token"] = refreshToken;
        }
        await WriteJsonAsync(context, 200, response);
    }

    private string CreateAccessToken(string subject, string resource, string scope, string clientId, TimeSpan lifetime, string? family = null)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("client_id", clientId),
            new("scope", scope),
            new("jti", Guid.NewGuid().ToString("N"))
        };
        if (family is not null)
        {
            claims.Add(new Claim("family", family));
        }
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer,
            resource,
            claims,
            now,
            now.Add(lifetime),
            new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)));
    }

    private async Task<(Client? Client, string? Authentication)> AuthenticateClientAsync(HttpContext context, IFormCollection form)
    {
        if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
            && string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && header.Parameter is { } parameter)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
            var separator = decoded.IndexOf(':');
            var id = Uri.UnescapeDataString(decoded[..separator]);
            var secret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
            return _clients.TryGetValue(id, out var basic) && basic.Secret is not null && FixedEquals(basic.Secret, secret)
                ? (basic, "client_secret_basic")
                : (null, "client_secret_basic");
        }
        if (form["client_assertion_type"] == "urn:ietf:params:oauth:client-assertion-type:jwt-bearer")
        {
            var assertion = form["client_assertion"].ToString();
            var clientId = new JwtSecurityTokenHandler().CanReadToken(assertion) ? new JwtSecurityTokenHandler().ReadJwtToken(assertion).Issuer : null;
            if (clientId is null || !_clients.TryGetValue(clientId, out var keyed) || keyed.PublicKey is null)
            {
                return (null, "private_key_jwt");
            }
            try
            {
                new JwtSecurityTokenHandler().ValidateToken(assertion, new TokenValidationParameters
                {
                    ValidIssuer = clientId,
                    ValidAudiences = [TokenEndpoint, Issuer],
                    IssuerSigningKey = keyed.PublicKey,
                    ClockSkew = TimeSpan.FromSeconds(30)
                }, out _);
                return (keyed, "private_key_jwt");
            }
            catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
            {
                return (null, "private_key_jwt");
            }
        }
        var formId = form["client_id"].ToString();
        var formSecret = form["client_secret"].ToString();
        var client = await FindClientAsync(formId);
        if (client is null)
        {
            return (null, null);
        }
        if (!string.IsNullOrEmpty(formSecret))
        {
            return client.Secret is not null && FixedEquals(client.Secret, formSecret) ? (client, "client_secret_post") : (null, "client_secret_post");
        }
        // A public client: only the authorization code and refresh grants, protected by PKCE and rotation.
        return client.Secret is null && client.PublicKey is null ? (client, "none") : (null, "none");
    }

    private async Task<Client?> FindClientAsync(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            return client;
        }
        if (!Options.SupportsClientIdMetadataDocuments || !Uri.TryCreate(clientId, UriKind.Absolute, out var document))
        {
            return null;
        }
        try
        {
            using var response = await _documents.GetAsync(document);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var metadata = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (metadata?["client_id"]?.GetValue<string>() != clientId)
            {
                return null;
            }
            var redirects = metadata["redirect_uris"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? [];
            return new Client(clientId, null, redirects, Dynamic: false, PublicKey: null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task RegisterAsync(HttpContext context)
    {
        var request = await JsonNode.ParseAsync(context.Request.Body);
        var redirects = request?["redirect_uris"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray() ?? [];
        var method = request?["token_endpoint_auth_method"]?.GetValue<string>() ?? "client_secret_basic";
        if (redirects.Length == 0)
        {
            await WriteErrorAsync(context, 400, "invalid_redirect_uri");
            return;
        }
        var clientId = "dyn-" + RandomToken()[..16];
        var secret = method == "none" ? null : RandomToken();
        _clients[clientId] = new Client(clientId, secret, redirects, Dynamic: true, PublicKey: null);
        var response = new JsonObject
        {
            ["client_id"] = clientId,
            ["redirect_uris"] = new JsonArray([.. redirects.Select(item => (JsonNode)item)]),
            ["token_endpoint_auth_method"] = method,
            ["registration_access_token"] = RandomToken()
        };
        if (secret is not null)
        {
            response["client_secret"] = secret;
            response["client_secret_expires_at"] = 0;
        }
        await WriteJsonAsync(context, 201, response);
    }

    private async Task RevokeAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        var token = form["token"].ToString();
        _revocations.Enqueue(form["token_type_hint"].ToString() is { Length: > 0 } hint ? hint : "unknown");
        if (_refreshTokens.TryGetValue(token, out var refresh))
        {
            _revokedFamilies[refresh.Family] = true;
        }
        else if (new JwtSecurityTokenHandler().CanReadToken(token))
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var family = jwt.Claims.FirstOrDefault(claim => claim.Type == "family")?.Value;
            _revokedFamilies[family ?? jwt.Id] = true;
        }
        context.Response.StatusCode = 200;
    }

    private static string Challenge(string verifier)
    {
        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    private static string RandomToken()
    {
        return Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    }

    private static bool FixedEquals(string expected, string actual)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string error)
    {
        return WriteJsonAsync(context, status, new JsonObject { ["error"] = error, ["error_description"] = "Test authorization server: " + error });
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(body.ToJsonString());
    }

    private sealed record Client(string Id, string? Secret, string[] RedirectUris, bool Dynamic, AsymmetricSecurityKey? PublicKey);

    private sealed record AuthorizationCode(
        string ClientId,
        string RedirectUri,
        string Challenge,
        string Resource,
        string Scope,
        string Subject,
        DateTimeOffset ExpiresAt);

    private sealed record RefreshToken(string ClientId, string Subject, string Resource, string Scope, string Family, bool Retired);
}

/// <summary>
/// A resource server's view of <see cref="NhAiTestAuthorizationServer"/>: serves protected resource
/// metadata (RFC 9728) and requires an access token of the server for <see cref="Resource"/> on every
/// request below <see cref="Path"/>, answering <c>401</c> with a <c>WWW-Authenticate</c> challenge or
/// <c>403 insufficient_scope</c> for missing <see cref="RequiredScopes"/>. Use it as middleware in
/// front of a test MCP server.
/// </summary>
public sealed class NhAiTestProtectedResource(NhAiTestAuthorizationServer server, string resource, string path = "/mcp")
{
    private readonly ConcurrentQueue<NhAiTestResourceRequest> _requests = new();

    public string Resource { get; } = resource.TrimEnd('/');

    public string Path { get; } = path;

    /// <summary>Scopes every request needs; change them to test step-up.</summary>
    public IList<string> RequiredScopes { get; set; } = [];

    /// <summary>Scopes the challenge of an unauthenticated request asks for.</summary>
    public string? ChallengeScope { get; set; }

    /// <summary>The authenticated requests: subject, client id and scopes, never the token.</summary>
    public IReadOnlyList<NhAiTestResourceRequest> Requests => [.. _requests];

    /// <summary>The number of requests refused with <c>401</c> or <c>403</c>.</summary>
    public int Refusals { get; private set; }

    public string MetadataPath => "/.well-known/oauth-protected-resource" + Path;

    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var requestPath = context.Request.Path.Value ?? string.Empty;
        var origin = context.Request.Scheme + "://" + context.Request.Host;
        if (requestPath == MetadataPath || requestPath == "/.well-known/oauth-protected-resource")
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(new JsonObject
            {
                ["resource"] = Resource,
                ["authorization_servers"] = new JsonArray(server.Issuer),
                ["scopes_supported"] = new JsonArray([.. server.Options.ScopesSupported.Select(scope => (JsonNode)scope)]),
                ["bearer_methods_supported"] = new JsonArray("header")
            }.ToJsonString());
            return;
        }
        if (!requestPath.StartsWith(Path, StringComparison.Ordinal))
        {
            await next();
            return;
        }
        var metadata = origin + MetadataPath;
        var authorization = context.Request.Headers.Authorization.ToString();
        var principal = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? server.ValidateAccessToken(authorization["Bearer ".Length..].Trim(), Resource)
            : null;
        if (principal is null)
        {
            Refusals++;
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Bearer resource_metadata=\"" + metadata + "\""
                + (ChallengeScope is { } scope ? ", scope=\"" + scope + "\"" : string.Empty);
            return;
        }
        var granted = (principal.FindFirst("scope")?.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var missing = RequiredScopes.Where(scope => !granted.Contains(scope, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            Refusals++;
            context.Response.StatusCode = 403;
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"insufficient_scope\", scope=\""
                + string.Join(' ', granted.Union(RequiredScopes)) + "\", resource_metadata=\"" + metadata + "\"";
            return;
        }
        _requests.Enqueue(new NhAiTestResourceRequest(
            principal.FindFirst("sub")?.Value ?? string.Empty,
            principal.FindFirst("client_id")?.Value ?? string.Empty,
            string.Join(' ', granted)));
        await next();
    }
}

/// <summary>One authenticated request to an <see cref="NhAiTestProtectedResource"/>.</summary>
public sealed record NhAiTestResourceRequest(string Subject, string ClientId, string Scopes);
