using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-370: an MCP client outside the application, here the official MCP SDK client as any client that
/// follows the MCP authorization flow, signs in to the API's <c>/mcp</c> through the application's
/// authorization server (a stand-in, <see cref="NhAiTestAuthorizationServer"/>) with
/// <see cref="SampleMcpSignInComposition"/>: it reads the challenge and the metadata, registers, signs the
/// division editor in with PKCE and the resource, and lists and calls the project tools that the editor's
/// account may use in Sample North. The viewer's account signs in too but gets no project tool, a subject
/// linked to no account is refused, and the application's own NewHeap token keeps working. Other
/// applications sign in at <c>/applications/mcp</c>, a named protected resource whose tokens never sign in
/// at <c>/mcp</c> and the other way round, and a token of the application's own scheme for service tokens
/// is never passed on, like the tokens of both protected resources.
/// </summary>
public sealed class McpSignInSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private const string Scope = "projects.read";
    private const string ApplicationsScope = "projects.applications";
    private const string EditorSubject = "north-editor";
    private const string ViewerSubject = "north-viewer";
    private const string ServiceSubject = "planning-service";
    private const string RedirectUri = "http://localhost:7071/callback";

    [Fact]
    public async Task OtherApplicationsSignInAtTheirOwnEndpointAndATokenForOneEndpointNeverSignsInAtTheOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // One authorization server issues the tokens of both endpoints; only the audience keeps them apart.
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options =>
            options.ScopesSupported = [Scope, ApplicationsScope]);
        await using var host = await SampleMcpSignInHost.StartAsync(
            await postgreSql.CreateMigratedDatabaseAsync("mcp_applications_sign_in"),
            authorization.Issuer,
            applicationsIssuer: authorization.Issuer);
        await host.LinkAsync(SampleAuthorizationDefaults.DivisionEditorEmail, authorization.Issuer, EditorSubject);

        // The endpoint of other applications has metadata of its own; the root address stays /mcp's.
        using (var metadata = await host.Client.GetAsync("/.well-known/oauth-protected-resource/applications/mcp", cancellationToken))
        using (var document = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(cancellationToken)))
        {
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            Assert.Equal(host.ApplicationsMcpUrl, document.RootElement.GetProperty("resource").GetString());
            Assert.Equal(ApplicationsScope, Assert.Single(document.RootElement.GetProperty("scopes_supported").EnumerateArray()).GetString());
        }
        using (var root = await host.Client.GetAsync("/.well-known/oauth-protected-resource", cancellationToken))
        using (var document = JsonDocument.Parse(await root.Content.ReadAsStringAsync(cancellationToken)))
        {
            Assert.Equal(host.McpUrl, document.RootElement.GetProperty("resource").GetString());
        }

        // An application signs in at its endpoint as its linked account and gets that account's tools.
        var forApplications = authorization.IssueAccessToken(EditorSubject, host.ApplicationsMcpUrl, ApplicationsScope);
        await using (var application = await ConnectAsync(host.ApplicationsMcpUrl, forApplications, cancellationToken))
        {
            Assert.Contains("projects_search_v1", (await application.ListToolsAsync(cancellationToken: cancellationToken)).Select(tool => tool.Name));
        }

        // A token for one endpoint never signs in at the other, though the same server issued it.
        var forMcp = authorization.IssueAccessToken(EditorSubject, host.McpUrl, Scope);
        foreach (var (path, token) in new[] { (SampleMcpSignInComposition.McpPath, forApplications), (SampleMcpSignInComposition.ApplicationsMcpPath, forMcp) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await host.Client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.EndsWith("error=\"invalid_token\"", Assert.Single(response.Headers.WwwAuthenticate).ToString(), StringComparison.Ordinal);
        }

        // Neither endpoint's token is ever passed on.
        Assert.False(await host.HasCallerTokenAsync(SampleMcpSignInComposition.McpPath, forMcp));
        Assert.False(await host.HasCallerTokenAsync(SampleMcpSignInComposition.ApplicationsMcpPath, forApplications));
    }

    [Fact]
    public async Task AServiceTokenOfTheApplicationsOwnSchemeSignsInAtMcpAndIsNeverPassedOn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options => options.ScopesSupported = [Scope]);
        // The organization's identity provider, whose tokens for /mcp the application validates itself.
        await using var identityProvider = await NhAiTestAuthorizationServer.StartAsync(options => options.ScopesSupported = [Scope]);
        await using var host = await SampleMcpSignInHost.StartAsync(
            await postgreSql.CreateMigratedDatabaseAsync("mcp_service_tokens"),
            authorization.Issuer,
            serviceTokensAuthority: identityProvider.Issuer);
        await host.LinkAsync(SampleAuthorizationDefaults.ManagerEmail, identityProvider.Issuer, ServiceSubject);

        var serviceToken = identityProvider.IssueAccessToken(ServiceSubject, host.McpUrl, Scope);
        await using (var service = await ConnectAsync(host.McpUrl, serviceToken, cancellationToken))
        {
            Assert.Contains("projects_search_v1", (await service.ListToolsAsync(cancellationToken: cancellationToken)).Select(tool => tool.Name));
        }

        // The service token is marked as never passed on; the application's own NewHeap token is not.
        Assert.False(await host.HasCallerTokenAsync(SampleMcpSignInComposition.McpPath, serviceToken));
        Assert.True(await host.HasCallerTokenAsync(SampleMcpSignInComposition.McpPath, await host.NewHeapTokenAsync(SampleAuthorizationDefaults.ManagerEmail)));

        // A token of the identity provider for another audience is refused.
        using var request = new HttpRequestMessage(HttpMethod.Post, SampleMcpSignInComposition.McpPath) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identityProvider.IssueAccessToken(ServiceSubject, host.Origin + "/api", Scope));
        using var refused = await host.Client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task AnExternalMcpClientSignsInThroughTheAuthorizationServerAndGetsTheLinkedAccountsTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options =>
        {
            options.ScopesSupported = [Scope];
            options.Subject = EditorSubject;
        });
        await using var host = await SampleMcpSignInHost.StartAsync(
            await postgreSql.CreateMigratedDatabaseAsync("mcp_sign_in"),
            authorization.Issuer);
        await host.LinkAsync(SampleAuthorizationDefaults.DivisionEditorEmail, authorization.Issuer, EditorSubject);
        await host.LinkAsync(SampleAuthorizationDefaults.ViewerEmail, authorization.Issuer, ViewerSubject);

        // The metadata names the API's own address, the authorization server and the scope.
        using var metadata = await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using (var document = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(cancellationToken)))
        {
            Assert.Equal(host.McpUrl, document.RootElement.GetProperty("resource").GetString());
            Assert.Equal(authorization.Issuer, Assert.Single(document.RootElement.GetProperty("authorization_servers").EnumerateArray()).GetString());
            Assert.Equal(Scope, Assert.Single(document.RootElement.GetProperty("scopes_supported").EnumerateArray()).GetString());
        }

        // Without a token the endpoint challenges with the metadata address.
        using var anonymous = await host.Client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{host.Origin}/.well-known/oauth-protected-resource/mcp\", scope=\"{Scope}\"",
            Assert.Single(anonymous.Headers.WwwAuthenticate).ToString());

        // The client follows the challenge, registers, signs the editor in and works in Sample North.
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        await using (var editor = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.McpUrl),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = ActiveDivision(),
                OAuth = new ClientOAuthOptions
                {
                    RedirectUri = new Uri(RedirectUri),
                    DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "An MCP client" },
                    // The person approves at the authorization server; the redirect carries the code,
                    // the state and the issuer, which the client checks.
                    AuthorizationCallbackHandler = async (context, token) =>
                    {
                        using var approved = await browser.GetAsync(context.AuthorizationUri, token);
                        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
                        var query = QueryHelpers.ParseQuery(approved.Headers.Location!.Query);
                        return new AuthorizationResult
                        {
                            Code = query["code"].ToString(),
                            State = query["state"].ToString(),
                            Iss = query["iss"].ToString()
                        };
                    }
                }
            }),
            cancellationToken: cancellationToken))
        {
            var tools = await editor.ListToolsAsync(cancellationToken: cancellationToken);
            Assert.Contains("projects_search_v1", tools.Select(tool => tool.Name));
            var found = await editor.CallToolAsync(
                "projects_search_v1",
                new Dictionary<string, object?> { ["input"] = new Dictionary<string, object?> { ["limit"] = 5 } },
                cancellationToken: cancellationToken);
            Assert.NotEqual(true, found.IsError);
            Assert.Contains("North roadmap", found.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal(
            host.McpUrl,
            authorization.TokenRequests.Single(request => request.GrantType == "authorization_code").Resource);

        // The viewer's account signs in, but the project tools' own rules leave it nothing in Sample North.
        await using (var viewer = await ConnectAsync(host, authorization.IssueAccessToken(ViewerSubject, host.McpUrl, Scope), cancellationToken))
        {
            Assert.DoesNotContain(
                (await viewer.ListToolsAsync(cancellationToken: cancellationToken)).Select(tool => tool.Name),
                name => name.StartsWith("projects_", StringComparison.Ordinal));
        }

        // A subject that is linked to no account is refused, and so is a token for another resource.
        foreach (var refused in new[]
        {
            authorization.IssueAccessToken("unlinked-person", host.McpUrl, Scope),
            authorization.IssueAccessToken(EditorSubject, host.Origin + "/api", Scope)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refused);
            using var response = await host.Client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.EndsWith("error=\"invalid_token\"", Assert.Single(response.Headers.WwwAuthenticate).ToString(), StringComparison.Ordinal);
        }

        // The application's own clients keep signing in with its NewHeap token.
        await using (var manager = await ConnectAsync(host, await host.NewHeapTokenAsync(SampleAuthorizationDefaults.ManagerEmail), cancellationToken))
        {
            Assert.Contains("projects_search_v1", (await manager.ListToolsAsync(cancellationToken: cancellationToken)).Select(tool => tool.Name));
        }
    }

    [Fact]
    public void WithoutItsSettingsTheApiKeepsItsMcpEndpointAsBefore()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddMcpServer().WithNewHeapPlatformAITools(exposure => exposure.WithSampleMcpSignIn(configuration));

        Assert.False(SampleMcpSignInComposition.IsConfigured(configuration));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.Name == "NhAiMcpProtectedResource");
    }

    private static Dictionary<string, string> ActiveDivision()
    {
        return new Dictionary<string, string>
        {
            [Constants.HttpHeaderKeys.ActiveDivisionId] = SampleAuthorizationDefaults.NorthDivisionId.ToString()
        };
    }

    private static Task<McpClient> ConnectAsync(SampleMcpSignInHost host, string token, CancellationToken cancellationToken)
    {
        return ConnectAsync(host.McpUrl, token, cancellationToken);
    }

    private static async Task<McpClient> ConnectAsync(string endpoint, string token, CancellationToken cancellationToken)
    {
        var headers = ActiveDivision();
        headers["Authorization"] = "Bearer " + token;
        return await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(endpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers
            }),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The authentication and MCP part of the API's composition in Program.cs, with
    /// <c>SampleProjectManagement:McpSignIn</c> naming the host's own address and the authorization
    /// server, on a database of its own with the development identities. With an
    /// <c>applicationsIssuer</c> it configures the endpoint of other applications, with a
    /// <c>serviceTokensAuthority</c> the alternative scheme for service tokens.
    /// </summary>
    private sealed class SampleMcpSignInHost : IAsyncDisposable
    {
        /// <summary>
        /// A probe of the test, not of the sample: behind the policy of the endpoint it names, it answers
        /// whether the caller-credentials accessor would pass the request's token on.
        /// </summary>
        private const string CallerTokenProbePath = "/test/caller-token";

        private readonly WebApplication _application;

        private SampleMcpSignInHost(WebApplication application, string origin)
        {
            _application = application;
            Origin = origin;
            Client = new HttpClient { BaseAddress = new Uri(origin) };
        }

        public string Origin { get; }

        public string McpUrl => Origin + SampleMcpSignInComposition.McpPath;

        public string ApplicationsMcpUrl => Origin + SampleMcpSignInComposition.ApplicationsMcpPath;

        public HttpClient Client { get; }

        /// <summary>
        /// Whether a request signed in with <paramref name="token"/> at the endpoint of
        /// <paramref name="mcpPath"/> has a caller token that a bridge or a forward-user-token server would get.
        /// </summary>
        public async Task<bool> HasCallerTokenAsync(string mcpPath, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CallerTokenProbePath + mcpPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return bool.Parse(await response.Content.ReadAsStringAsync());
        }

        public static async Task<SampleMcpSignInHost> StartAsync(
            string connectionString,
            string issuer,
            string? applicationsIssuer = null,
            string? serviceTokensAuthority = null)
        {
            // The resource names the host's own address, so the port is chosen before the host starts.
            var origin = "http://localhost:" + FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls(origin);
            builder.Logging.ClearProviders();
            var settings = new Dictionary<string, string?>
            {
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://sample-project-management.example.test",
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:ValidAudience"] = "https://sample-project-management.example.test",
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "sample-project-management-mcp-sign-in-signing-key",
                [SampleMcpSignInComposition.SectionName + ":Resource"] = origin + SampleMcpSignInComposition.McpPath,
                [SampleMcpSignInComposition.SectionName + ":AuthorizationServers:0"] = issuer,
                [SampleMcpSignInComposition.SectionName + ":ScopesSupported:0"] = Scope,
            };
            if (applicationsIssuer is not null)
            {
                settings[SampleMcpSignInComposition.ApplicationsSectionName + ":Resource"] = origin + SampleMcpSignInComposition.ApplicationsMcpPath;
                settings[SampleMcpSignInComposition.ApplicationsSectionName + ":AuthorizationServers:0"] = applicationsIssuer;
                settings[SampleMcpSignInComposition.ApplicationsSectionName + ":ScopesSupported:0"] = ApplicationsScope;
            }
            if (serviceTokensAuthority is not null)
            {
                settings[SampleMcpSignInComposition.ServiceTokensSectionName + ":Authority"] = serviceTokensAuthority;
                settings[SampleMcpSignInComposition.ServiceTokensSectionName + ":Audience"] = origin + SampleMcpSignInComposition.McpPath;
            }
            builder.Configuration.AddInMemoryCollection(settings);

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
                    NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions
                    .Builder(builder.Configuration)
                    .ConfigureAuthorization(options =>
                    {
                        // The division policies the API registers in Program.cs.
                        options.AddPolicy(
                            ProjectAiTools.ViewPolicy,
                            policy => policy
                                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                                .RequireActiveDivisionAccess(null, new Claim(NhPlatformClaimTypes.DivisionPermission, "project.view")));
                        options.AddPolicy(
                            ProjectAiTools.ManagePolicy,
                            policy => policy.RequireActiveDivisionAccess(null, new Claim(NhPlatformClaimTypes.DivisionPermission, "project.manage")));
                    })
                    .Build())
                .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(options =>
                {
                    options.WithAuthenticationService<SampleAuthenticationService>();
                    options.AddUserNamePasswordAuthentication(authentication => authentication.EnableDivisions = true);
                })
                .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(connectionString))
                .WithIdentity()
                .WithDbLogService(_ => { });
            builder.Services.AddHttpContextAccessor();

            // The project tools as Program.cs exposes them, over a stand-in for the project store.
            builder.Services.AddSingleton<IProjectAiReadService, NorthProjects>();
            builder.Services.AddSingleton<IProjectAiMutationService, NorthProjects>();
            builder.Services.AddSingleton<IProjectAiContextService, NorthProjects>();
            builder.Services.AddScoped<ProjectAiTools>();
            builder.Services.AddSampleProjectManagementAi();
            builder.Services.AddNewHeapPlatformAIAspNet(ai => ai
                .UseToolInvocationPurpose("project-assistance")
                .AddActiveDivisionScope(ProjectAiTools.ViewPolicy)
                .AddCapabilityGrant(ProjectAiTools.ReadCapability, ProjectAiTools.ViewPolicy)
                .AddCapabilityGrant(ProjectAiTools.ManageCapability, ProjectAiTools.ManagePolicy));
            builder.Services.AddScoped<IClaimsTransformation, SampleRuntimeClaimsTransformation>();
            builder.Services.AddAuthentication().AddSampleMcpServiceTokens(builder.Configuration);
            builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithNewHeapPlatformAITools(exposure => exposure.WithSampleMcpSignIn(builder.Configuration));

            var application = builder.Build();
            await using (var scope = application.Services.CreateAsyncScope())
            {
                await SampleDevelopmentIdentitySeeder.SeedAsync(scope.ServiceProvider);
            }
            application.UseRouting();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapSampleMcp(builder.Configuration);
            application.MapPost(CallerTokenProbePath + SampleMcpSignInComposition.McpPath, ReportCallerTokenAsync)
                .RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy);
            if (applicationsIssuer is not null)
            {
                application.MapPost(CallerTokenProbePath + SampleMcpSignInComposition.ApplicationsMcpPath, ReportCallerTokenAsync)
                    .RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicyFor(SampleMcpSignInComposition.ApplicationsResourceName));
            }
            await application.StartAsync();

            return new SampleMcpSignInHost(application, origin);
        }

        private static async Task<string> ReportCallerTokenAsync(HttpContext context, INhAiCallerCredentialAccessor credentials)
        {
            var token = await credentials.GetBearerTokenAsync(context.RequestAborted);
            return (token is not null).ToString();
        }

        /// <summary>Links an account to a person of the authorization server.</summary>
        public async Task LinkAsync(string email, string issuer, string subject)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
            var account = (await users.FindByNameAsync(email))!;
            Assert.True((await users.AddLoginAsync(account, SampleMcpSignInComposition.LoginInfo(issuer, subject))).Succeeded);
        }

        /// <summary>The NewHeap access token of a password sign-in, as the API issues it to its own clients.</summary>
        public async Task<string> NewHeapTokenAsync(string email)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var signedIn = await scope.ServiceProvider.GetRequiredService<INhAuthenticationService>()
                .Authenticate(new AuthenticateRequest(email, SampleAuthorizationDefaults.Password));
            Assert.True(signedIn.Success);
            return signedIn.Data!.Token;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    /// <summary>The projects of Sample North, for the project tools.</summary>
    private sealed class NorthProjects : IProjectAiReadService, IProjectAiMutationService, IProjectAiContextService
    {
        private static readonly ProjectAiSearchItem Roadmap = new(Guid.Parse("7f1c2a54-0b8e-4d3f-9a61-2c5e8b4d7a10"), "NORTH-1", "North roadmap");

        public Task<IReadOnlyList<ProjectAiSearchItem>> SearchForAiAsync(
            Guid divisionId,
            string? query,
            int limit,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProjectAiSearchItem> projects = divisionId == SampleAuthorizationDefaults.NorthDivisionId ? [Roadmap] : [];
            return Task.FromResult(projects);
        }

        public Task<TaskResult<ProjectAiStatusChangeReport>> ChangeStatusForAiAsync(
            Guid divisionId,
            Guid projectId,
            ProjectStatus status,
            ProjectAgentCause? agentCause,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(TaskResult<ProjectAiStatusChangeReport>.Failed("not-used", "The sign-in sample changes no status."));
        }

        public Task<ProjectStatus?> GetStatusForAiAsync(
            Guid divisionId,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ProjectStatus?>(null);
        }

        public Task<IReadOnlyList<ProjectAiContextDocument>> SearchContextForAiAsync(
            Guid divisionId,
            string query,
            int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProjectAiContextDocument>>([]);
        }
    }
}
