using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class AuthenticationConfiguration
{
    public bool RefreshTokenEnabled { get; init; } = true;
    public bool DivisionsEnabled { get; init; } = false;
    public bool ImpersonateEnabled { get; init; } = false;

    public string? CookieName { get; init; } = "nh_access_token";
    public string? RefreshCookieName { get; init; } = "nh_refresh_token";
    public string? RefreshTokenEndpoint { get; init; }
    public string? AuthenticationEndpoint { get; init; }
    public string? AccountInformationEndpoint { get; init; }
    public string? LogoutEndpoint { get; init; }
    public string? ImpersonateEndpoint { get; init; }
    public string? RevertImpersonateEndpoint { get; init; }

    public string? AuthenticationServiceKey { get; set; }

    public List<Claim> AuthenticateRequiredClaims { get; set; } = new List<Claim>();

    public TimeSpan ExpirationTimespanRefreshToken { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan ExpirationTimespanToken { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long an impersonation access token is valid. The impersonation session has no refresh
    /// token, so it ends when this token expires unless the origin user reverts it first.
    /// Defaults to one day; set it through
    /// <c>UserNamePasswordOptions.ExpirationTimespanImpersonationToken</c>.
    /// </summary>
    public TimeSpan ExpirationTimespanImpersonationToken { get; set; } = TimeSpan.FromDays(1);

    internal AuthenticationConfiguration()
    {
        
    }
}