using Microsoft.AspNetCore.WebUtilities;
using NewHeap.Platform.AspNet.Common.Test;
using NewHeap.Platform.Tests.Relational;
using System.Net;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The Microsoft OAuth endpoints on each relational provider, with a stand-in for the Microsoft
/// identity platform: a sign-in completes only in the browser that started it, with the state the
/// application issued, once and before it expires, and returns only to a path of the application
/// or an allowed origin. It signs in the local account linked to the Microsoft account's tenant and
/// object id, links an account by its mail address only once and only in a tenant the application
/// names, and refuses an ID token that was not issued for this sign-in.
/// </summary>
public sealed class MicrosoftOAuthProviderTests(MicrosoftOAuthTestHosts hosts) : IClassFixture<MicrosoftOAuthTestHosts>
{
    private const string TenantId = "aaaaaaaa-1111-4222-8333-444444444444";
    private const string OtherTenantId = "bbbbbbbb-5555-4666-8777-888888888888";
    private const string ReturnUrl = MicrosoftOAuthTestHost.ApplicationOrigin + "/signed-in";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ASignInCompletesOnceInTheBrowserThatStartedIt(RelationalTestProvider provider)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("browser");

        var started = await host.StartAsync(user.UserName!, ReturnUrl);
        Assert.Equal(HttpStatusCode.OK, started.Status);
        Assert.False(string.IsNullOrEmpty(started.Query["nonce"]));
        Assert.Equal("S256", started.Query["code_challenge_method"]);
        Assert.Contains("openid", started.Query["scope"].Split(' '));

        // The correlation cookie is host-only, HttpOnly and Secure, for the callback's path, and is
        // sent along with Microsoft's top-level redirect (SameSite=Lax).
        var correlationCookie = Assert.Single(started.SetCookies);
        Assert.StartsWith(".NewHeap.MicrosoftOAuth.", correlationCookie, StringComparison.Ordinal);
        Assert.Contains("path=/authentication/oath/microsoft/authorize", correlationCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", correlationCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", correlationCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", correlationCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", correlationCookie, StringComparison.OrdinalIgnoreCase);

        // Another browser, which lacks the cookie, cannot complete the sign-in: a login CSRF fails.
        var account = NhMicrosoftOAuthTestAccount.In(TenantId, user.UserName);
        var withoutCookie = await host.CallbackAsync(host.Microsoft.SignIn(started.AuthorizationUrl!, account), started.State, cookie: null);
        Assert.Equal(HttpStatusCode.BadRequest, withoutCookie.StatusCode);
        Assert.False(withoutCookie.Headers.Contains("Set-Cookie"));

