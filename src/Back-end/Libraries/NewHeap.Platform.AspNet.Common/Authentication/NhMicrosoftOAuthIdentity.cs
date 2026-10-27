using NewHeap.Platform.Common.Models.Options;
using System.IdentityModel.Tokens.Jwt;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>Who signed in with Microsoft: the directory (tenant) id and the object id in that tenant.</summary>
internal sealed record NhMicrosoftOAuthIdentity(Guid TenantId, Guid ObjectId)
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The identity in the ID token of a code redemption, or null when the token is not one that
    /// Microsoft issued to this application for this sign-in: the audience must be the client id,
    /// the issuer the v2.0 issuer of the token's own tenant, the nonce the flow's nonce, and the
    /// token unexpired.
    /// </summary>
    /// <remarks>
    /// The application received the token directly from Microsoft's token endpoint over TLS, in
    /// exchange for its code, verifier and client secret, so its claims are checked without its
    /// signature (OpenID Connect Core 1.0, section 3.1.3.7).
    /// </remarks>
    internal static NhMicrosoftOAuthIdentity? FromIdToken(string? idToken, string clientId, string nonce, DateTimeOffset now)
    {
        var handler = new JwtSecurityTokenHandler();
        if (string.IsNullOrWhiteSpace(idToken) || !handler.CanReadToken(idToken))
        {
            return null;
        }

        JwtSecurityToken token;
        try
        {
            token = handler.ReadJwtToken(idToken);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            return null;
        }

        if (!Guid.TryParse(Claim(token, "tid"), out var tenantId)
            || !Guid.TryParse(Claim(token, "oid"), out var objectId))
        {
            return null;
        }

        var expectedIssuer = $"https://login.microsoftonline.com/{tenantId:D}/v2.0";
        if (!string.Equals(token.Issuer, expectedIssuer, StringComparison.OrdinalIgnoreCase)
            || !token.Audiences.Contains(clientId, StringComparer.Ordinal)
            || !string.Equals(Claim(token, "nonce"), nonce, StringComparison.Ordinal)
            || token.Payload.Expiration is not { } expiration
            || DateTimeOffset.FromUnixTimeSeconds(expiration) <= now - ClockSkew)
        {
            return null;
        }

        return new NhMicrosoftOAuthIdentity(tenantId, objectId);
    }

    /// <summary>
    /// Whether the account's tenant may sign in: it is in <see cref="MicrosoftAuthSettings.AllowedTenantIds"/>
    /// when that list is not empty, and it is <see cref="MicrosoftAuthSettings.TenantId"/> when that is a tenant id.
    /// </summary>
    internal bool TenantMaySignIn(MicrosoftAuthSettings settings)
    {
        if (settings.AllowedTenantIds is { Length: > 0 } allowed
            && !allowed.Any(entry => Guid.TryParse(entry, out var allowedTenant) && allowedTenant == TenantId))
        {
            return false;
        }

        return !Guid.TryParse(settings.TenantId, out var authorityTenant) || authorityTenant == TenantId;
    }

    /// <summary>
    /// Whether an account that is not linked yet may be linked by its mail address: linking is on
    /// and the application names the tenants it trusts, in the allow-list or as its authority.
    /// </summary>
    internal bool MayLinkByEmail(MicrosoftAuthSettings settings)
    {
        if (!settings.LinkAccountsByEmail || !TenantMaySignIn(settings))
        {
            return false;
        }

        return settings.AllowedTenantIds is { Length: > 0 } || Guid.TryParse(settings.TenantId, out _);
    }

    private static string? Claim(JwtSecurityToken token, string type)
    {
        return token.Claims.FirstOrDefault(claim => claim.Type == type)?.Value;
    }
}
