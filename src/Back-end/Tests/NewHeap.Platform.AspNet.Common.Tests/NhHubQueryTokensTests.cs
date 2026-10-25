using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// LS27: the NewHeap bearer setup reads the token and the active division from the query string for
/// SignalR requests below <c>/hub</c> and below the configured path of a hub a NewHeap library maps,
/// and for no other request.
/// </summary>
public sealed class NhHubQueryTokensTests
{
    [Theory]
    [InlineData("/hub/anything", true)]
    [InlineData("/hub", true)]
    [InlineData("/hubs/assistant", true)]
    [InlineData("/hubs/assistant/negotiate", true)]
    [InlineData("/HUBS/Assistant", true)]
    [InlineData("/hubs/assistant-other", false)]
    [InlineData("/hubs", false)]
    [InlineData("/realtime/operations", true)]
    [InlineData("/api/projects", false)]
    [InlineData("/", false)]
    public void The_query_token_applies_below_hub_and_below_the_hubs_the_libraries_map(string path, bool applies)
    {
        var services = new ServiceCollection()
            .AddSingleton<INhLibraryHubPath>(new FixedHubPath("/hubs/assistant/"))
            // A root path or an unmapped hub opens nothing.
            .AddSingleton<INhLibraryHubPath>(new FixedHubPath("/"))
            .AddSingleton<INhLibraryHubPath>(new FixedHubPath(null))
            .AddSingleton(new NhBackgroundOperationSignalRMarker())
            .AddSingleton(new NhBackgroundOperationsOptions { HubPath = "/realtime/operations" })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;

        Assert.Equal(applies, NhHubQueryTokens.Applies(context));
    }

    [Fact]
    public void The_background_operations_hub_counts_only_while_it_is_mapped()
    {
        var services = new ServiceCollection()
            .AddSingleton(new NhBackgroundOperationsOptions { HubPath = "/realtime/operations" })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/realtime/operations";

        Assert.False(NhHubQueryTokens.Applies(context));
    }

    [Fact]
    public async Task The_platform_bearer_setup_reads_the_query_token_for_a_library_hub_outside_hub()
    {
        using var application = BuildPlatform(services => services.AddSingleton<INhLibraryHubPath>(new FixedHubPath("/hubs/assistant")));
        var options = application.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));

        var moved = await ReceiveAsync(application.Services, options, scheme, "/hubs/assistant/negotiate");
        var below = await ReceiveAsync(application.Services, options, scheme, "/hub/background-operations");
        var api = await ReceiveAsync(application.Services, options, scheme, "/api/projects");

        Assert.Equal(("token-1", "division-1"), (moved.Token, moved.HttpContext.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId].ToString()));
        Assert.Equal("token-1", below.Token);
        // Elsewhere the token comes from the Authorization header only.
        Assert.Null(api.Token);
        Assert.False(api.HttpContext.Request.Headers.ContainsKey(Constants.HttpHeaderKeys.ActiveDivisionId));
    }

    private static async Task<MessageReceivedContext> ReceiveAsync(
        IServiceProvider services,
        JwtBearerOptions options,
        AuthenticationScheme scheme,
        string path)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Path = path;
        httpContext.Request.QueryString = new QueryString("?access_token=token-1&divisionId=division-1");
        var context = new MessageReceivedContext(httpContext, scheme, options);
        await options.Events.OnMessageReceived(context);
        return context;
    }

    private static WebApplication BuildPlatform(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "hub-query-token-tests",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "hub-query-token-tests-signing-key-2026",
        });
        builder.Services.AddNewHeapPlatformAspNetCommon<
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
            HubTokenDbContext,
            NhUserManager,
            NhDivisionService,
            NhDivisionMutateModel,
            NhDivisionUserService,
            NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build());
        builder.Services.AddSingleton(Substitute.For<IUserStore<NhUser>>());
        builder.Services.AddIdentityCore<NhUser>();
        configure(builder.Services);
        return builder.Build();
    }

    private sealed class FixedHubPath(string? hubPath) : INhLibraryHubPath
    {
        public string? HubPath => hubPath;
    }

    private sealed class HubTokenDbContext(DbContextOptions<HubTokenDbContext> options) : NhIdentityDbContext(options);
}
