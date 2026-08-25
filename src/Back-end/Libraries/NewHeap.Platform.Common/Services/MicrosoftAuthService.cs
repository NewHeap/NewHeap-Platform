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
