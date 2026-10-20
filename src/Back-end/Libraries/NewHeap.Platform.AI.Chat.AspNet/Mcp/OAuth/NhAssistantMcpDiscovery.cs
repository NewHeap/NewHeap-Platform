using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Authorization server metadata (RFC 8414 or OpenID Connect discovery) as the assistant uses it.
/// Every endpoint passed the host guard when the metadata was read.
/// </summary>
internal sealed record NhAssistantMcpAuthorizationServerMetadata(
    string Issuer,
    Uri? AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri? RegistrationEndpoint,
    Uri? RevocationEndpoint,
    IReadOnlyList<string> CodeChallengeMethods,
    IReadOnlyList<string> TokenEndpointAuthMethods,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> ScopesSupported,
    bool ClientIdMetadataDocumentSupported,
    bool IssuerParameterSupported)
{
    public bool SupportsS256 => CodeChallengeMethods.Contains("S256", StringComparer.Ordinal);

    public bool SupportsAuthMethod(string method)
    {
        // RFC 8414: without the field, client_secret_basic is the default.
        return TokenEndpointAuthMethods.Count == 0
            ? method == "client_secret_basic"
            : TokenEndpointAuthMethods.Contains(method, StringComparer.Ordinal);
    }
}

/// <summary>
/// The result of discovery for an MCP server URL: its protected resource, the chosen authorization
/// server and the scopes the challenge and the metadata name.
/// </summary>
internal sealed record NhAssistantMcpDiscoveryResult(
    string Resource,
    IReadOnlyList<string> AuthorizationServers,
    IReadOnlyList<string> ChallengedScopes,
    IReadOnlyList<string> ResourceScopes,
    NhAssistantMcpAuthorizationServerMetadata AuthorizationServer);

/// <summary>
/// A <c>WWW-Authenticate: Bearer</c> challenge (RFC 6750, RFC 9728) with which an MCP server refused a
/// request: the status (<c>401</c>, or <c>403</c> with <c>insufficient_scope</c>), the error, the
/// scopes it asked for and its protected resource metadata.
/// </summary>
public sealed record NhAssistantMcpChallenge(int Status, string? Error, string? Scope, string? ResourceMetadata)
{
    internal static NhAssistantMcpChallenge Parse(int status, HttpHeaderValueCollection<AuthenticationHeaderValue> challenges)
    {
        foreach (var challenge in challenges)
        {
            if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var parameters = ParseParameters(challenge.Parameter);
            parameters.TryGetValue("error", out var error);
            parameters.TryGetValue("scope", out var scope);
            parameters.TryGetValue("resource_metadata", out var metadata);
            return new NhAssistantMcpChallenge(status, error, scope, metadata);
        }
        return new NhAssistantMcpChallenge(status, null, null, null);
    }

    /// <summary>The scopes of <see cref="Scope"/>.</summary>
    public IReadOnlyList<string> Scopes => NhAssistantMcpScopes.Split(Scope);

    private static Dictionary<string, string> ParseParameters(string? parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return result;
        }
        var index = 0;
        while (index < parameter.Length)
        {
            while (index < parameter.Length && (parameter[index] == ',' || char.IsWhiteSpace(parameter[index])))
            {
                index++;
            }
            var equals = parameter.IndexOf('=', index);
            if (equals < 0)
            {
                break;
            }
            var name = parameter[index..equals].Trim();
            index = equals + 1;
            string value;
            if (index < parameter.Length && parameter[index] == '"')
            {
                var builder = new System.Text.StringBuilder();
                index++;
                while (index < parameter.Length && parameter[index] != '"')
                {
                    if (parameter[index] == '\\' && index + 1 < parameter.Length)
                    {
                        index++;
                    }
                    builder.Append(parameter[index]);
                    index++;
                }
                index++;
                value = builder.ToString();
            }
            else
            {
                var end = parameter.IndexOf(',', index);
                value = (end < 0 ? parameter[index..] : parameter[index..end]).Trim();
                index = end < 0 ? parameter.Length : end;
            }
            result[name] = value;
        }
        return result;
    }
}

/// <summary>
/// Scope strings: space-separated scope tokens (RFC 6749).
/// </summary>
internal static class NhAssistantMcpScopes
{
    public const int MaxLength = 1_024;

