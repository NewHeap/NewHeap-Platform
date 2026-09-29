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
