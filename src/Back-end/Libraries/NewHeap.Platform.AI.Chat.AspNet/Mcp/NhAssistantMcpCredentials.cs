using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The headers that authenticate requests to an MCP server. It is held only while requests are
/// built and is never stored, logged or audited.
/// </summary>
internal sealed class NhAssistantMcpCredential(IReadOnlyDictionary<string, string> headers)
{
    public static NhAssistantMcpCredential None { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;
}

/// <summary>
/// Whose credential a connection carries. In a person's request it is that person's own; in a
/// headless turn it is the person whose rights the run uses (<see cref="PersonGrantSource"/>), or
/// the organization's when there is no person.
/// </summary>
internal sealed record NhAssistantMcpCredentialSubject(
    bool IsHeadless,
    string? PersonActorId,
    NhAiGrantSource PersonGrantSource,
    string? TenantId)
{
    public static NhAssistantMcpCredentialSubject Request { get; } = new(false, null, NhAiGrantSource.None, null);
}

/// <summary>
/// Supplies the credential of one or more authentication modes. The client factory asks for it
/// before every HTTP request, so a credential can change while a connection is cached.
/// </summary>
internal interface INhAssistantMcpCredentialProvider
{
    bool Supports(string authMode);

    /// <summary>
    /// Whether <see cref="RenewAsync"/> can produce a new credential after the server answered
    /// <c>401</c>. The request is then repeated once.
    /// </summary>
    bool CanRenew { get; }

    ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken);

    ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether a governed call that the invocation gate authorized may carry this credential.
    /// </summary>
    TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call);
}

/// <summary>
/// The credential source of one connection: the provider of the server's mode, the server as it was
/// planned and the subject of the connection.
/// </summary>
internal sealed class NhAssistantMcpCredentialSource(
    INhAssistantMcpCredentialProvider provider,
    AssistantMcpServer server,
    NhAssistantMcpCredentialSubject subject)
{
    public string ServerId => server.Id;

    public bool CanRenew => provider.CanRenew;

    public ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(CancellationToken cancellationToken)
    {
        return provider.GetAsync(server, subject, cancellationToken);
    }

    public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(CancellationToken cancellationToken)
    {
        return provider.RenewAsync(server, subject, cancellationToken);
    }

    public TaskResult AuthorizeCall(NhAiMcpCallContext call)
    {
        return provider.AuthorizeCall(server, subject, call);
    }
}

