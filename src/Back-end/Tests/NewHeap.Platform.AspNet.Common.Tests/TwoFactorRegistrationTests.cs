using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using System.Globalization;
using System.Security.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class TwoFactorRegistrationTests
{
    [Fact]
    public async Task TwoFactorServicesBuildAndPassStartupValidation()
    {
        using var application = BuildApplication(EnableTwoFactor);
        using var scope = application.Services.CreateScope();

        var authenticationService = scope.ServiceProvider.GetRequiredService<INhAuthenticationService>();

        Assert.True(Assert.IsAssignableFrom<INhMultiFactorAuthenticationService>(authenticationService).IsTwoFactorAvailable);
        Assert.IsType<NhTwoFactorService<NhUser>>(scope.ServiceProvider.GetRequiredService<INhTwoFactorService<NhUser>>());

        await GetStartupValidator(application).StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupFailsWhenTheAuthenticationServiceCannotEnforceTwoFactor()
    {
        using var application = BuildApplication(authentication =>
        {
            EnableTwoFactor(authentication);
            authentication.WithAuthenticationService<ServiceWithoutTwoFactorContext>();
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetStartupValidator(application).StartAsync(CancellationToken.None));

        Assert.Contains(nameof(ServiceWithoutTwoFactorContext), exception.Message, StringComparison.Ordinal);
        Assert.Contains("NhTwoFactorAuthenticationContext", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationsWithoutTwoFactorKeepTheExistingServices()
    {
        using var application = BuildApplication(_ => { });
        using var scope = application.Services.CreateScope();

        var authenticationService = scope.ServiceProvider.GetRequiredService<INhAuthenticationService>();

        Assert.False(Assert.IsAssignableFrom<INhMultiFactorAuthenticationService>(authenticationService).IsTwoFactorAvailable);
        Assert.Null(scope.ServiceProvider.GetService<INhTwoFactorService<NhUser>>());
        Assert.Empty(application.Services.GetServices<IHostedService>().OfType<NhTwoFactorStartupValidator>());
    }

    [Fact]
    public void TwoFactorEndpointsAreMappedWithTheirAuthenticationIntent()
    {
        using var application = BuildApplication(EnableTwoFactor);
        application.UseRouting();

        CreateEndpointBuilder()
            .AddUserNamePasswordEndpoint(enableRefreshToken: true, enableImpersonate: false)
            .AddTwoFactorEndpoints()
            .Build(application, application.Services);

        var endpoints = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/'));

        var verify = endpoints["authentication/two-factor/verify"];
        Assert.NotNull(verify.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Contains(
            verify.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            metadata => metadata.Type == typeof(NhLoginResponse) && metadata.StatusCode == StatusCodes.Status200OK);

        foreach (var pattern in new[]
                 {
                     "account/two-factor",
                     "account/two-factor/authenticator",
                     "account/two-factor/authenticator/confirm",
                     "account/two-factor/recovery-codes",
                     "account/two-factor/disable",
                 })
        {
            var authorizeData = endpoints[pattern].Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(authorizeData, data => data.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme);
            Assert.NotNull(endpoints[pattern].Metadata.GetMetadata<IEndpointSummaryMetadata>());
        }

        Assert.Contains(
            endpoints["authentication/login"].Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            metadata => metadata.Type == typeof(NhLoginResponse));
    }

    [Fact]
    public async Task TwoFactorChangesAreRefusedWhileImpersonating()
    {
        using var application = BuildApplication(EnableTwoFactor);
        using var scope = application.Services.CreateScope();

        var handler = application.Services.GetRequiredService<NhTwoFactorDisableEndpointHandler<NhUser>>();
        var accessor = application.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(NhPlatformClaimTypes.ImpersonateOriginUserId, Guid.NewGuid().ToString()),
                ],
                JwtBearerDefaults.AuthenticationScheme)),
        };

        try
        {
            var result = await (Task<IResult>)handler.Handler.DynamicInvoke(new NhTwoFactorReauthenticationRequest())!;

            var badRequest = Assert.IsType<BadRequest<Dictionary<string, IEnumerable<string>>>>(result);
            Assert.Contains(NhTwoFactorFailureCodes.NotAllowedWhileImpersonating, badRequest.Value!.Keys);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public void MappingTwoFactorEndpointsWithoutEnablingTwoFactorExplainsTheMissingFeature()
    {
        using var application = BuildApplication(_ => { });
        application.UseRouting();

        var exception = Assert.Throws<InvalidOperationException>(() => CreateEndpointBuilder()
            .AddTwoFactorEndpoints()
            .Build(application, application.Services));

        Assert.Contains(nameof(NhTwoFactorVerifyAuthenticationHandler), exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddAuthentication", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasskeysConfigureIdentityAndMapTheirEndpoints()
    {
        using var application = BuildApplication<PasskeyRegistrationDbContext>(EnablePasskeys);
        using var scope = application.Services.CreateScope();

        Assert.IsType<PasskeyHandler<NhUser>>(scope.ServiceProvider.GetRequiredService<IPasskeyHandler<NhUser>>());

        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value;
        Assert.Equal("app.example.test", options.ServerDomain);
        Assert.Equal("required", options.UserVerificationRequirement);
        Assert.Equal("required", options.ResidentKeyRequirement);
        Assert.True(await options.ValidateOrigin!(new PasskeyOriginValidationContext
        {
            HttpContext = new DefaultHttpContext(),
            Origin = "https://app.example.test",
            CrossOrigin = false,
        }));
        Assert.False(await options.ValidateOrigin!(new PasskeyOriginValidationContext
        {
            HttpContext = new DefaultHttpContext(),
            Origin = "https://other.example.test",
            CrossOrigin = false,
        }));
        Assert.False(await options.ValidateOrigin!(new PasskeyOriginValidationContext
        {
            HttpContext = new DefaultHttpContext(),
            Origin = "https://app.example.test",
            CrossOrigin = true,
        }));

        await GetStartupValidator(application).StartAsync(CancellationToken.None);

        application.UseRouting();
        CreateEndpointBuilder()
            .AddTwoFactorEndpoints()
            .AddPasskeyEndpoints()
            .Build(application, application.Services);

        var endpoints = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single() + " " +
                endpoint.RoutePattern.RawText!.TrimStart('/'));

        foreach (var pattern in new[]
                 {
                     "POST authentication/passkey/options",
                     "POST authentication/passkey/login",
                     "POST authentication/two-factor/passkey/options",
                     "POST authentication/two-factor/passkey",
                     "POST authentication/two-factor/enrollment/passkey/options",
                     "POST authentication/two-factor/enrollment/passkey",
                 })
        {
            Assert.NotNull(endpoints[pattern].Metadata.GetMetadata<IAllowAnonymous>());
        }

        foreach (var pattern in new[]
                 {
                     "GET account/passkeys",
                     "POST account/passkeys/options",
                     "POST account/passkeys",
                     "PUT account/passkeys/{passkeyId}",
                     "POST account/passkeys/{passkeyId}/remove",
                 })
        {
            var authorizeData = endpoints[pattern].Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(authorizeData, data => data.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme);
        }
    }

    [Fact]
    public async Task StartupFailsWhenPasskeysMissTheIdentityTable()
    {
        using var application = BuildApplication(EnablePasskeys);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetStartupValidator(application).StartAsync(CancellationToken.None));

        Assert.Contains("IncludeIdentityPasskeys", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RegistrationDbContext), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingPasskeyEndpointsWithoutEnablingPasskeysExplainsTheMissingFeature()
    {
        using var application = BuildApplication(EnableTwoFactor);
        application.UseRouting();

        var exception = Assert.Throws<InvalidOperationException>(() => CreateEndpointBuilder()
            .AddPasskeyEndpoints()
            .Build(application, application.Services));

        Assert.Contains(nameof(NhPasskeySignInOptionsHandler), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFactorMessagesAreTranslatedToDutch()
    {
        using var application = BuildApplication(EnableTwoFactor);
        using var scope = application.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<NhTwoFactorMessages>>();

        var previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("nl-NL");
        try
        {
            var title = localizer["Passkey added"];
            Assert.False(title.ResourceNotFound);
            Assert.Equal("Passkey toegevoegd", title.Value);

            var message = localizer["A recovery code was used to sign in to your account. {0} codes are left.", 3];
            Assert.Contains("nog 3 codes", message.Value, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private static void EnablePasskeys(
        NhAuthenticationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole,
            NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel> authentication)
    {
        authentication.AddTwoFactor(twoFactor => twoFactor
            .EnableAuthenticator()
            .EnableRecoveryCodes()
            .EnablePasskeys(passkeys =>
            {
                passkeys.ServerDomain = "app.example.test";
                passkeys.AllowedOrigins.Add("https://app.example.test/");
            }));
    }

    private static void EnableTwoFactor(
        NhAuthenticationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole,
            NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel> authentication)
    {
        authentication.AddTwoFactor(twoFactor => twoFactor
            .EnableAuthenticator(authenticator => authenticator.Issuer = "Two-factor registration tests")
            .EnableRecoveryCodes());
    }

    private static NhAuthenticationConfigurationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole,
        NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
        NhClaimViewModel> CreateEndpointBuilder()
    {
        return new NhAuthenticationConfigurationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole,
            NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
            NhClaimViewModel>();
    }

    private static IHostedService GetStartupValidator(WebApplication application)
    {
        return application.Services.GetServices<IHostedService>().OfType<NhTwoFactorStartupValidator>().Single();
    }

    private static WebApplication BuildApplication(
        Action<NhAuthenticationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole,
            NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>> configure)
    {
        return BuildApplication<RegistrationDbContext>(configure);
    }

    private static WebApplication BuildApplication<TDbContext>(
        Action<NhAuthenticationBuilder<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole,
            NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>> configure)
        where TDbContext : NhIdentityDbContext
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://two-factor.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "two-factor-registration-tests-signing-key",
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
                TDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .AddAuthentication(configure)
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql("Host=localhost;Database=unused"))
            .WithIdentity()
            .WithDbLogService(_ => { });

        return builder.Build();
    }

    private sealed class RegistrationDbContext(DbContextOptions<RegistrationDbContext> options)
        : NhIdentityDbContext(options);

    private sealed class PasskeyRegistrationDbContext(DbContextOptions<PasskeyRegistrationDbContext> options)
        : NhIdentityDbContext(options)
    {
        protected override bool IncludeIdentityPasskeys => true;
    }

    /// <summary>
    /// A consumer service that still calls the constructor without the two-factor context.
    /// </summary>
    private sealed class ServiceWithoutTwoFactorContext : NhAuthenticationService<
        NhUser,
        NhDivision,
        NhDivisionUser,
        NhDivisionRole,
        NhDivisionUserRole,
        NhDivisionRoleClaim>
    {
        public ServiceWithoutTwoFactorContext(
            SignInManager<NhUser> signInManager,
            INhUserManager<NhUser> userManager,
            ILogger<Microsoft.AspNetCore.Authentication.AuthenticationService> logger,
            IConfiguration configuration,
            TokenValidationParameters tokenValidationParameters,
            AuthenticationConfiguration authConfiguration)
            : base(signInManager, userManager, logger, configuration, tokenValidationParameters, authConfiguration)
        {
        }
    }
}
