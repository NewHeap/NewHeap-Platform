using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Models.MicrosoftAuth;

public class MicosoftAuthTokenSuccessResponse
{
    [JsonProperty("access_token")]
    public string AccessToken { get; set; }

    [JsonProperty("token_type")]
    public string TokenType { get; set; }

    [JsonProperty("expires_in")]
    public int ExpiresIn { get; set; }
    [JsonProperty("scope")]
    public string Scope { get; set; }

    [JsonProperty("refresh_token")]
    public string RefreshToken { get; set; }
    [JsonProperty("id_token")]
    public string IdToken { get; set; }

    [JsonIgnore]
    public TimeSpan Expires => TimeSpan.FromSeconds(ExpiresIn);
}
