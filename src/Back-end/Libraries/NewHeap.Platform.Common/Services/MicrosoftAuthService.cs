using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models.MicrosoftAuth;
using NewHeap.Platform.Common.Models.Options;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Web;

namespace NewHeap.Platform.Common.Services;

public class MicrosoftAuthService
{
    private const string GraphUserReadScope = "https://graph.microsoft.com/user.read";

    /// <summary>
    /// The scopes of a bound sign-in: the OpenID Connect scopes, which make the token response
    /// carry an ID token with the account's tenant and object id, and Graph's profile read.
    /// </summary>
    private const string SignInScopes = "openid profile " + GraphUserReadScope;

    private readonly HttpClient _httpClient;
    private readonly ILogger<MicrosoftAuthService> _logger;
    private readonly MicrosoftAuthSettings _settings;

    public MicrosoftAuthService(
        IOptions<MicrosoftAuthSettings> options,
        HttpClient httpClient,
        ILogger<MicrosoftAuthService> logger)
    {
        _settings = options.Value;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<MicrosoftAuthUser?> GetProfile(
        string token,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage requestMessage = new(HttpMethod.Get, _settings.ProfileEndpoint);

        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var result = await _httpClient.SendAsync(requestMessage, cancellationToken);
        if (result.IsSuccessStatusCode)
        {
            var user =
                JsonConvert.DeserializeObject<MicrosoftAuthUser>(
                    await result.Content.ReadAsStringAsync(cancellationToken));
            return user;
        }

        LogFailedRequest("profile", result.StatusCode);
        return null;
    }

    [Obsolete("Redeems the code without a PKCE code verifier. Use RedeemCodeAsync(code, codeVerifier) with the verifier of a sign-in started through GetLoginUrl(state, nonce, codeChallenge).")]
    public async Task<MicosoftAuthTokenSuccessResponse?> GetToken(
        string code,
        string? state = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://login.microsoftonline.com/{_settings.TenantId}/oauth2/v2.0/token";

        var query = HttpUtility.ParseQueryString(string.Empty);
        query.Add("client_id", _settings.ClientId);
        query.Add("scope", "https://graph.microsoft.com/user.read");
        query.Add("code", code);
        query.Add("redirect_uri", _settings.CallbackUrl);
        query.Add("grant_type", "authorization_code");
        query.Add("client_secret", _settings.ClientSecret);

        using var result = await _httpClient.PostAsync(
            url,
            new ReadOnlyMemoryContent(Encoding.UTF8.GetBytes(query.ToString()!)),
            cancellationToken);

        if (result.IsSuccessStatusCode)
        {
            var response =
                JsonConvert.DeserializeObject<MicosoftAuthTokenSuccessResponse>(
                    await result.Content.ReadAsStringAsync(cancellationToken));
            return response!;
        }

        LogFailedRequest("token", result.StatusCode);
        return null;
    }

    /// <summary>
    /// Redeems an authorization code of a sign-in started through
    /// <see cref="GetLoginUrl(string, string, string)"/>, with the PKCE code verifier whose
    /// challenge that request carried. Microsoft redeems a code only once and only with that
    /// verifier. The response carries the ID token of the account.
    /// </summary>
    public async Task<MicosoftAuthTokenSuccessResponse?> RedeemCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        var url = $"https://login.microsoftonline.com/{_settings.TenantId}/oauth2/v2.0/token";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _settings.ClientId,
            ["scope"] = SignInScopes,
            ["code"] = code,
            ["redirect_uri"] = _settings.CallbackUrl,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
            ["client_secret"] = _settings.ClientSecret,
        });

        using var result = await _httpClient.PostAsync(url, content, cancellationToken);
        if (!result.IsSuccessStatusCode)
        {
            LogFailedRequest("token", result.StatusCode);
            return null;
        }

        return JsonConvert.DeserializeObject<MicosoftAuthTokenSuccessResponse>(
            await result.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// The authorization URL of a sign-in that the caller bound to the browser. Microsoft returns
    /// <paramref name="state"/> unchanged to the callback, puts <paramref name="nonce"/> in the ID
    /// token, and issues a code that only the verifier of the S256
    /// <paramref name="codeChallenge"/> redeems (<see cref="RedeemCodeAsync"/>).
    /// </summary>
    public string GetLoginUrl(string state, string nonce, string codeChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);

        var query = HttpUtility.ParseQueryString(string.Empty);
        query.Add("client_id", _settings.ClientId);
        query.Add("scope", SignInScopes);
        query.Add("redirect_uri", _settings.CallbackUrl);
        query.Add("response_mode", "query");
        query.Add("response_type", "code");
        query.Add("prompt", "select_account");
        query.Add("state", state);
        query.Add("nonce", nonce);
        query.Add("code_challenge", codeChallenge);
        query.Add("code_challenge_method", "S256");

        return $"https://login.microsoftonline.com/{_settings.TenantId}/oauth2/v2.0/authorize?{query}";
    }

    [Obsolete("Builds an authorization request without a PKCE challenge or nonce. Use GetLoginUrl(state, nonce, codeChallenge) with a state bound to the browser.")]
    public string GetLoginUrl(string? state = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);

        query.Add("client_id", _settings.ClientId);
        query.Add("scope", "https://graph.microsoft.com/user.read");
        query.Add("redirect_uri", _settings.CallbackUrl);
        query.Add("response_mode", "query");
        query.Add("response_type", "code");
        query.Add("prompt", "select_account");

        if (!string.IsNullOrEmpty(state))
        {
            query.Add("state", state);
        }

        var url = $"https://login.microsoftonline.com/{_settings.TenantId}/oauth2/v2.0/authorize?{query}";
        return url;
    }

    private void LogFailedRequest(string operation, System.Net.HttpStatusCode statusCode)
    {
        if ((int)statusCode >= 500)
        {
            _logger.LogWarning(
                "Microsoft authentication {Operation} request failed with status code {StatusCode}.",
                operation,
                (int)statusCode);
            return;
        }

        _logger.LogDebug(
            "Microsoft authentication {Operation} request was rejected with status code {StatusCode}.",
            operation,
            (int)statusCode);
    }
}
