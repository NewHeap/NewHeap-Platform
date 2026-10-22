using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// Delegated reads in a host composed as a consumer composes it: <c>AddNewHeapPlatformAspNetCommon</c>
/// with <c>WithIdentity</c>, so the Identity cookie is the default authentication scheme, the bridged
/// controllers authorize with the JWT bearer scheme as <c>NhBaseController</c> does, and
/// <c>UseNewHeapPlatformAspNetCommon</c> builds the pipeline, with the old <c>UseMvc</c> routing or
/// with <c>UseEndpointRoutingOnly</c>.
/// </summary>
public sealed class NhAiMvcBridgePlatformPipelineTests
{
    private const string Audience = "platform-pipeline-tests";
    private const string ListAction = "test-api.order.get";

    public enum ControllerRouting
    {
        UseMvc,
        EndpointRoutingOnly
    }

    [Theory]
    [InlineData(ControllerRouting.UseMvc)]
    [InlineData(ControllerRouting.EndpointRoutingOnly)]
    public async Task A_delegated_read_reaches_its_gateway_action_and_nothing_else(ControllerRouting routing)
    {
        await using var app = await StartAsync(routing);
        using var client = app.GetTestClient();
        var invocation = Guid.NewGuid();
        var list = CreateToken(invocation, ListAction);

        // The host authenticates its bearer tokens; a token without the marker is not restricted.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/probe/echo", CreateToken(null, null), null));

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/orders", list, invocation));
        // Another gateway read, a write, an action outside the gateway and a missing header fail.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, "/orders/7", list, invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Post, "/orders", list, invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, "/probe/echo", list, invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, "/orders", list, null));
    }

    [Fact]
    public async Task A_delegated_read_principal_from_an_earlier_authentication_runs_no_other_action()
    {
        await using var app = await StartAsync(ControllerRouting.UseMvc);
        await using var scope = app.Services.CreateAsyncScope();
        var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerName == "Order")
            .ToArray();
        var invocation = Guid.NewGuid();
        // A principal that, say, a pipeline routed before MVC authenticated for the list read.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(NhAiDelegatedReadDefaults.ClaimType, invocation.ToString("D")),
                new Claim(NhAiDelegatedReadDefaults.ActionClaimType, ListAction)
            ],
            "Bearer"));

        async Task<IActionResult?> AuthorizeAsync(string actionName, Type[] parameters)
        {
            var action = actions.Single(candidate => candidate.ActionName == actionName
                && candidate.MethodInfo.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters));
            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = principal };
            httpContext.Request.Method = HttpMethods.Get;
            httpContext.Request.Headers[NhAiDelegatedReadDefaults.InvocationHeaderName] = invocation.ToString("D");
            var context = new AuthorizationFilterContext(
                new ActionContext(httpContext, new RouteData(), action),
                []);
            await new NhAiMvcBridgeDelegatedReadFilter().OnAuthorizationAsync(context);
            return context.Result;
        }

        Assert.Null(await AuthorizeAsync("Get", [typeof(TestOrderCollectionRequest)]));
        Assert.IsType<UnauthorizedResult>(await AuthorizeAsync("Get", [typeof(int)]));
    }

    private static string CreateToken(Guid? invocation, string? action)
    {
        return TestReadTokens.Create("reader", [TestPolicies.OrderView], invocation, TimeSpan.FromMinutes(1), action, audience: Audience);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, string token, Guid? invocation)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (invocation is { } id)
        {
            request.Headers.Add(NhAiDelegatedReadDefaults.InvocationHeaderName, id.ToString("D"));
        }
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<WebApplication> StartAsync(ControllerRouting routing)
    {
        // Production, as deployed: the host does not validate services it never resolves, such as the
        // database-backed Identity services that no request here reaches.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Authorization:JWT:Token:Issuer"] = TestReadTokens.Issuer,
            [$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Authorization:JWT:Token:ValidAudience"] = Audience,
            [NhJwtSigningKey.ConfigurationKey] = TestReadTokens.KeyText
        });

        // The test controllers name their policies; the policies use the bearer scheme as
        // NhBaseController does, because WithIdentity makes the Identity cookie the default scheme.
        var platformOptions = NewHeapAspNetCommonOptions.Builder(builder.Configuration)
            .UseEndpointRoutingOnly(routing == ControllerRouting.EndpointRoutingOnly)
            .ConfigureAuthorization(options =>
            {
                options.AddPolicy(TestPolicies.OrderView, policy => policy
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireClaim("permission", TestPolicies.OrderView));
                options.AddPolicy(TestPolicies.OrderManage, policy => policy
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireClaim("permission", TestPolicies.OrderManage));
            })
            .Build();
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
                PipelineTestDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(platformOptions)
            .WithIdentity();
        builder.Services.AddControllers().AddApplicationPart(typeof(BridgeApiFactory).Assembly);
        // The test tokens belong to no stored user; the session check is not under test here.
        builder.Services.AddScoped<INhAuthenticationSessionValidator, AcceptingSessionValidator>();

        builder.Services.AddNewHeapPlatformAIAspNet(_ => { });
        builder.Services.AddNewHeapPlatformAI(ai => ai
            .UseBudgetManager<NhAiTestBudgetManager>(ServiceLifetime.Singleton)
            .UseIdempotencyManager<TestIdempotencyManager>(ServiceLifetime.Singleton));
        builder.Services.AddNewHeapPlatformAIMvcBridge(bridge => bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order", "OrderAudit")
            .UseConventions<NhAiMvcBridgeGatewayTests.GatewayTestConventions>()
            .EnableGateway(gateway => gateway
                .UseGatewayToolSetId("test-gateway")
                .IncludeReadOnlyOnly()));
        builder.Services.AddSingleton<TestReadIssuer.Recorder>();
        builder.Services.AddNewHeapPlatformAIDelegatedReads(reads => reads.UseIssuer<TestReadIssuer>());

        var app = builder.Build();
        app.UseNewHeapPlatformAspNetCommon(
            app.Environment,
            app.Services,
            NewHeapPlatformAspNetCommonApplicationBuilderOptions.Builder
                .UseHsTs(false)
                .UseHttpsRedirection(false)
                .Build());
        await app.StartAsync();
        return app;
    }

    private sealed class AcceptingSessionValidator : INhAuthenticationSessionValidator
    {
        public Task<bool> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class PipelineTestDbContext(DbContextOptions<PipelineTestDbContext> options)
        : NhIdentityDbContext(options);
}
