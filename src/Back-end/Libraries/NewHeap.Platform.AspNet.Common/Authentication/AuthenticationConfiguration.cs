namespace NewHeap.Platform.AspNet.Common.Authentication;

public class AuthenticationConfiguration
{
    public bool RefreshTokenEnabled { get; init; } = true;
    public bool DivisionsEnabled { get; init; } = false;

    public string? CookieName { get; init; }
    public string? RefreshCookieName { get; init; }
    public string? RefreshTokenEndpoint { get; init; }
    public string? AuthenticationEndpoint { get; init; }
    public string? AccountInformationEndpoint { get; init; }
    public string? LogoutEndpoint { get; init; }

    public string? AuthenticationServiceKey { get; set; }
    
    internal AuthenticationConfiguration()
    {
        
    }
}