using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.Actors;
using NewHeap.Platform.Common.Actors;
using System.Text.Encodings.Web;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhPlatformModuleRegistrationTests
{
    [Fact]
    public void ModulesUseTheirOwnSchemaUnlessTheContextKeepsItsDefault()
    {
        using var plain = new PlainContext(new DbContextOptionsBuilder<PlainContext>().UseNpgsql("Host=unused").Options);
        Schema<NhBackgroundOperation>(plain).Should().Be("nhops");
        Table<NhBackgroundOperation>(plain).Should().Be("BackgroundOperations");
        Schema<NhUserNotification>(plain).Should().Be("app_notify");
        Table<NhUserNotification>(plain).Should().Be("UserNotifications");

        // Identity hosting keeps the context's schema and its foreign keys to users and divisions.
        using var identity = new IdentityContext(new DbContextOptionsBuilder<IdentityContext>().UseNpgsql("Host=unused").Options);
        Schema<NhBackgroundOperation>(identity).Should().BeNull();
        Schema<NhNotification>(identity).Should().BeNull();
        identity.Model.FindEntityType(typeof(NhBackgroundOperation))!.GetForeignKeys()
            .Select(foreignKey => foreignKey.PrincipalEntityType.ClrType)
            .Should().Contain([typeof(NhUser), typeof(NhDivision), typeof(NhUserNotification)]);
        plain.Model.FindEntityType(typeof(NhBackgroundOperation))!.GetForeignKeys()
            .Select(foreignKey => foreignKey.PrincipalEntityType.ClrType)
            .Should().OnlyContain(type => type == typeof(NhBackgroundOperation));
    }

    [Fact]
    public void IdentityDefaultsReplaceGenericDefaultsAndTheApplicationReplacesBoth()
    {
        var genericFirst = new ServiceCollection();
        NhPlatformModuleRegistration.AddGenericActorDefaults(genericFirst);
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser>(genericFirst);
        Implementation<INhActorDirectory>(genericFirst).Should().Be<NhIdentityActorDirectory<NhUser>>();
        Implementation<INhActorContextAccessor>(genericFirst).Should().Be<NhIdentityActorContextAccessor>();
        Implementation<INhDivisionAccess>(genericFirst).Should().Be<NhIdentityDivisionAccess>();

        var identityFirst = new ServiceCollection();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser>(identityFirst);
        NhPlatformModuleRegistration.AddGenericActorDefaults(identityFirst);
        Implementation<INhActorDirectory>(identityFirst).Should().Be<NhIdentityActorDirectory<NhUser>>();

        var applicationFirst = new ServiceCollection();
        applicationFirst.AddNewHeapActorDirectory<ApplicationDirectory>();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser>(applicationFirst);
        NhPlatformModuleRegistration.AddGenericActorDefaults(applicationFirst);
        Implementation<INhActorDirectory>(applicationFirst).Should().Be<ApplicationDirectory>();

        var applicationLast = new ServiceCollection();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser>(applicationLast);
        applicationLast.AddNewHeapActorDirectory<ApplicationDirectory>();
        Implementation<INhActorDirectory>(applicationLast).Should().Be<ApplicationDirectory>();

        var standalone = new ServiceCollection();
        NhPlatformModuleRegistration.AddGenericActorDefaults(standalone);
        Implementation<INhActorContextAccessor>(standalone).Should().Be<NhClaimsActorContextAccessor>();
        Implementation<INhDivisionAccess>(standalone).Should().Be<NhNoDivisionAccess>();
        Implementation<INhActorDirectory>(standalone).Should().Be<NhIdActorDirectory>();
        Implementation<INhUserNotificationSink>(standalone).Should().Be<NhUserNotificationServiceSink>();
    }

    [Fact]
    public async Task ActorDefaultsReadTheRequestAndDenyUnknownDivisions()
    {
        var userId = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        httpContext.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] = divisionId.ToString();
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var claims = await new NhClaimsActorContextAccessor(accessor).GetCurrentAsync();
        claims.UserId.Should().Be(userId);
        claims.ActiveDivisionId.Should().BeNull("there are no divisions without NewHeap Identity");

        var identity = await new NhIdentityActorContextAccessor(accessor).GetCurrentAsync();
        identity.UserId.Should().Be(userId);
        identity.ActiveDivisionId.Should().Be(divisionId);

        // Without a request nobody is signed in. The accessor shares its context per async flow.
        accessor.HttpContext = null;
        (await new NhClaimsActorContextAccessor(accessor).GetCurrentAsync())
            .Should().Be(NhActorContext.Anonymous);
        (await new NhNoDivisionAccess().HasAccessAsync(NhActorRef.ForUser(userId), divisionId)).Should().BeFalse();

        var actor = new NhActorRef(NhActorKinds.Agent, "intake-reviewer");
        var directory = await new NhIdActorDirectory().GetAsync([actor, actor]);
        directory.Should().ContainSingle().Which.Value.DisplayName.Should().Be("intake-reviewer");
    }

    [Fact]
    public async Task TheDefaultSinkDeliversNothingWithoutTheNotificationsModule()
    {
        var sink = new NhUserNotificationServiceSink(new ServiceCollection().BuildServiceProvider());

        sink.CanDeliver.Should().BeFalse();
        var result = await sink.SendAsync(new NhUserNotificationRequest(Guid.NewGuid(), "Title", "Message"));
        result.Success.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task PlatformSchemesForwardToTheApplicationSchemeOnlyWhenMissing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApplicationHandler>("partner", _ => { })
            .AddScheme<AuthenticationSchemeOptions, ApplicationHandler>(JwtBearerDefaults.AuthenticationScheme, _ => { });
        services.AddNewHeapAuthenticationForwarding("partner");
        await using var provider = services.BuildServiceProvider();

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        (await schemes.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme))!.HandlerType.Should().Be<ApplicationHandler>();
        (await schemes.GetSchemeAsync(IdentityConstants.ApplicationScheme))!.HandlerType.Should().Be<PolicySchemeHandler>();
        provider.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>()
            .Get(IdentityConstants.ApplicationScheme).ForwardDefault.Should().Be("partner");
    }

    private static string? Schema<TEntity>(DbContext context)
    {
        return context.Model.FindEntityType(typeof(TEntity))!.GetSchema();
    }

    private static string? Table<TEntity>(DbContext context)
    {
        return context.Model.FindEntityType(typeof(TEntity))!.GetTableName();
    }

    private static Type? Implementation<TService>(IServiceCollection services)
    {
        return services.Last(descriptor => descriptor.ServiceType == typeof(TService)).ImplementationType;
    }

    private sealed class ApplicationDirectory : INhActorDirectory
    {
        public Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
            IReadOnlyCollection<NhActorRef> actors,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<NhActorRef, NhActorInfo>>(new Dictionary<NhActorRef, NhActorInfo>());
        }
    }

    private sealed class ApplicationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    private sealed class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddNewHeapBackgroundOperations();
            modelBuilder.AddNewHeapNotifications(notifications => notifications.Schema = "app_notify");
        }
    }

    private sealed class IdentityContext(DbContextOptions<IdentityContext> options) : NhIdentityDbContext(options);
}
