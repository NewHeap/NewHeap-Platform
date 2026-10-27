namespace NewHeap.Platform.Common.Models.Options;

public class MicrosoftAuthSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string ProfileEndpoint { get; set; } = "";
    public string[] AuthDomains { get; set; } = [];

    /// <summary>
    /// The absolute origins, such as <c>https://app.example.test</c>, that the Microsoft OAuth
    /// callback may return the browser to, besides paths on the callback's own host. When it is
    /// empty, the callback uses <c>NewHeapAspNetCommonSettings.AllowedOrigins</c>, the origins the
    /// application already allows to call the API.
    /// </summary>
    public string[] AllowedReturnOrigins { get; set; } = [];
}