using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.AspNet.Common.Models;

/*
 * Collection of authorization related models
 */

public class UserToken(string Token, DateTime ValidTo, string? RefreshToken, DateTime? RefreshValidTo, string Issuer)
{
    public string Token { get; } = Token;
    public DateTime ValidTo { get; } = ValidTo;
    public string? RefreshToken { get; set; } = RefreshToken;
    public DateTime? RefreshValidTo { get; } = RefreshValidTo;

    public string Issuer { get; } = Issuer;
}

public record RefreshTokenRequest(string UserName, string RefreshToken);

public sealed class LogoutRequest
{
    public string? RefreshToken { get; init; }

    /// <summary>
    /// Revokes only <see cref="RefreshToken"/> and leaves the browser's authentication cookies as they
    /// are, for a browser that signs out one of several accounts while another one stays active. The
    /// cookie's refresh token is never used in its place, so without <see cref="RefreshToken"/> the
    /// request revokes nothing. The response then carries
    /// <see cref="NewHeap.Platform.AspNet.Common.Authentication.NhLogoutAuthenticationHandler.AuthenticationCookiesHeaderName"/> with
    /// <see cref="NewHeap.Platform.AspNet.Common.Authentication.NhLogoutAuthenticationHandler.AuthenticationCookiesKept"/>. Off by default: a logout
    /// ends the browser's session and expires the cookies.
    /// </summary>
    public bool KeepAuthenticationCookies { get; init; }
}

public record AuthenticateRequest([Required] string UserName, [Required] string Password)
{
    /// <summary>
    /// Remember-device token from an earlier two-factor sign-in, for clients that do not use
    /// cookies. Cookie clients send it automatically.
    /// </summary>
    public string? RememberDeviceToken { get; init; }
}

public record ImpersonateRequest(Guid? UserId);

public record ImpersonateRevertRequest();
