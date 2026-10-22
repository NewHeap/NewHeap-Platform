using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// <c>UseNewHeapPlatformAspNetCommon</c> with the old <c>UseMvc</c> routing (the default) and with
/// <c>UseEndpointRoutingOnly</c>, in a host composed as a consumer composes it.
/// </summary>
public sealed class PlatformPipelineRoutingTests
{
    private const string Issuer = "https://pipeline.example.test";
    private const string Key = "platform-pipeline-routing-tests-signing-key";
    private const string AllowedOrigin = "https://app.example.test";
    public const string Permission = "pipeline.view";

    public enum ControllerRouting
    {
        UseMvc,
        EndpointRoutingOnly
    }

    [Theory]
    [InlineData(ControllerRouting.UseMvc)]
    [InlineData(ControllerRouting.EndpointRoutingOnly)]
    public async Task Controllers_keep_authorization_and_CORS_and_carry_endpoint_metadata_only_with_the_opt_in(ControllerRouting routing)
    {
        await using var app = await StartAsync(routing, builder => { });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, CreateToken([]))).StatusCode);
        using var response = await SendAsync(client, CreateToken([Permission]), AllowedOrigin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        // With the opt-in, endpoint routing serves the controller and UseMvc never routes it.
        var probe = await response.Content.ReadFromJsonAsync<PipelineProbe>();
        Assert.Equal(routing == ControllerRouting.EndpointRoutingOnly, probe!.HasEndpointMetadata);
        Assert.Equal(routing == ControllerRouting.UseMvc, probe.RoutedByUseMvc);

        // The platform's CORS policy still answers preflight requests for every origin it allows.
        Assert.Equal(AllowedOrigin, await PreflightAsync(client, AllowedOrigin));
        Assert.Null(await PreflightAsync(client, "https://other.example.test"));
    }

    [Fact]
    public async Task The_opt_in_refuses_routes_of_the_old_MVC_routing_and_endpoint_routing_turned_off()
    {
        var routes = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            ControllerRouting.EndpointRoutingOnly,
            pipeline => pipeline.UseMvc(routes => routes.MapRoute("default", "{controller}/{action}"))));
        var turnedOff = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            ControllerRouting.EndpointRoutingOnly,
            _ => { },
            options => options.ConfigureMvc(mvc => mvc.EnableEndpointRouting = false)));

        Assert.Contains("MapControllerRoute", routes.Message);
        Assert.Contains("EnableEndpointRouting", turnedOff.Message);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? token, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/pipeline-probe");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return await client.SendAsync(request);
    }

    private static async Task<string?> PreflightAsync(HttpClient client, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/pipeline-probe");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;
    }

    private static string CreateToken(string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer,
            Issuer,
            claims,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)));
    }

    private static async Task<WebApplication> StartAsync(
        ControllerRouting routing,
        Action<NewHeapPlatformAspNetCommonOptionsBuilder> configurePipeline,
        Action<NewHeapAspNetCommonOptionsBuilder>? configureOptions = null)
    {
        // Production, as deployed: the host does not validate services it never resolves, such as the
        // database-backed Identity services that no request here reaches.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Authorization:JWT:Token:Issuer"] = Issuer,
            [$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Authorization:JWT:Token:ValidAudience"] = Issuer,
            [NhJwtSigningKey.ConfigurationKey] = Key,
            [$"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:Settings:AllowedOrigins:0"] = AllowedOrigin
        });

        var options = NewHeapAspNetCommonOptions.Builder(builder.Configuration)
            .UseEndpointRoutingOnly(routing == ControllerRouting.EndpointRoutingOnly)
            .ConfigureAuthorization(authorization => authorization.AddPolicy(
                Permission,
                policy => policy.RequireClaim("permission", Permission)));
        configureOptions?.Invoke(options);
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
                NhDivisionUserMutateModel>(options.Build())
            .WithIdentity();
        builder.Services.AddControllers().AddApplicationPart(typeof(PlatformPipelineRoutingTests).Assembly);
        // The test tokens belong to no stored user; the session check is not under test here.
        builder.Services.AddScoped<INhAuthenticationSessionValidator, AcceptingSessionValidator>();

        var app = builder.Build();
        var pipeline = NewHeapPlatformAspNetCommonApplicationBuilderOptions.Builder
            .UseHsTs(false)
            .UseHttpsRedirection(false);
        configurePipeline(pipeline);
        app.UseNewHeapPlatformAspNetCommon(app.Environment, app.Services, pipeline.Build());
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

public sealed record PipelineProbe(bool HasEndpointMetadata, bool RoutedByUseMvc);

/// <summary>
/// Reports how the request reached it. It authorizes with the bearer scheme as <c>NhBaseController</c> does.
/// </summary>
[ApiController]
[Route("pipeline-probe")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = PlatformPipelineRoutingTests.Permission)]
public sealed class PipelineProbeController : ControllerBase
{
    [HttpGet]
    public PipelineProbe Get()
    {
        return new PipelineProbe(
            HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAuthorizeData>() is not null,
            HttpContext.Features.Get<IRoutingFeature>() is not null);
    }
}
