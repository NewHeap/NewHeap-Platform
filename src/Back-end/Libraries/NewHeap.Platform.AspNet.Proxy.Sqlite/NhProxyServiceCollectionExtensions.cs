using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>Registers YARP, literal redirects, MVC administration and SQLite storage.</summary>
public static class NhProxyServiceCollectionExtensions
{
    /// <summary>Binds proxy options from the supplied section and storage options from its Sqlite child.</summary>
    public static IServiceCollection AddNewHeapProxy(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddNewHeapProxy(
            options => configuration.Bind(options),
            options => configuration.GetSection(NhProxySqliteOptions.ConfigurationSectionName).Bind(options));
    }

    public static IServiceCollection AddNewHeapProxy(
        this IServiceCollection services,
        Action<NhProxyOptions>? configure = null,
        Action<NhProxySqliteOptions>? configureStorage = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new NhProxyOptions();
        var storageOptions = new NhProxySqliteOptions();
        configure?.Invoke(options);
        configureStorage?.Invoke(storageOptions);

        if (options.Limits is null || options.Limits.MaximumRulesPerEngine <= 0)
        {
            throw new ArgumentException("MaximumRulesPerEngine must be positive.", nameof(configure));
        }

        services.AddSingleton<IOptions<NhProxyOptions>>(Options.Create(options));
        services.AddSingleton<IOptions<NhProxySqliteOptions>>(Options.Create(storageOptions));
        services.AddSingleton<INhProxyConfigurationValidator, NhProxyConfigurationValidator>();
        services.AddSingleton<INhProxyConfigurationStore, NhProxySqliteConfigurationStore>();
        services.AddSingleton<INhProxyConfigurationService, NhProxyConfigurationService>();
        services.AddSingleton<INhProxyLoginAuditStore, NhProxySqliteLoginAuditStore>();
        services.AddSingleton<INhProxyAdministrationService, NhProxyAdministrationService>();
        services.AddSingleton<NhProxyRuntime>();
        services.AddSingleton<INhProxyRuntime>(provider => provider.GetRequiredService<NhProxyRuntime>());
        services.AddHostedService<NhProxyInitializationService>();

        services.AddControllersWithViews().AddApplicationPart(typeof(NhProxyAdminController).Assembly);
        services.AddAuthentication().AddCookie(NhProxyOptions.AuthenticationScheme, cookie =>
        {
            cookie.Cookie.Name = "NewHeapProxy.Session";
            cookie.Cookie.Path = NhProxyOptions.AdministrationPath;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            cookie.SlidingExpiration = false;
            cookie.Events = new CookieAuthenticationEvents
            {
                OnSigningIn = context =>
                {
                    context.CookieOptions.Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : NhProxyOptions.AdministrationPath;
                    return Task.CompletedTask;
                },
                OnSigningOut = context =>
                {
                    context.CookieOptions.Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : NhProxyOptions.AdministrationPath;
                    return Task.CompletedTask;
                },
                OnValidatePrincipal = context =>
                {
                    if (context.Principal?.FindFirst("newheap-proxy.credential-stamp")?.Value != NhProxyAdministrationService.CredentialStamp(options.Administrator))
                    {
                        context.RejectPrincipal();
                    }

                    return Task.CompletedTask;
                },
                OnRedirectToLogin = context =>
                {
                    context.Response.Redirect(context.Request.PathBase + "/Login");
                    return Task.CompletedTask;
                }
            };
        });
        var schemeProvider = services.LastOrDefault(service => service.ServiceType == typeof(IAuthenticationSchemeProvider));
        if (schemeProvider?.ImplementationType == typeof(AuthenticationSchemeProvider))
        {
            services.Remove(schemeProvider);
            services.AddSingleton<IAuthenticationSchemeProvider, NhProxyAuthenticationSchemeProvider>();
        }

        services.AddAuthorization(authorization => authorization.AddPolicy(NhProxyOptions.AdministrationPolicy,
            policy => policy.AddAuthenticationSchemes(NhProxyOptions.AuthenticationScheme).RequireAuthenticatedUser()));

        var yarp = services.AddReverseProxy().LoadFromMemory([], []);
        options.YarpConfiguration?.Invoke(yarp);

        return services;
    }
}
