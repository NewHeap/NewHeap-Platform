namespace NewHeap.Platform.Common.Models.Options;

public class MicrosoftAuthSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string ProfileEndpoint { get; set; } = "";
    public string[] AuthDomains { get; set; } = [];
}