using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Test;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Tests.Relational;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Xunit;
using HttpMethod = System.Net.Http.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The hosts of the Microsoft OAuth tests, started once per provider and configuration for the
/// test class. Every test creates accounts of its own, so tests share a host but no rows.
/// </summary>
public sealed class MicrosoftOAuthTestHosts : IAsyncDisposable
{
    private readonly ConcurrentDictionary<(RelationalTestProvider Provider, string Configuration), Lazy<Task<MicrosoftOAuthTestHost>>> _hosts = new();

    internal Task<MicrosoftOAuthTestHost> GetAsync(
        RelationalTestProvider provider,
        string configuration,
        Action<MicrosoftAuthSettings> configure)
    {
        return _hosts.GetOrAdd(
            (provider, configuration),
            _ => new Lazy<Task<MicrosoftOAuthTestHost>>(() => MicrosoftOAuthTestHost.CreateAsync(provider, configure))).Value;
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
/// An application with the platform's Microsoft OAuth endpoints on a database of its own, whose
/// Microsoft identity platform is <see cref="NhMicrosoftOAuthTestPlatform"/>: no request leaves
/// the process. The tests act as the browser and keep its cookies themselves.
/// </summary>
internal sealed class MicrosoftOAuthTestHost : IAsyncDisposable
{
    public const string ClientId = "11111111-2222-4333-8444-555555555555";
    public const string AuthDomain = "oauth.example.test";
    public const string ApplicationOrigin = "https://app.example.test";
    public const string CallbackUrl = "https://api.example.test/authentication/oath/microsoft/authorize";
    public const string ProfileEndpoint = "https://graph.example.test/v1.0/me";
    public const string AccessCookieName = "oauth_tests_auth";
    public const string RefreshCookieName = "oauth_tests_refresh";

    private readonly WebApplication _application;

    private MicrosoftOAuthTestHost(
        WebApplication application,
        HttpClient client,
        NhMicrosoftOAuthTestPlatform microsoft,
        MicrosoftOAuthTestClock clock,
        string label)
    {
        _application = application;
        Client = client;
        Microsoft = microsoft;
        Clock = clock;
        Label = label;
    }

    public HttpClient Client { get; }

    public NhMicrosoftOAuthTestPlatform Microsoft { get; }

    public MicrosoftOAuthTestClock Clock { get; }

    /// <summary>The provider's name as the tests write it into their data.</summary>
    public string Label { get; }

    public static async Task<MicrosoftOAuthTestHost> CreateAsync(
        RelationalTestProvider provider,
        Action<MicrosoftAuthSettings> configure)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var microsoft = new NhMicrosoftOAuthTestPlatform(ProfileEndpoint);
        var clock = new MicrosoftOAuthTestClock(DateTimeOffset.UtcNow);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://api.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "microsoft-oauth-tests-signing-key-2026",
            ["NewHeap:PlatformAspNetCommon:Settings:AllowedOrigins:0"] = ApplicationOrigin,
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
                MicrosoftOAuthTestDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(authentication =>
            {
                authentication.AddMicrosoftOAuth(oauth =>
                {
                    oauth.Settings = new MicrosoftAuthSettings
                    {
                        ClientId = ClientId,
                        ClientSecret = "dummy-microsoft-oauth-client-secret",
                        TenantId = "common",
                        CallbackUrl = CallbackUrl,
                        ProfileEndpoint = ProfileEndpoint,
                        AuthDomains = [AuthDomain],
                    };
                    configure(oauth.Settings);
                });
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
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddHttpClient<MicrosoftAuthService>().ConfigurePrimaryHttpMessageHandler(() => microsoft);

        var application = builder.Build();
        application.UseRouting();
        new NhAuthenticationConfigurationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole,
                NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
                NhClaimViewModel>()
            .AddMicrosoftOauthEndpoints()
            .Build(application, application.Services);

        await using (var scope = application.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MicrosoftOAuthTestDbContext>().Database.EnsureCreatedAsync();
        }

        await application.StartAsync();
        return new MicrosoftOAuthTestHost(application, application.GetTestClient(), microsoft, clock, database.Label);
    }

    /// <summary>A local account that signs in with Microsoft: its user name is in the auth domains.</summary>
    public async Task<NhUser> CreateAccountAsync(string name)
    {
        var email = $"{name}-{Label}-{Guid.NewGuid():N}@{AuthDomain}";
        var user = new NhUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
        };

        await using var scope = _application.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>().CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        return user;
    }

    /// <summary>Starts a sign-in as the browser does: <c>POST authentication/oath/microsoft</c>.</summary>
    public async Task<StartedMicrosoftSignIn> StartAsync(string userName, string returnUrl)
    {
        var response = await Client.PostAsJsonAsync(
            "/authentication/oath/microsoft",
            new { callbackUrl = returnUrl, userName },
            CancellationToken.None);

        return await StartedMicrosoftSignIn.ReadAsync(response);
    }

    /// <summary>The browser's request to the callback after Microsoft redirected it there.</summary>
    public Task<HttpResponseMessage> CallbackAsync(string code, string state, string? cookie)
    {
        var url = QueryHelpers.AddQueryString(
            "/authentication/oath/microsoft/authorize",
            new Dictionary<string, string?> { ["code"] = code, ["state"] = state });
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie != null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return Client.SendAsync(request, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}

/// <summary>What the start of a sign-in answered: the authorization URL and the correlation cookie.</summary>
internal sealed record StartedMicrosoftSignIn(
    HttpStatusCode Status,
    string? AuthorizationUrl,
    string? Cookie,
    IReadOnlyList<string> SetCookies)
{
    public IReadOnlyDictionary<string, string> Query => QueryHelpers.ParseQuery(new Uri(AuthorizationUrl!).Query)
        .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

    public string State => Query["state"];

    public static async Task<StartedMicrosoftSignIn> ReadAsync(HttpResponseMessage response)
    {
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new StartedMicrosoftSignIn(response.StatusCode, null, null, setCookies);
        }

        var url = await response.Content.ReadFromJsonAsync<string>(CancellationToken.None);
        var cookie = setCookies.Select(header => header.Split(';')[0]).SingleOrDefault();
        return new StartedMicrosoftSignIn(response.StatusCode, url, cookie, setCookies);
    }
}

/// <summary>The application's clock, which the tests move forward instead of waiting.</summary>
internal sealed class MicrosoftOAuthTestClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _ticks, by.Ticks);
    }

    public override DateTimeOffset GetUtcNow()
    {
        return new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }
}

internal sealed class MicrosoftOAuthTestDbContext(DbContextOptions<MicrosoftOAuthTestDbContext> options)
    : NhIdentityDbContext(options);
