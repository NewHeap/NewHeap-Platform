using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-251: an authentication mode of the sample's own (D1), registered with
/// <c>AddMcpCredentialProvider</c> as <see cref="Mode"/> for a person's credential. It exchanges the
/// person's NewHeap token at the sample's token service (RFC 8693) for a short-lived token of the MCP
/// server, so the person's own token never reaches the server. The token service is
/// <see cref="EndpointKey"/> with the client <see cref="ClientIdKey"/>; its secret lives in the secrets
/// file (<see cref="ClientSecretKey"/>, read with <see cref="NhConfigurationSecrets.Read"/>). A
/// background run carries nobody's token, so the mode serves only a person's own requests. Tokens
/// never reach a log.
/// </summary>
public sealed partial class SampleTokenExchangeCredentialProvider(
    IConfiguration configuration,
    IHttpClientFactory httpClients,
    IServiceScopeFactory scopes,
    ILogger<SampleTokenExchangeCredentialProvider> logger) : INhAssistantMcpCredentialProvider
{
    public const string Mode = "sample-token-exchange";
    public const string HttpClientName = "sample-token-exchange";
    public const string EndpointKey = "SampleProjectManagement:Assistant:TokenExchange:Endpoint";
    public const string ClientIdKey = "SampleProjectManagement:Assistant:TokenExchange:ClientId";
    public const string ClientSecretKey = "SampleProjectManagement:Assistant:TokenExchange:ClientSecret";
    private const string TokenType = "urn:ietf:params:oauth:token-type:access_token";

    public bool CanRenew => true;

    public bool Supports(string authMode)
    {
        return authMode == Mode;
    }

    public async ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        if (subject.IsHeadless)
        {
            // Nobody's own token is present in a background run.
            return NhAssistantMcpCredentialProviders.Failed(NhAssistantAdminErrorCodes.McpUnauthorized);
        }

        string? subjectToken;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var caller = scope.ServiceProvider.GetService<INhAiCallerCredentialAccessor>();
            subjectToken = caller is null ? null : await caller.GetBearerTokenAsync(cancellationToken);
        }
        var endpoint = configuration[EndpointKey];
        var clientId = configuration[ClientIdKey];
        var clientSecret = NhConfigurationSecrets.Read(configuration, ClientSecretKey);
        if (string.IsNullOrWhiteSpace(subjectToken)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var tokenEndpoint)
            || string.IsNullOrWhiteSpace(clientId)
            || clientSecret is null)
        {
            return NhAssistantMcpCredentialProviders.Failed(NhAssistantAdminErrorCodes.McpUnauthorized);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["subject_token"] = subjectToken,
                ["subject_token_type"] = TokenType,
                ["requested_token_type"] = TokenType,
                ["resource"] = server.Url
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(clientSecret))));
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogRefused(logger, server.Id, (int)response.StatusCode);
                return NhAssistantMcpCredentialProviders.Failed(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } accessToken)
            {
                LogRefused(logger, server.Id, (int)response.StatusCode);
                return NhAssistantMcpCredentialProviders.Failed(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
            }
            var expiresAt = document.RootElement.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds)
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : (DateTimeOffset?)null;
            return TaskResult<NhAssistantMcpCredential>.Succeeded(NhAssistantMcpCredential.Bearer(accessToken, expiresAt));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The token service failed, not the person's request: the next request may succeed.
            LogFailed(logger, server.Id, exception);
            return NhAssistantMcpCredentialProviders.Failed(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
        }
    }

    public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        NhAssistantMcpChallenge challenge,
        CancellationToken cancellationToken)
    {
        // The exchange is cheap and holds no state: a refused token is exchanged again once.
        return GetAsync(server, subject, cancellationToken);
    }

    public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
    {
        // The token is the person's: only a call with that person's own rights may carry it.
        return NhAssistantMcpCredentialProviders.AuthorizePersonalCall(subject, call);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The token service refused to exchange a token for MCP server {ServerId} ({Status}).")]
    private static partial void LogRefused(ILogger logger, string serverId, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The token exchange for MCP server {ServerId} failed.")]
    private static partial void LogFailed(ILogger logger, string serverId, Exception exception);
}
