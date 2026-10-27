using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Test;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL;
using System.Net;
using System.Net.Http.Json;
using Xunit;
using HttpMethod = System.Net.Http.HttpMethod;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-062: Microsoft sign-in with the API's own settings from <c>appsettings.json</c> and
/// <see cref="SampleMicrosoftOAuthComposition"/>, against a stand-in for Microsoft
/// (<see cref="NhMicrosoftOAuthTestPlatform"/>). The management app starts the sign-in; the callback
/// completes it only in that browser and returns only to one of the sample's origins. The first
/// sign-in of an account in the developer's directory links it by mail; from then on the tenant and
/// object id find it, and an account of another directory with the same mail signs in to nothing.
/// </summary>
public sealed class MicrosoftOAuthSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private const string ManagementReturnUrl = "http://localhost:4210/auth/microsoft/callback";
    private const string SampleTenantId = "0b8e4c2a-5d1f-4e3b-9a6c-7f2d1e0c9b8a";

    [Fact]
    public async Task TheManagementAppSignsInWithMicrosoftInTheBrowserThatStartedIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await SampleMicrosoftOAuthHost.StartAsync(
            await postgreSql.CreateMigratedDatabaseAsync("microsoft_oauth"));
        var userName = await host.CreateAccountAsync("microsoft-" + Guid.NewGuid().ToString("N") + "@sample.local");

        // A return target outside the sample's origins starts nothing.
        var foreign = await host.StartAsync(userName, "https://elsewhere.example.test/signed-in", cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.False(foreign.Headers.Contains("Set-Cookie"));

        // The management app's origin is allowed. The correlation cookie is set for the path of
        // the configured callback, behind the management app's /api proxy.
        var start = await host.StartAsync(userName, ManagementReturnUrl, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var authorizationUrl = await start.Content.ReadFromJsonAsync<string>(cancellationToken);
        var correlation = Assert.Single(start.Headers.GetValues("Set-Cookie"));
        Assert.Contains("path=/api/authentication/oath/microsoft/authorize", correlation, StringComparison.OrdinalIgnoreCase);
        var cookie = correlation.Split(';')[0];
        var state = QueryHelpers.ParseQuery(new Uri(authorizationUrl!).Query)["state"].ToString();
        var account = NhMicrosoftOAuthTestAccount.In(SampleTenantId, userName);

        // Another browser, without the cookie, cannot complete the sign-in.
        var otherBrowser = await host.CallbackAsync(host.Microsoft.SignIn(authorizationUrl!, account), state, cookie: null, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, otherBrowser.StatusCode);
        Assert.False(otherBrowser.Headers.Contains("Set-Cookie"));

        var callback = await host.CallbackAsync(host.Microsoft.SignIn(authorizationUrl!, account), state, cookie, cancellationToken);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, callback.StatusCode);
        Assert.Equal(ManagementReturnUrl, callback.Headers.Location!.OriginalString);
        Assert.Contains(
            callback.Headers.GetValues("Set-Cookie"),
            header => header.StartsWith(SampleMicrosoftOAuthHost.AccessCookieName + "=", StringComparison.Ordinal));

        // That first sign-in linked the account to the Microsoft account's tenant and object id.
        Assert.Equal(
            [NhMicrosoftOAuthLogins.ProviderKey(account.TenantId, account.ObjectId)],
            await host.MicrosoftLoginsAsync(userName));

        // An account of another directory whose mail names the local account signs in to nothing.
        var otherDirectory = NhMicrosoftOAuthTestAccount.In("5a1f0c3e-8b2d-4c6a-9e7f-1d3b5a7c9e0f", userName);
        var refused = await host.SignInAsync(userName, otherDirectory, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.DoesNotContain(
            refused.Headers.GetValues("Set-Cookie"),
            header => header.StartsWith(SampleMicrosoftOAuthHost.AccessCookieName + "=", StringComparison.Ordinal));
    }

    /// <summary>
    /// The authentication part of the API's composition in Program.cs, with the API's
    /// <c>appsettings.json</c>, on a database of its own and a loopback port.
    /// </summary>
    private sealed class SampleMicrosoftOAuthHost : IAsyncDisposable
    {
        public const string AccessCookieName = "sample_project_management_auth";

        private readonly WebApplication _application;
        private readonly HttpClient _client;

        private SampleMicrosoftOAuthHost(WebApplication application, NhMicrosoftOAuthTestPlatform microsoft)
        {
            _application = application;
            Microsoft = microsoft;
            _client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(application.Urls.First()),
            };
        }

        public NhMicrosoftOAuthTestPlatform Microsoft { get; }

        public static async Task<SampleMicrosoftOAuthHost> StartAsync(string connectionString)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "sample-project-management-oauth-signing-key",
                [SampleMicrosoftOAuthComposition.SettingsSection + ":ClientId"] = "00000000-0000-4000-8000-000000000062",
                [SampleMicrosoftOAuthComposition.SettingsSection + ":ClientSecret"] = "sample-microsoft-client-secret",
                // The developer's directory, as appsettings.Development.json or user secrets name it.
                [SampleMicrosoftOAuthComposition.SettingsSection + ":AllowedTenantIds:0"] = SampleTenantId,
            });

            var settings = new MicrosoftAuthSettings();
            settings.ConfigureSampleMicrosoftOAuth(builder.Configuration);
            var microsoft = new NhMicrosoftOAuthTestPlatform(settings.ProfileEndpoint);

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
                    SampleProjectManagementDbContext,
                    NhUserManager,
                    NhDivisionService,
                    NhDivisionMutateModel,
                    NhDivisionUserService,
                    NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
                .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(options =>
                {
                    options.WithAuthenticationService<SampleAuthenticationService>();
                    options.AddMicrosoftOAuth(oauth => oauth.Settings.ConfigureSampleMicrosoftOAuth(builder.Configuration));
                    options.AddUserNamePasswordAuthentication(authentication =>
                    {
                        authentication.AccessTokenCookieName = AccessCookieName;
                        authentication.RefreshTokenCookieName = "sample_project_management_refresh";
                    });
                })
                .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(connectionString))
                .WithIdentity()
                .WithDbLogService(_ => { });
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddHttpClient<MicrosoftAuthService>().ConfigurePrimaryHttpMessageHandler(() => microsoft);

            var application = builder.Build();
            application.UseRouting();
            new NhAuthenticationConfigurationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole,
                    NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
                    NhClaimViewModel>()
                .AddMicrosoftOauthEndpoints()
                .Build(application, application.Services);
            await application.StartAsync();

            return new SampleMicrosoftOAuthHost(application, microsoft);
        }

        public async Task<string> CreateAccountAsync(string email)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>().CreateAsync(new NhUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
            });
            Assert.True(result.Succeeded);
            return email;
        }

        public async Task<IReadOnlyList<string>> MicrosoftLoginsAsync(string userName)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
            var logins = await users.GetLoginsAsync((await users.FindByNameAsync(userName))!);
            return logins
                .Where(login => login.LoginProvider == NhMicrosoftOAuthLogins.LoginProvider)
                .Select(login => login.ProviderKey)
                .ToList();
        }

        /// <summary>A complete sign-in in one browser that returns to the management app.</summary>
        public async Task<HttpResponseMessage> SignInAsync(
            string userName,
            NhMicrosoftOAuthTestAccount account,
            CancellationToken cancellationToken)
        {
            var start = await StartAsync(userName, ManagementReturnUrl, cancellationToken);
            var authorizationUrl = await start.Content.ReadFromJsonAsync<string>(cancellationToken);
            var cookie = start.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            var state = QueryHelpers.ParseQuery(new Uri(authorizationUrl!).Query)["state"].ToString();

            return await CallbackAsync(Microsoft.SignIn(authorizationUrl!, account), state, cookie, cancellationToken);
        }

        /// <summary>The management app's request through its proxy, which reaches the API without <c>/api</c>.</summary>
        public Task<HttpResponseMessage> StartAsync(string userName, string returnUrl, CancellationToken cancellationToken)
        {
            return _client.PostAsJsonAsync(
                "/authentication/oath/microsoft",
                new { callbackUrl = returnUrl, userName },
                cancellationToken);
        }

        public Task<HttpResponseMessage> CallbackAsync(string code, string state, string? cookie, CancellationToken cancellationToken)
        {
            var url = QueryHelpers.AddQueryString(
                "/authentication/oath/microsoft/authorize",
                new Dictionary<string, string?> { ["code"] = code, ["state"] = state });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            return _client.SendAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
