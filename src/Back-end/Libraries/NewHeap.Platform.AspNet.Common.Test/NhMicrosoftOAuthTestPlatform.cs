using Microsoft.AspNetCore.WebUtilities;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.AspNet.Common.Test;

/// <summary>
/// A Microsoft account in <see cref="NhMicrosoftOAuthTestPlatform"/>: the directory (tenant) it
/// belongs to, its object id in that tenant and the <c>mail</c> attribute Microsoft Graph returns.
/// </summary>
public sealed record NhMicrosoftOAuthTestAccount(string TenantId, string ObjectId, string? Mail)
{
    /// <summary>A new account in <paramref name="tenantId"/> with a random object id.</summary>
    public static NhMicrosoftOAuthTestAccount In(string tenantId, string? mail)
    {
        return new NhMicrosoftOAuthTestAccount(tenantId, Guid.NewGuid().ToString(), mail);
    }
}

/// <summary>
/// A stand-in for the Microsoft identity platform and Microsoft Graph in tests of applications that
/// sign in with <c>AddMicrosoftOAuth</c>, so no request leaves the test. Make it the primary handler
/// of the Microsoft client:
/// <c>services.AddHttpClient&lt;MicrosoftAuthService&gt;().ConfigurePrimaryHttpMessageHandler(() =&gt; platform)</c>.
/// <see cref="SignIn"/> plays the account choosing itself at Microsoft for the authorization URL
/// the sign-in start returned and gives the code Microsoft sends to the callback. As Microsoft
/// does, the token endpoint redeems a code once and only with the PKCE verifier of the request's
/// challenge, and answers with an ID token that carries the account's tenant and object id and the
/// request's nonce; Graph's <c>/me</c> answers for the access token it issued.
/// </summary>
public sealed class NhMicrosoftOAuthTestPlatform : HttpMessageHandler
{
    private readonly string _profileEndpoint;
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new();
    private readonly ConcurrentDictionary<string, NhMicrosoftOAuthTestAccount> _accessTokens = new();

    /// <param name="profileEndpoint">The application's configured <c>ProfileEndpoint</c>, such as <c>https://graph.microsoft.com/v1.0/me</c>.</param>
    public NhMicrosoftOAuthTestPlatform(string profileEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileEndpoint);
        _profileEndpoint = new Uri(profileEndpoint).GetLeftPart(UriPartial.Path);
    }

    /// <summary>
    /// <paramref name="account"/> signs in for <paramref name="authorizationUrl"/>; returns the
    /// authorization code. <paramref name="configureIdToken"/> may change the ID token's claims,
    /// for example to test a token for another application.
    /// </summary>
    public string SignIn(
        string authorizationUrl,
        NhMicrosoftOAuthTestAccount account,
        Action<IDictionary<string, object>>? configureIdToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationUrl);
        ArgumentNullException.ThrowIfNull(account);

        var query = QueryHelpers.ParseQuery(new Uri(authorizationUrl).Query);
        if (!query.TryGetValue("code_challenge_method", out var method) || method != "S256"
            || !query.TryGetValue("code_challenge", out var challenge)
            || !query.TryGetValue("client_id", out var clientId))
        {
            throw new InvalidOperationException("The authorization request carries no S256 PKCE challenge.");
        }

        var nonce = query.TryGetValue("nonce", out var nonceValue) ? nonceValue.ToString() : null;
        var code = "code-" + Guid.NewGuid().ToString("N");
        _codes[code] = new IssuedCode(account, challenge.ToString(), nonce, clientId.ToString(), configureIdToken);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Post
            && uri.Host == "login.microsoftonline.com"
            && uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            return await RedeemAsync(request, cancellationToken);
        }

        if (uri.GetLeftPart(UriPartial.Path) == _profileEndpoint
            && request.Headers.Authorization?.Parameter is { } accessToken
            && _accessTokens.TryGetValue(accessToken, out var account))
        {
            return Json(new Dictionary<string, object?>
            {
                ["id"] = account.ObjectId,
                ["mail"] = account.Mail,
                ["userPrincipalName"] = account.Mail,
            });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> RedeemAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
        var code = form.TryGetValue("code", out var codeValue) ? codeValue.ToString() : "";
        var verifier = form.TryGetValue("code_verifier", out var verifierValue) ? verifierValue.ToString() : "";

        if (!_codes.TryRemove(code, out var issued)
            || string.IsNullOrEmpty(verifier)
            || WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != issued.CodeChallenge)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var accessToken = "graph-" + Guid.NewGuid().ToString("N");
        _accessTokens[accessToken] = issued.Account;

        return Json(new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 3600,
            ["scope"] = form.TryGetValue("scope", out var scope) ? scope.ToString() : "",
            ["id_token"] = IdToken(issued),
        });
    }

    /// <summary>An unsigned ID token, as the application receives it over TLS from the token endpoint.</summary>
    private static string IdToken(IssuedCode issued)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["iss"] = $"https://login.microsoftonline.com/{issued.Account.TenantId}/v2.0",
            ["aud"] = issued.ClientId,
            ["tid"] = issued.Account.TenantId,
            ["oid"] = issued.Account.ObjectId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
        };
        if (issued.Nonce != null)
        {
            claims["nonce"] = issued.Nonce;
        }

        if (issued.Account.Mail != null)
        {
            claims["preferred_username"] = issued.Account.Mail;
        }

        issued.ConfigureIdToken?.Invoke(claims);

        var payload = new JwtPayload();
        foreach (var claim in claims)
        {
            payload[claim.Key] = claim.Value;
        }

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(), payload));
    }

    private static HttpResponseMessage Json(object body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    private sealed record IssuedCode(
        NhMicrosoftOAuthTestAccount Account,
        string CodeChallenge,
        string? Nonce,
        string ClientId,
        Action<IDictionary<string, object>>? ConfigureIdToken);
}
