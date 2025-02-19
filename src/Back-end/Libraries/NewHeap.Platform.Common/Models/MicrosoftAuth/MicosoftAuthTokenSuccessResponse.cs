using Newtonsoft.Json;

namespace NewHeap.Platform.Common.Models.MicrosoftAuth;

public class MicosoftAuthTokenSuccessResponse
{
    [JsonProperty("access_token")]
    public string AccessToken { get; set; } = null!;

    [JsonProperty("token_type")]
    public string TokenType { get; set; } = null!;

    [JsonProperty("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonProperty("scope")]
    public string Scope { get; set; } = null!;

    [JsonProperty("refresh_token")]
    public string RefreshToken { get; set; } = null!;

    [JsonProperty("id_token")]
    public string IdToken { get; set; } = null!;

    [JsonIgnore]
    public TimeSpan Expires => TimeSpan.FromSeconds(ExpiresIn);
}