    public static IReadOnlyList<string> Split(string? scopes)
    {
        return string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string? Join(IEnumerable<string> scopes)
    {
        var joined = string.Join(' ', scopes.Distinct(StringComparer.Ordinal));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Whether every token is a valid scope token: printable ASCII without space, quote or backslash.</summary>
    public static bool IsValid(string? scopes)
    {
        return scopes is null
            || (scopes.Length <= MaxLength
                && Split(scopes).All(token => token.All(character => character >= 0x21 && character <= 0x7E && character != '"' && character != '\\')));
    }
}

/// <summary>
/// Discovery for OAuth MCP servers (2026-07-28 authorization): the protected resource metadata from
/// the challenge or the well-known URIs, its <c>resource</c> checked against the server's canonical
/// URI, then the authorization server metadata in the specified order with the issuer checked. The
/// metadata of an issuer is cached for ten minutes.
/// </summary>
internal sealed class NhAssistantMcpDiscovery(NhAssistantMcpOAuthHttp http)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (NhAssistantMcpAuthorizationServerMetadata Metadata, DateTimeOffset ExpiresAt)> _issuers = new(StringComparer.Ordinal);

    /// <summary>
    /// The canonical URI of a resource (RFC 8707, 2026-07-28): lowercase scheme and host, the port only
    /// when it is not the default, the path without a trailing slash, the query, no fragment.
    /// </summary>
    public static string Canonical(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var authority = uri.Scheme.ToLowerInvariant() + "://" + uri.IdnHost.TrimEnd('.').ToLowerInvariant()
            + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var path = uri.AbsolutePath.TrimEnd('/');
        return authority + path + uri.Query;
    }

    public async Task<TaskResult<NhAssistantMcpDiscoveryResult>> DiscoverAsync(
        string serverUrl,
        string? pinnedIssuer,
        CancellationToken cancellationToken)
    {
        var resource = Canonical(serverUrl);
        var endpoint = new Uri(serverUrl);
        var probe = await http.ProbeAsync(endpoint, cancellationToken);
        if (!probe.Success)
        {
            return TaskResult<NhAssistantMcpDiscoveryResult>.Failed(probe);
        }
        var challenge = NhAssistantMcpChallenge.Parse((int)probe.Data!.Status, probe.Data.Headers.WwwAuthenticate);
        var candidates = new List<Uri>();
        if (challenge.ResourceMetadata is { } challenged && Uri.TryCreate(challenged, UriKind.Absolute, out var challengedUri))
        {
            candidates.Add(challengedUri);
        }
        var origin = new Uri(endpoint.GetLeftPart(UriPartial.Authority));
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.Length > 0)
        {
            candidates.Add(new Uri(origin, "/.well-known/oauth-protected-resource" + path));
        }
        candidates.Add(new Uri(origin, "/.well-known/oauth-protected-resource"));

        var metadata = await FetchResourceMetadataAsync(candidates, resource, cancellationToken);
        if (!metadata.Success)
        {
            return TaskResult<NhAssistantMcpDiscoveryResult>.Failed(metadata);
        }
        var (authorizationServers, resourceScopes) = metadata.Data;
        var issuer = pinnedIssuer is not null && authorizationServers.Contains(pinnedIssuer, StringComparer.Ordinal)
            ? pinnedIssuer
            : authorizationServers[0];
        var server = await GetAuthorizationServerAsync(issuer, bypassCache: true, cancellationToken);
        if (!server.Success)
        {
            return TaskResult<NhAssistantMcpDiscoveryResult>.Failed(server);
        }
        return TaskResult<NhAssistantMcpDiscoveryResult>.Succeeded(new NhAssistantMcpDiscoveryResult(
            resource,
            authorizationServers,
            challenge.Scopes,
            resourceScopes,
            server.Data!));
    }

    /// <summary>
    /// The authorization servers a protected resource metadata document names, after checking its
    /// <c>resource</c>; used again when a challenge points to new metadata.
    /// </summary>
    public async Task<TaskResult<(IReadOnlyList<string> AuthorizationServers, IReadOnlyList<string> Scopes)>> FetchResourceMetadataAsync(
        IReadOnlyList<Uri> candidates,
        string resource,
        CancellationToken cancellationToken)
    {
        string? blocked = null;
        foreach (var candidate in candidates)
        {
            var response = await http.GetAsync(candidate, cancellationToken);
            if (!response.Success)
            {
                blocked ??= BlockedCode(response);
                continue;
            }
            if (response.Data!.Status != HttpStatusCode.OK || response.Data.Body is not { ValueKind: JsonValueKind.Object } body)
            {
                continue;
            }
            var named = body.TryGetProperty("resource", out var resourceElement) && resourceElement.ValueKind == JsonValueKind.String
                ? resourceElement.GetString()
                : null;
            if (named is null || !Uri.TryCreate(named, UriKind.Absolute, out _) || !string.Equals(Canonical(named), resource, StringComparison.Ordinal))
            {
                return Failed<(IReadOnlyList<string>, IReadOnlyList<string>)>(NhAssistantAdminErrorCodes.McpDiscoveryFailed);
            }
            var servers = Strings(body, "authorization_servers");
            if (servers.Count == 0)
            {
                return Failed<(IReadOnlyList<string>, IReadOnlyList<string>)>(NhAssistantAdminErrorCodes.McpDiscoveryFailed);
            }
            return TaskResult<(IReadOnlyList<string>, IReadOnlyList<string>)>.Succeeded((servers, Strings(body, "scopes_supported")));
        }
        return Failed<(IReadOnlyList<string>, IReadOnlyList<string>)>(blocked ?? NhAssistantAdminErrorCodes.McpDiscoveryFailed);
    }

