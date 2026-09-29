using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class AuthenticationEndpointRouteTests
{
    [Fact]
    public void CustomEndpointOptionsMapEveryHandlerToItsOwnRoute()
    {
        var routes = MapUserNamePasswordEndpoints(options =>
        {
            options.EnableImpersonate = true;
            options.Endpoint = "custom/login";
            options.RefreshTokenEndpoint = "custom/refresh";
            options.LogoutEndpoint = "custom/logout";
            options.ImpersonateEndpoint = "custom/impersonate";
            options.RevertImpersonateEndpoint = "custom/impersonate/revert";
            options.AccountInformationEndpoint = "custom/account";
        });

        Assert.Equal("custom/login", routes["Login"]);
        Assert.Equal("custom/refresh", routes["Refresh token"]);
        Assert.Equal("custom/logout", routes["Logout"]);
        Assert.Equal("custom/impersonate", routes["Impersonate user"]);
        Assert.Equal("custom/impersonate/revert", routes["Impersonate user revert"]);
        Assert.Equal("custom/account", routes["Account information"]);
    }

    [Fact]
    public void ACustomRefreshRouteLeavesTheOtherRoutesAtTheirDefaults()
    {
        var routes = MapUserNamePasswordEndpoints(options =>
        {
            options.EnableImpersonate = true;
            options.RefreshTokenEndpoint = "custom/refresh";
        });

        Assert.Equal("custom/refresh", routes["Refresh token"]);
        Assert.Equal("authentication/login", routes["Login"]);
        Assert.Equal("authentication/logout", routes["Logout"]);
        Assert.Equal("authentication/impersonate", routes["Impersonate user"]);
        Assert.Equal("authentication/ImpersonateRevert", routes["Impersonate user revert"]);
        Assert.Equal("account", routes["Account information"]);
    }

    private static Dictionary<string, string> MapUserNamePasswordEndpoints(
        Action<NhAuthenticationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole,
            NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
            NhClaimViewModel>.UserNamePasswordOptions> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://routes.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "authentication-endpoint-route-tests-key",
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
                RouteTestDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(
                authentication => authentication.AddUserNamePasswordAuthentication(configure))
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql("Host=localhost;Database=unused"))
            .WithIdentity()
            .WithDbLogService(_ => { });

        using var application = builder.Build();
        application.UseRouting();

        new NhAuthenticationConfigurationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole,
                NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
                NhClaimViewModel>()
            .AddUserNamePasswordEndpoint(enableRefreshToken: true, enableImpersonate: true)
            .Build(application, application.Services);

        return ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>() != null)
            .ToDictionary(
                endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName,
                endpoint => endpoint.RoutePattern.RawText!.TrimStart('/'));
    }

    private sealed class RouteTestDbContext(DbContextOptions<RouteTestDbContext> options)
        : NhIdentityDbContext(options);
}