        var callback = await host.CallbackAsync(host.Microsoft.SignIn(started.AuthorizationUrl!, account), started.State, started.Cookie);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, callback.StatusCode);
        Assert.Equal(ReturnUrl, callback.Headers.Location!.OriginalString);
        var setCookies = callback.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, header => header.StartsWith(MicrosoftOAuthTestHost.AccessCookieName + "=", StringComparison.Ordinal));
        Assert.Contains(setCookies, header => header.StartsWith(MicrosoftOAuthTestHost.RefreshCookieName + "=", StringComparison.Ordinal));

        // The callback removes the correlation cookie, so the browser cannot use the state again.
        var removed = Assert.Single(setCookies, header => header.StartsWith(started.Cookie!.Split('=')[0] + "=", StringComparison.Ordinal));
        Assert.Contains("expires=Thu, 01 Jan 1970", removed, StringComparison.OrdinalIgnoreCase);
        var replay = await host.CallbackAsync(host.Microsoft.SignIn(started.AuthorizationUrl!, account), started.State, cookie: null);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AStateTheApplicationDidNotIssueCompletesNothing(RelationalTestProvider provider)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("unissued");
        var account = NhMicrosoftOAuthTestAccount.In(TenantId, user.UserName);
        var started = await host.StartAsync(user.UserName!, ReturnUrl);

        // The return target and a random value, as states looked before, read as no state at all.
        var forged = await host.CallbackAsync(
            host.Microsoft.SignIn(started.AuthorizationUrl!, account),
            "https://elsewhere.example.test/;" + Guid.NewGuid(),
            started.Cookie);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Null(forged.Headers.Location);
        Assert.False(forged.Headers.Contains("Set-Cookie"));

        // A changed state fails its integrity check.
        var tampered = await host.CallbackAsync(
            host.Microsoft.SignIn(started.AuthorizationUrl!, account),
            started.State[..^2] + (started.State[^2] == 'A' ? "BB" : "AA"),
            started.Cookie);
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.False(tampered.Headers.Contains("Set-Cookie"));

        // A state that was issued, but more than ten minutes ago on the application's clock, expired.
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        var expired = await host.CallbackAsync(host.Microsoft.SignIn(started.AuthorizationUrl!, account), started.State, started.Cookie);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.False(expired.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, "https://elsewhere.example.test/signed-in")]
    [InlineData(RelationalTestProvider.PostgreSql, "https://elsewhere.example.test/signed-in")]
    [InlineData(RelationalTestProvider.PostgreSql, "//elsewhere.example.test/signed-in")]
    [InlineData(RelationalTestProvider.PostgreSql, "/\\elsewhere.example.test/signed-in")]
    [InlineData(RelationalTestProvider.PostgreSql, "https://user@app.example.test/signed-in")]
    [InlineData(RelationalTestProvider.PostgreSql, "javascript:alert(1)")]
    public async Task AReturnTargetOutsideTheApplicationStartsNoSignIn(RelationalTestProvider provider, string returnUrl)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("return");

        var started = await host.StartAsync(user.UserName!, returnUrl);

        Assert.Equal(HttpStatusCode.BadRequest, started.Status);
        Assert.Empty(started.SetCookies);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task APathOfTheApplicationIsAReturnTarget(RelationalTestProvider provider)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("path");
        var account = NhMicrosoftOAuthTestAccount.In(TenantId, user.UserName);

        var started = await host.StartAsync(user.UserName!, "/projects?view=mine");
        Assert.Equal(HttpStatusCode.OK, started.Status);

        var callback = await host.CallbackAsync(host.Microsoft.SignIn(started.AuthorizationUrl!, account), started.State, started.Cookie);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, callback.StatusCode);
        Assert.Equal("/projects?view=mine", callback.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task TheFirstSignInLinksTheAccountByMailAndLaterSignInsUseTheLink(RelationalTestProvider provider)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("link");
        var other = await host.CreateAccountAsync("other");
        var account = NhMicrosoftOAuthTestAccount.In(TenantId, user.UserName);

        var first = await host.SignInAsync(user.UserName!, account);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, first.StatusCode);
        Assert.Equal(user.Id, MicrosoftOAuthTestHost.SessionUserId(first));
        Assert.Equal([$"{TenantId}/{account.ObjectId}"], await host.MicrosoftLoginsAsync(user));

        // The Microsoft account's mail now names another local account; the link still decides.
        var renamed = await host.SignInAsync(user.UserName!, account with { Mail = other.UserName });
        Assert.Equal(HttpStatusCode.TemporaryRedirect, renamed.StatusCode);
        Assert.Equal(user.Id, MicrosoftOAuthTestHost.SessionUserId(renamed));
        Assert.Empty(await host.MicrosoftLoginsAsync(other));

        // Without a mail attribute at all, the link still signs the account in.
        var withoutMail = await host.SignInAsync(user.UserName!, account with { Mail = null });
        Assert.Equal(user.Id, MicrosoftOAuthTestHost.SessionUserId(withoutMail));
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AnotherMicrosoftAccountWithTheSameMailDoesNotTakeOverALinkedAccount(RelationalTestProvider provider)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("taken");
        var owner = NhMicrosoftOAuthTestAccount.In(TenantId, user.UserName);
        Assert.Equal(user.Id, MicrosoftOAuthTestHost.SessionUserId(await host.SignInAsync(user.UserName!, owner)));

        var sameMail = NhMicrosoftOAuthTestAccount.In(TenantId, user.UserName);
        var refused = await host.SignInAsync(user.UserName!, sameMail);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(MicrosoftOAuthTestHost.SessionUserId(refused));
        Assert.Equal([$"{TenantId}/{owner.ObjectId}"], await host.MicrosoftLoginsAsync(user));
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AnAccountOfATenantThatIsNotAllowedSignsInToNothing(RelationalTestProvider provider)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("foreign");

        // A directory the application does not allow, whose account's mail names the local account.
        var refused = await host.SignInAsync(user.UserName!, NhMicrosoftOAuthTestAccount.In(OtherTenantId, user.UserName));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(MicrosoftOAuthTestHost.SessionUserId(refused));
        Assert.Empty(await host.MicrosoftLoginsAsync(user));
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, "audience")]
    [InlineData(RelationalTestProvider.PostgreSql, "audience")]
    [InlineData(RelationalTestProvider.PostgreSql, "nonce")]
    [InlineData(RelationalTestProvider.PostgreSql, "issuer")]
    [InlineData(RelationalTestProvider.PostgreSql, "expired")]
    [InlineData(RelationalTestProvider.PostgreSql, "no-object-id")]
    public async Task AnIdTokenThatWasNotIssuedForThisSignInIsRefused(RelationalTestProvider provider, string change)
    {
        var host = await HostAsync(provider);
        var user = await host.CreateAccountAsync("id-token");

        var refused = await host.SignInAsync(
            user.UserName!,
            NhMicrosoftOAuthTestAccount.In(TenantId, user.UserName),
            claims =>
            {
                switch (change)
                {
                    case "audience":
                        claims["aud"] = "99999999-0000-4000-8000-000000000000";
                        break;
                    case "nonce":
                        claims["nonce"] = "a-nonce-of-another-sign-in";
                        break;
                    case "issuer":
                        claims["iss"] = $"https://login.microsoftonline.com/{OtherTenantId}/v2.0";
                        break;
                    case "expired":
                        claims["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
                        break;
                    default:
                        claims.Remove("oid");
                        break;
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(MicrosoftOAuthTestHost.SessionUserId(refused));
        Assert.Empty(await host.MicrosoftLoginsAsync(user));
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task WithoutAllowedTenantsOnlyAccountsTheApplicationLinkedSignIn(RelationalTestProvider provider)
    {
        // A multi-tenant authority without AllowedTenantIds: no tenant is trusted to link by mail.
        var host = await hosts.GetAsync(provider, "any-tenant", settings => settings.AllowedTenantIds = []);
        var user = await host.CreateAccountAsync("any-tenant");
        var account = NhMicrosoftOAuthTestAccount.In(OtherTenantId, user.UserName);

        var unlinked = await host.SignInAsync(user.UserName!, account);
        Assert.Equal(HttpStatusCode.BadRequest, unlinked.StatusCode);
        Assert.Null(MicrosoftOAuthTestHost.SessionUserId(unlinked));
        Assert.Empty(await host.MicrosoftLoginsAsync(user));

        await host.LinkAsync(user, account);
        var linked = await host.SignInAsync(user.UserName!, account);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, linked.StatusCode);
        Assert.Equal(user.Id, MicrosoftOAuthTestHost.SessionUserId(linked));
    }

    private Task<MicrosoftOAuthTestHost> HostAsync(RelationalTestProvider provider)
    {
        return hosts.GetAsync(provider, "allowed-tenant", settings => settings.AllowedTenantIds = [TenantId]);
    }
}