    /// <summary>
    /// The metadata of an issuer, from the well-known URIs in the order the specification gives:
    /// OAuth authorization server metadata, then OpenID Connect discovery. The document's
    /// <c>issuer</c> must equal the issuer exactly and every endpoint must pass the host guard.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpAuthorizationServerMetadata>> GetAuthorizationServerAsync(
        string issuer,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        if (!bypassCache && _issuers.TryGetValue(issuer, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return TaskResult<NhAssistantMcpAuthorizationServerMetadata>.Succeeded(cached.Metadata);
        }
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Query.Length > 0 || issuerUri.Fragment.Length > 0)
        {
            return Failed<NhAssistantMcpAuthorizationServerMetadata>(NhAssistantAdminErrorCodes.McpDiscoveryFailed);
        }
        var origin = issuerUri.GetLeftPart(UriPartial.Authority);
        var path = issuerUri.AbsolutePath.TrimEnd('/');
        string[] candidates = path.Length == 0
            ? [origin + "/.well-known/oauth-authorization-server", origin + "/.well-known/openid-configuration"]
            : [
                origin + "/.well-known/oauth-authorization-server" + path,
                origin + "/.well-known/openid-configuration" + path,
                origin + path + "/.well-known/openid-configuration"
            ];
        string? blocked = null;
        foreach (var candidate in candidates)
        {
            var response = await http.GetAsync(new Uri(candidate), cancellationToken);
            if (!response.Success)
            {
                blocked ??= BlockedCode(response);
                continue;
            }
            if (response.Data!.Status != HttpStatusCode.OK || response.Data.Body is not { ValueKind: JsonValueKind.Object } body)
            {
                continue;
            }
            if (!body.TryGetProperty("issuer", out var named) || named.ValueKind != JsonValueKind.String || named.GetString() != issuer)
            {
                // Mix-up protection: metadata of another issuer is never used.
                return Failed<NhAssistantMcpAuthorizationServerMetadata>(NhAssistantAdminErrorCodes.McpDiscoveryFailed);
            }
            var metadata = Read(body, issuer);
            if (metadata is null)
            {
                return Failed<NhAssistantMcpAuthorizationServerMetadata>(NhAssistantAdminErrorCodes.McpDiscoveryFailed);
            }
            _issuers[issuer] = (metadata, DateTimeOffset.UtcNow + CacheDuration);
            return TaskResult<NhAssistantMcpAuthorizationServerMetadata>.Succeeded(metadata);
        }
        return Failed<NhAssistantMcpAuthorizationServerMetadata>(blocked ?? NhAssistantAdminErrorCodes.McpDiscoveryFailed);
    }

    private NhAssistantMcpAuthorizationServerMetadata? Read(JsonElement body, string issuer)
    {
        var token = Endpoint(body, "token_endpoint");
        if (token is null)
        {
            return null;
        }
        var authorization = Endpoint(body, "authorization_endpoint");
        var registration = Endpoint(body, "registration_endpoint");
        var revocation = Endpoint(body, "revocation_endpoint");
        if (token == Invalid || authorization == Invalid || registration == Invalid || revocation == Invalid)
        {
            return null;
        }
        return new NhAssistantMcpAuthorizationServerMetadata(
            issuer,
            authorization,
            token,
            registration,
            revocation,
            Strings(body, "code_challenge_methods_supported"),
            Strings(body, "token_endpoint_auth_methods_supported"),
            Strings(body, "grant_types_supported"),
            Strings(body, "scopes_supported"),
            Bool(body, "client_id_metadata_document_supported"),
            Bool(body, "authorization_response_iss_parameter_supported"));
    }

    private static readonly Uri Invalid = new("invalid:endpoint");

    /// <summary>The endpoint, null when absent, or <see cref="Invalid"/> when it fails the guard.</summary>
    private Uri? Endpoint(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && http.IsAllowed(uri) ? uri : Invalid;
    }

    private static IReadOnlyList<string> Strings(JsonElement body, string name)
    {
        return body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
    }

    private static bool Bool(JsonElement body, string name)
    {
        return body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// The host guard's code when a candidate URL was blocked, so discovery reports it instead of a
    /// general failure; null for another failure.
    /// </summary>
    private static string? BlockedCode(TaskResult result)
    {
        return NhAssistantResults.CodeOf(result) == NhAssistantAdminErrorCodes.McpHostBlocked
            ? NhAssistantAdminErrorCodes.McpHostBlocked
            : null;
    }

    private static TaskResult<T> Failed<T>(string code)
    {
        return TaskResult<T>.Failed(code, "OAuth discovery failed.");
    }
}