/// <summary>
/// The secret of a server: the stored one, protected with Data Protection, or the value of its
/// reference (<see cref="NhAssistantMcpSecretReferences"/>), resolved for every request so that a
/// rotation applies at once. Values never reach a log.
/// </summary>
internal sealed partial class NhAssistantMcpSecretSource(
    NhAssistantMcpSecretProtector protector,
    IEnumerable<NhAssistantMcpSecretResolverRegistration> registrations,
    IServiceProvider services,
    ILogger<NhAssistantMcpSecretSource> logger)
{
    private readonly NhAssistantMcpSecretResolverRegistration[] _registrations = registrations.ToArray();

    public async ValueTask<TaskResult<string>> ResolveAsync(AssistantMcpServer server, CancellationToken cancellationToken)
    {
        if (server.SecretReference is { } reference)
        {
            return await ResolveReferenceAsync(reference, cancellationToken);
        }
        var secret = protector.Unprotect(server.ProtectedSecret);
        return secret is null
            ? Failed(NhAssistantAdminErrorCodes.McpUnauthorized)
            : TaskResult<string>.Succeeded(secret);
    }

    public async ValueTask<TaskResult<string>> ResolveReferenceAsync(string reference, CancellationToken cancellationToken)
    {
        if (!NhAssistantMcpSecretReferences.TryParse(reference, out var prefix, out var name))
        {
            return Failed(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid);
        }
        if (prefix == NhAssistantMcpSecretReferences.ConfigurationPrefix)
        {
            var configuration = services.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            var value = configuration?[NhAssistantMcpSecretReferences.ConfigurationSection + ":" + name];
            // A secret store that cannot resolve a ${...} placeholder leaves it as literal text: fail closed.
            return string.IsNullOrWhiteSpace(value) || IsUnresolvedReference(value)
                ? Failed(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid)
                : TaskResult<string>.Succeeded(value);
        }

        var registration = _registrations.FirstOrDefault(item => item.Prefix == prefix);
        if (registration is null)
        {
            return Failed(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid);
        }
        try
        {
            var resolver = (INhAssistantMcpSecretResolver)services.GetRequiredService(registration.ResolverType);
            var resolved = await resolver.ResolveAsync(name, cancellationToken);
            return resolved.Success && !string.IsNullOrEmpty(resolved.Data)
                ? TaskResult<string>.Succeeded(resolved.Data)
                : Failed(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogResolverFailed(logger, prefix, exception.GetType().Name);
            return Failed(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid);
        }
    }

    private static TaskResult<string> Failed(string code)
    {
        return TaskResult<string>.Failed(code, "The MCP secret is not available.");
    }

    /// <summary>
    /// Whether a configured value still contains a <c>${...}</c> reference that the configuration's
    /// secret store did not resolve.
    /// </summary>
    internal static bool IsUnresolvedReference(string value)
    {
        return UnresolvedReferencePattern().IsMatch(value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{[^{}]*\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UnresolvedReferencePattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The MCP secret resolver for prefix {Prefix} failed ({ExceptionType}).")]
    private static partial void LogResolverFailed(ILogger logger, string prefix, string exceptionType);
}

/// <summary>
/// The credential every caller shares: none, a bearer token, an API key header or basic
/// authentication, from the stored secret or its reference for each request.
/// </summary>
internal sealed class NhAssistantMcpStaticCredentialProvider(NhAssistantMcpSecretSource secrets) : INhAssistantMcpCredentialProvider
{
    public const string DefaultApiKeyHeader = "X-Api-Key";

    public bool CanRenew => false;

    public bool Supports(string authMode)
    {
        return NhAssistantMcpAuthModes.IsOrganizationCredential(authMode);
    }

    public async ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        if (server.AuthMode == NhAssistantMcpAuthModes.None)
        {
            return TaskResult<NhAssistantMcpCredential>.Succeeded(NhAssistantMcpCredential.None);
        }

        var secret = await secrets.ResolveAsync(server, cancellationToken);
        if (!secret.Success)
        {
            return NhAssistantMcpCredentialResults.Failed(NhAssistantMcpCredentialResults.FirstCode(secret) ?? NhAssistantAdminErrorCodes.McpUnauthorized);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (server.AuthMode)
        {
            case NhAssistantMcpAuthModes.Bearer:
                headers["Authorization"] = "Bearer " + secret.Data;
                break;
            case NhAssistantMcpAuthModes.Basic:
                // RFC 7617: user-id ":" password, UTF-8 and base64.
                headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes((server.UserName ?? string.Empty) + ":" + secret.Data));
                break;
            default:
                headers[string.IsNullOrWhiteSpace(server.HeaderName) ? DefaultApiKeyHeader : server.HeaderName] = secret.Data!;
                break;
        }
        return TaskResult<NhAssistantMcpCredential>.Succeeded(new NhAssistantMcpCredential(headers));
    }

    public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(NhAssistantMcpCredentialResults.Failed(NhAssistantAdminErrorCodes.McpUnauthorized));
    }

    public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
    {
        return TaskResult.Succeeded();
    }
}

/// <summary>
/// A person's NewHeap token, sent only to hosts in <see cref="NhAssistantMcpOptions.ForwardUserTokenHosts"/>.
/// In a person's request it is the caller's own bearer. In a headless turn it is a delegated token
/// minted for each request for the person the run acts for, after that person's consent (checked
/// before the connection is planned): short-lived, marked with the invocation, bound to the server's
/// origin, audited and refused when the host could accept it itself. A call that the gate authorized
/// for anyone but that person is refused.
/// </summary>
internal sealed partial class NhAssistantMcpForwardedTokenCredentialProvider(
    IOptionsMonitor<NhAssistantOptions> options,
    IServiceProvider services,
    ILogger<NhAssistantMcpForwardedTokenCredentialProvider> logger) : INhAssistantMcpCredentialProvider
{
    public const string IssuedResult = "issued";

    private static readonly TimeSpan ExpiryTolerance = TimeSpan.FromSeconds(30);

    public bool CanRenew => false;

    public bool Supports(string authMode)
    {
        return authMode == NhAssistantMcpAuthModes.ForwardUserToken;
    }

    public async ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedHost(server))
        {
            return NhAssistantMcpCredentialResults.Failed(NhAssistantAdminErrorCodes.McpHostBlocked);
        }
        if (subject.IsHeadless)
        {
            return await IssueDelegatedTokenAsync(server, subject, cancellationToken);
        }

        var accessor = services.GetService<INhAiCallerCredentialAccessor>();
        var token = accessor is null ? null : await accessor.GetBearerTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return NhAssistantMcpCredentialResults.Failed(NhAssistantAdminErrorCodes.McpUnauthorized);
        }
        return Bearer(token);
    }

    public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(NhAssistantMcpCredentialResults.Failed(NhAssistantAdminErrorCodes.McpUnauthorized));
    }

    public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
    {
        // A person's request: a chat call or a call with that person's delegated rights. A headless
        // turn: exactly the rights the token's person lends, never an agent grant or another person.
        var allowed = subject.IsHeadless
            ? call.Invocation.GrantSource == subject.PersonGrantSource
            : call.Invocation.GrantSource is NhAiGrantSource.None or NhAiGrantSource.Delegated;
        return allowed
            ? TaskResult.Succeeded()
            : TaskResult.Failed(
                NhAssistantAdminErrorCodes.McpCredentialMismatch,
                "The call was not authorized for the person whose token the connection carries.");
    }

    private async ValueTask<TaskResult<NhAssistantMcpCredential>> IssueDelegatedTokenAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        var call = NhAiMcpCallContext.Current is { } current && current.ServerId == server.Id ? current : null;
        // Startup validates the lifetime; a value changed at runtime is refused, never stretched.
        var lifetime = options.CurrentValue.Mcp.ForwardedTokenLifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > NhAiMcpDelegatedTokenDefaults.MaxLifetime)
        {
            LogLifetimeInvalid(logger, server.Id);
            return await RefuseAsync(server, subject, call, cancellationToken);
        }
        if (subject.PersonActorId is not { } person)
        {
            return await RefuseAsync(server, subject, call, cancellationToken);
        }
        if (!await services.IsDelegatedTokenRestrictionEnforcedAsync())
        {
            LogRestrictionMissing(logger, server.Id);
            return await RefuseAsync(server, subject, call, cancellationToken);
        }
        var issuer = services.GetService<INhAiMcpDelegatedTokenIssuer>();
        if (issuer is null)
        {
            return await RefuseAsync(server, subject, call, cancellationToken);
        }

        var marker = call?.Invocation.InvocationId.ToString("D") ?? "connection-" + Guid.NewGuid().ToString("N");
        var audience = NhAiMcpDelegatedTokenDefaults.AudienceOf(new Uri(server.Url));
        TaskResult<NhAiMcpDelegatedToken> issued;
        try
        {
            issued = await issuer.IssueAsync(
                new NhAiMcpDelegatedTokenRequest(person, audience, marker, lifetime)
                {
                    TenantId = subject.TenantId
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Issuers read identity data; their messages may carry it. Log the type only.
            LogIssuerFailed(logger, server.Id, exception.GetType().Name);
            return await RefuseAsync(server, subject, call, cancellationToken);
        }
        if (!issued.Success || issued.Data is null || !IsBound(issued.Data.BearerToken, audience, marker, lifetime))
        {
            return await RefuseAsync(server, subject, call, cancellationToken);
        }
        await AuditAsync(server, subject, call, IssuedResult, cancellationToken);
        return Bearer(issued.Data.BearerToken);
    }

    /// <summary>
    /// Whether the token is a JWT that carries exactly the requested marker and audience and expires
    /// within the requested lifetime. A token without them is never sent.
    /// </summary>
    internal static bool IsBound(string bearerToken, string audience, string marker, TimeSpan lifetime)
    {
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        if (string.IsNullOrWhiteSpace(bearerToken) || !handler.CanReadToken(bearerToken))
        {
            return false;
        }
        System.IdentityModel.Tokens.Jwt.JwtSecurityToken token;
        try
        {
            token = handler.ReadJwtToken(bearerToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
        var markers = token.Claims.Where(claim => claim.Type == NhAiMcpDelegatedTokenDefaults.MarkerClaimType).Select(claim => claim.Value).ToArray();
        var audiences = token.Claims.Where(claim => claim.Type == NhAiMcpDelegatedTokenDefaults.AudienceClaimType).Select(claim => claim.Value).ToArray();
        return markers.Length == 1
            && string.Equals(markers[0], marker, StringComparison.Ordinal)
            && audiences.Length == 1
            && string.Equals(audiences[0], audience, StringComparison.Ordinal)
            && token.Payload.Expiration is not null
            && token.ValidTo <= DateTime.UtcNow + lifetime + ExpiryTolerance;
    }

    private async ValueTask<TaskResult<NhAssistantMcpCredential>> RefuseAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        NhAiMcpCallContext? call,
        CancellationToken cancellationToken)
    {
        await AuditAsync(server, subject, call, NhAssistantAdminErrorCodes.McpDelegatedTokenUnavailable, cancellationToken);
        return NhAssistantMcpCredentialResults.Failed(NhAssistantAdminErrorCodes.McpDelegatedTokenUnavailable);
    }

    private async Task AuditAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        NhAiMcpCallContext? call,
        string resultCode,
        CancellationToken cancellationToken)
    {
        var turn = NhAssistantExecutionScope.CurrentTurn;
        var evt = new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.McpDelegatedTokenIssued,
            turn?.ConversationId ?? Guid.Empty,
            turn?.TurnId ?? Guid.Empty,
            subject.PersonActorId ?? string.Empty,
            turn?.Agent.Id ?? string.Empty,
            turn?.Agent.Version ?? 0,
            DateTimeOffset.UtcNow)
        {
            ObjectId = server.Id,
            ToolId = call?.Descriptor.Id,
            ToolVersion = call?.Descriptor.Version,
            InvocationId = call?.Invocation.InvocationId,
            ResultCode = resultCode
        };
        foreach (var sink in services.GetServices<INhAssistantBusinessAuditSink>())
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }

    private static TaskResult<NhAssistantMcpCredential> Bearer(string token)
    {
        return TaskResult<NhAssistantMcpCredential>.Succeeded(new NhAssistantMcpCredential(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + token
            }));
    }

    private bool IsAllowedHost(AssistantMcpServer server)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint))
        {
            return false;
        }
        var host = endpoint.IdnHost.TrimEnd('.').ToLowerInvariant();
        return options.CurrentValue.Mcp.ForwardUserTokenHosts.Any(allowed =>
            string.Equals(allowed.Trim().TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No delegated token is minted for MCP server {ServerId}: a JWT bearer scheme of the host does not restrict delegated tokens.")]
    private static partial void LogRestrictionMissing(ILogger logger, string serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No delegated token is minted for MCP server {ServerId}: the forwarded token lifetime is not longer than zero and at most 5 minutes.")]
    private static partial void LogLifetimeInvalid(ILogger logger, string serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No delegated token is minted for MCP server {ServerId}: the issuer failed ({ExceptionType}).")]
    private static partial void LogIssuerFailed(ILogger logger, string serverId, string exceptionType);
}

internal static class NhAssistantMcpCredentialResults
{
    public static TaskResult<NhAssistantMcpCredential> Failed(string code)
    {
        return TaskResult<NhAssistantMcpCredential>.Failed(code, "The MCP server cannot be contacted with this configuration.");
    }

    public static string? FirstCode(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }
}

/// <summary>
/// Authenticates every request of an MCP client: it checks the request target, the person a
/// governed call was authorized for and the credential, renews a renewable credential once after
/// <c>401</c> and turns <c>403 insufficient_scope</c> into its own code. Credentials never reach a
/// log; failures carry stable codes only.
/// </summary>
internal sealed class NhAssistantMcpCredentialHandler(
    NhAssistantMcpCredentialSource credentials,
    Uri endpoint,
    NhAssistantMcpHostGuard guard) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (guard.CheckRequest(request.RequestUri, endpoint) is { } blocked)
        {
            throw new NhAssistantMcpException(blocked, "The MCP request target is not allowed.");
        }
        if (NhAiMcpCallContext.Current is { } call
            && string.Equals(call.ServerId, credentials.ServerId, StringComparison.Ordinal)
            && credentials.AuthorizeCall(call) is { Success: false } refused)
        {
            throw new NhAssistantMcpException(
                NhAssistantMcpCredentialResults.FirstCode(refused) ?? NhAssistantAdminErrorCodes.McpCredentialMismatch,
                "The MCP call may not carry this credential.");
        }

        var credential = await credentials.GetAsync(cancellationToken);
        if (!credential.Success)
        {
            throw new NhAssistantMcpException(
                NhAssistantMcpCredentialResults.FirstCode(credential) ?? NhAssistantAdminErrorCodes.McpUnauthorized,
                "The MCP credential is not available.");
        }
        Apply(request, credential.Data!);

        if (!credentials.CanRenew)
        {
            return Inspect(await base.SendAsync(request, cancellationToken));
        }

        // The body is read again for the one repeat after a renewal.
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return Inspect(response);
        }

        var renewed = await credentials.RenewAsync(cancellationToken);
        if (!renewed.Success)
        {
            return response;
        }
        response.Dispose();
        using var repeat = await CloneAsync(request, cancellationToken);
        Apply(repeat, renewed.Data!);
        return Inspect(await base.SendAsync(repeat, cancellationToken));
    }

    private static void Apply(HttpRequestMessage request, NhAssistantMcpCredential credential)
    {
        foreach (var (name, value) in credential.Headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static HttpResponseMessage Inspect(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden && IsInsufficientScope(response.Headers.WwwAuthenticate))
        {
            response.Dispose();
            throw new NhAssistantMcpException(
                NhAssistantAdminErrorCodes.McpInsufficientScope,
                "The MCP credential lacks a permission the request needs.");
        }
        return response;
    }

    /// <summary>
    /// Whether a <c>Bearer</c> challenge names <c>error="insufficient_scope"</c> (RFC 6750).
    /// </summary>
    internal static bool IsInsufficientScope(HttpHeaderValueCollection<AuthenticationHeaderValue> challenges)
    {
        foreach (var challenge in challenges)
        {
            if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || challenge.Parameter is null)
            {
                continue;
            }
            var parameters = challenge.Parameter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var parameter in parameters)
            {
                var separator = parameter.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                var name = parameter[..separator].Trim();
                var value = parameter[(separator + 1)..].Trim().Trim('"');
                if (string.Equals(name, "error", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(value, "insufficient_scope", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(body);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return clone;
    }
}
