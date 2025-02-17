using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models.MicrosoftAuth;
using NewHeap.Platform.Common.Models.Options;
using Newtonsoft.Json;

namespace NewHeap.Platform.Common.Services;
public class MicrosoftAuthService
{
    private readonly MicrosoftAuthSettings _settings;
    private static readonly HttpClient _httpClient = new HttpClient();

    public MicrosoftAuthService(IOptions<MicrosoftAuthSettings> options)
    {
        _settings = options.Value;
    }

    public async Task<MicrosoftAuthUser> GetProfile(string token)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, _settings.ProfileEndpoint);

        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var result = await _httpClient.SendAsync(requestMessage);
        if (result.IsSuccessStatusCode)
        {
            var user = JsonConvert.DeserializeObject<MicrosoftAuthUser>(await result.Content.ReadAsStringAsync());
            return user;
        }

        return null;
    }

    public async Task<MicosoftAuthTokenSuccessResponse> GetToken(string code, string state = null)
    {
        var url = $"https://login.microsoftonline.com/{_settings.TenantId}/oauth2/v2.0/token";

        var query = HttpUtility.ParseQueryString(string.Empty);
        query.Add("client_id", _settings.ClientId);
        query.Add("scope", "https://graph.microsoft.com/user.read");
        query.Add("code", code);
        query.Add("redirect_uri", _settings.CallbackUrl);
        query.Add("grant_type", "authorization_code");
        query.Add("client_secret", _settings.ClientSecret);

        var result = await _httpClient.PostAsync(url, new ReadOnlyMemoryContent(Encoding.UTF8.GetBytes(query.ToString())));

        if (result.IsSuccessStatusCode)
        {
            var response = JsonConvert.DeserializeObject<MicosoftAuthTokenSuccessResponse>(await result.Content.ReadAsStringAsync());
            return response;
        }
        else
        {
            var response = await result.Content.ReadAsStringAsync();
            var resonseStatus = result.StatusCode;
            return null;
        }
    }

    public string GetLoginUrl(string state = null)
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
}