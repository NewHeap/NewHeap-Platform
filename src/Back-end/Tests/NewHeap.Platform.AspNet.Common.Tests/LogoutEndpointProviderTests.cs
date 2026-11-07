using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Tests.Relational;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Xunit;
using HttpMethod = System.Net.Http.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The standard logout endpoint over HTTP on each relational provider. A logout revokes the presented
/// refresh token and expires the browser's authentication cookies. With
/// <see cref="Models.LogoutRequest.KeepAuthenticationCookies"/> it revokes only the token in the
/// body, for a browser that signs out one of several accounts, and leaves the cookies of the account
/// that stays active alone.
/// </summary>
public sealed class LogoutEndpointProviderTests(LogoutEndpointTestHosts hosts) : IClassFixture<LogoutEndpointTestHosts>
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ALogoutRevokesThePresentedTokenAndExpiresTheCookies(RelationalTestProvider provider)
    {
        var host = await hosts.GetAsync(provider);
        var account = await host.CreateAccountAsync("single");
        var session = await host.SignInAsync(account);

        var logout = await host.Client.PostAsJsonAsync(
            "/authentication/logout",
            new { refreshToken = session.RefreshToken },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False(logout.Headers.Contains(NhLogoutAuthenticationHandler.AuthenticationCookiesHeaderName));
        var expired = logout.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(expired, cookie => cookie.StartsWith(LogoutEndpointTestHost.AccessCookieName + "=;", StringComparison.Ordinal));
        Assert.Contains(expired, cookie => cookie.StartsWith(LogoutEndpointTestHost.RefreshCookieName + "=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.RefreshAsync(account, session.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task SigningOutAnotherAccountRevokesOnlyItsTokenAndKeepsTheCookies(RelationalTestProvider provider)
    {
        var host = await hosts.GetAsync(provider);
        var other = await host.CreateAccountAsync("other");
        var active = await host.CreateAccountAsync("active");
        var otherSession = await host.SignInAsync(other);
        var activeSession = await host.SignInAsync(active);

        // The browser sends the cookies of the active account along, as it does with every request.
        var request = new HttpRequestMessage(HttpMethod.Post, "/authentication/logout")
        {
            Content = JsonContent.Create(new { refreshToken = otherSession.RefreshToken, keepAuthenticationCookies = true })
        };
        request.Headers.Add("Cookie", $"{LogoutEndpointTestHost.RefreshCookieName}={activeSession.RefreshToken}");
        var logout = await host.Client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(
            [NhLogoutAuthenticationHandler.AuthenticationCookiesKept],
            logout.Headers.GetValues(NhLogoutAuthenticationHandler.AuthenticationCookiesHeaderName));
        Assert.False(logout.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.RefreshAsync(other, otherSession.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.RefreshAsync(active, activeSession.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task KeepingTheCookiesNeverRevokesTheCookiesToken(RelationalTestProvider provider)
    {
        var host = await hosts.GetAsync(provider);
        var account = await host.CreateAccountAsync("cookie");
        var session = await host.SignInAsync(account);

        var request = new HttpRequestMessage(HttpMethod.Post, "/authentication/logout")
        {
            Content = JsonContent.Create(new { keepAuthenticationCookies = true })
        };
        request.Headers.Add("Cookie", $"{LogoutEndpointTestHost.RefreshCookieName}={session.RefreshToken}");
        var logout = await host.Client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.False(logout.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await host.RefreshAsync(account, session.RefreshToken)).StatusCode);
    }
}

/// <summary>The logout tests' hosts, started once per provider for the test class.</summary>
public sealed class LogoutEndpointTestHosts : IAsyncDisposable
{
    private readonly ConcurrentDictionary<RelationalTestProvider, Lazy<Task<LogoutEndpointTestHost>>> _hosts = new();

    internal Task<LogoutEndpointTestHost> GetAsync(RelationalTestProvider provider)
    {
        return _hosts.GetOrAdd(
            provider,
            _ => new Lazy<Task<LogoutEndpointTestHost>>(() => LogoutEndpointTestHost.CreateAsync(provider))).Value;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values.Where(host => host.IsValueCreated))
        {
            await (await host.Value).DisposeAsync();
        }
    }
}

/// <summary>
/// An application with the standard username and password endpoints on a database of its own. The
/// tests act as the browser and send its cookies themselves; every test creates accounts of its own.
/// </summary>
internal sealed class LogoutEndpointTestHost : IAsyncDisposable
{
    public const string AccessCookieName = "logout_tests_auth";
    public const string RefreshCookieName = "logout_tests_refresh";
    private const string Password = "Logout-Tests-2026!";

    private readonly WebApplication _application;

    private LogoutEndpointTestHost(WebApplication application, string label)
    {
        _application = application;
        Client = application.GetTestClient();
        Label = label;
    }

    public HttpClient Client { get; }

    private string Label { get; }

    public static async Task<LogoutEndpointTestHost> CreateAsync(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://api.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "logout-endpoint-tests-signing-key-2026",
        });

        builder.Services
            .AddNewHeapPlatformAspNetCommon<
                NhUser,
                NhUserRole,
                NhDivision,
                NhDivisionUser,
                NhDivisionRole,
                NhDivisionUserRole,
                NhDivisionRoleClaim,
                NhLog,
                NhLogMessageArgument,
                NhLogFile,
                NhLogMessageTranslated,
                NhDbLogService,
                LogoutEndpointTestDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(authentication =>
            {
                authentication.AddUserNamePasswordAuthentication(options =>
                {
                    options.AccessTokenCookieName = AccessCookieName;
                    options.RefreshTokenCookieName = RefreshCookieName;
                });
            })
            .WithIdentityEntityFramework(database.UseNewHeapProvider)
            .WithIdentity()
            .WithDbLogService(_ => { });

        builder.Services.AddHttpContextAccessor();

        var application = builder.Build();
        application.UseRouting();
        new NhAuthenticationConfigurationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole,
                NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
                NhClaimViewModel>()
            .AddUserNamePasswordEndpoint(enableImpersonate: false)
            .Build(application, application.Services);

        await using (var scope = application.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LogoutEndpointTestDbContext>().Database.EnsureCreatedAsync();
        }

        await application.StartAsync();
        return new LogoutEndpointTestHost(application, database.Label);
    }

    public async Task<NhUser> CreateAccountAsync(string name)
    {
        var email = $"{name}-{Label}-{Guid.NewGuid():N}@example.test";
        var user = new NhUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
        };

        await using var scope = _application.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>().CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        return user;
    }

    /// <summary>Signs in as the browser does and returns the session from the response body.</summary>
    public async Task<SignedInSession> SignInAsync(NhUser user)
    {
        var response = await Client.PostAsJsonAsync(
            "/authentication/login",
            new { userName = user.UserName, password = Password },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<SignedInSession>(CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(session?.RefreshToken));
        return session!;
    }

    public Task<HttpResponseMessage> RefreshAsync(NhUser user, string refreshToken)
    {
        return Client.PostAsJsonAsync(
            "/authentication/refresh",
            new { userName = user.UserName, refreshToken },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}

internal sealed record SignedInSession(string Token, string RefreshToken);

internal sealed class LogoutEndpointTestDbContext(DbContextOptions<LogoutEndpointTestDbContext> options)
    : NhIdentityDbContext(options);
