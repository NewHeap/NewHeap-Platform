using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// One Microsoft sign-in that this application started: the browser that started it carries the
/// correlation cookie named after <see cref="CorrelationId"/>, Microsoft returns the protected flow
/// as the OAuth state, <see cref="Nonce"/> comes back in the ID token, and only
/// <see cref="CodeVerifier"/> redeems the authorization code.
/// </summary>
internal sealed record NhMicrosoftOAuthFlow(
    string CorrelationId,
    string ReturnUrl,
    string Nonce,
    string CodeVerifier,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Issues and reads the OAuth state of Microsoft sign-ins. The state is the flow itself, encrypted
/// and signed with ASP.NET Core Data Protection, so a state the application did not issue, or one
/// that was changed, expired or issued for another purpose, reads as nothing. Applications that
/// run on several instances share the Data Protection key ring, as two-factor tickets need too.
/// </summary>
internal sealed class NhMicrosoftOAuthStateProtector
{
    /// <summary>How long a started sign-in can be completed.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const string ProtectorPurpose = "NewHeap.Platform.AspNet.Common.MicrosoftOAuth.State.v1";
    private const string CorrelationCookiePrefix = ".NewHeap.MicrosoftOAuth.";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    internal NhMicrosoftOAuthStateProtector(IDataProtectionProvider dataProtectionProvider, TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _timeProvider = timeProvider;
    }

    /// <summary>A new flow with fresh random values that returns to <paramref name="returnUrl"/>.</summary>
    internal NhMicrosoftOAuthFlow Start(string returnUrl)
    {
        return new NhMicrosoftOAuthFlow(
            RandomValue(),
            returnUrl,
            RandomValue(),
            RandomValue(),
            _timeProvider.GetUtcNow().Add(Lifetime));
    }

    internal string Protect(NhMicrosoftOAuthFlow flow)
    {
        return _protector.Protect(JsonSerializer.Serialize(flow), flow.ExpiresAt);
    }

    /// <summary>The flow in <paramref name="state"/>, or null when this application did not issue it or it expired.</summary>
    internal NhMicrosoftOAuthFlow? Unprotect(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        NhMicrosoftOAuthFlow? flow;
        try
        {
            flow = JsonSerializer.Deserialize<NhMicrosoftOAuthFlow>(_protector.Unprotect(state, out _));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (flow == null
            || string.IsNullOrEmpty(flow.CorrelationId)
            || string.IsNullOrEmpty(flow.ReturnUrl)
            || string.IsNullOrEmpty(flow.Nonce)
            || string.IsNullOrEmpty(flow.CodeVerifier))
        {
            return null;
        }

        if (flow.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        return flow;
    }

    /// <summary>The name of the cookie that binds <paramref name="flow"/> to the browser that started it.</summary>
    internal static string CorrelationCookieName(NhMicrosoftOAuthFlow flow)
    {
        return CorrelationCookiePrefix + flow.CorrelationId;
    }

    /// <summary>The S256 PKCE challenge of <paramref name="codeVerifier"/>.</summary>
    internal static string CodeChallenge(string codeVerifier)
    {
        return WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    /// <summary>32 random bytes as base64url: 43 characters, a valid PKCE code verifier.</summary>
    private static string RandomValue()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }
}
