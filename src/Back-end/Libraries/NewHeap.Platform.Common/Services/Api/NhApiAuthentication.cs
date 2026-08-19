using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NewHeap.Platform.Common.Services.Api;

/// <summary>
/// Supplies bearer tokens for one logical target API.
/// </summary>
public interface INhApiAccessTokenProvider<TApi>
    where TApi : class
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    void InvalidateAccessToken()
    {
    }
}

/// <summary>
/// Obtains and caches a token from a NewHeap username/password authentication endpoint.
/// </summary>
internal sealed class NhUsernamePasswordApiAccessTokenProvider<TApi> : INhApiAccessTokenProvider<TApi>, IDisposable
    where TApi : class
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NhApiClientOptions _clientOptions;
    private readonly NhApiUsernamePasswordAuthenticationOptions _authenticationOptions;
    private readonly ILogger<NhUsernamePasswordApiAccessTokenProvider<TApi>> _logger;
    private readonly SemaphoreSlim _authenticationLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset? _expiresAt;

    public NhUsernamePasswordApiAccessTokenProvider(
        IHttpClientFactory httpClientFactory,
        NhApiClientRegistration<TApi> registration,
        ILogger<NhUsernamePasswordApiAccessTokenProvider<TApi>> logger)
    {
        _httpClientFactory = httpClientFactory;
        _clientOptions = registration.Options;
        _authenticationOptions = registration.Options.Authentication
            ?? throw new InvalidOperationException(
                $"No username/password authentication is configured for {typeof(TApi).FullName}.");
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (HasValidAccessToken())
        {
            return _accessToken;
        }

        await _authenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasValidAccessToken())
            {
                return _accessToken;
            }

            InvalidateAccessToken();

            using var request = new HttpRequestMessage(HttpMethod.Post, _authenticationOptions.Endpoint)
            {
                Content = JsonContent.Create(
                    new AuthenticationRequest(
                        _authenticationOptions.Username,
                        _authenticationOptions.Password,
                        _authenticationOptions.Realm),
                    options: _clientOptions.JsonSerializerOptions)
            };

            using var httpClient = _httpClientFactory.CreateClient(
                NhApiClientNames.GetAuthenticationClientName<TApi>());
            using var response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Authentication for API client {ApiClient} failed with status code {StatusCode}.",
                    typeof(TApi).FullName,
                    response.StatusCode);
                return null;
            }

            var tokenResponse = await response.Content
                .ReadFromJsonAsync<AuthenticationResponse>(
                    _clientOptions.JsonSerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(tokenResponse?.Token) || tokenResponse.ValidTo == default)
            {
                _logger.LogWarning(
                    "Authentication for API client {ApiClient} returned an invalid token response.",
                    typeof(TApi).FullName);
                return null;
            }

            _accessToken = tokenResponse.Token;
            _expiresAt = tokenResponse.ValidTo;
            return _accessToken;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Authentication for API client {ApiClient} failed.",
                typeof(TApi).FullName);
            return null;
        }
        finally
        {
            _authenticationLock.Release();
        }
    }

    public void InvalidateAccessToken()
    {
        _accessToken = null;
        _expiresAt = null;
    }

    private bool HasValidAccessToken()
    {
        return !string.IsNullOrWhiteSpace(_accessToken)
            && _expiresAt.HasValue
            && DateTimeOffset.UtcNow <
                _expiresAt.Value.Subtract(_authenticationOptions.RefreshBeforeExpiration);
    }

    public void Dispose()
    {
        _authenticationLock.Dispose();
    }

    private sealed record AuthenticationRequest(string Username, string Password, string Realm);

    private sealed class AuthenticationResponse
    {
        public string? Token { get; set; }

        public DateTimeOffset ValidTo { get; set; }
    }
}

internal sealed class NhApiAuthenticationDelegatingHandler<TApi> : DelegatingHandler
    where TApi : class
{
    private readonly INhApiAccessTokenProvider<TApi> _accessTokenProvider;

    public NhApiAuthenticationDelegatingHandler(INhApiAccessTokenProvider<TApi> accessTokenProvider)
    {
        _accessTokenProvider = accessTokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokenProvider
            .GetAccessTokenAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new
                {
                    errors = new[] { "Failed to authenticate with the target API." }
                })
            };
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _accessTokenProvider.InvalidateAccessToken();
        }

        return response;
    }
}
