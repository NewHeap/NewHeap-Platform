using Hangfire;
using Hangfire.Console;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Identity.Describers;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Resolvers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Policy.AuthorizationHandlers;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Translations;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;

namespace NewHeap.Platform.AspNet.Common;

public partial class NewHeapPlatformAspNetCommonConfigurator
{
    private bool IdentityEntityFrameworkConfigured = false;
    private readonly NewHeapPlatformCommonConfigurator _commonConfigurator;
    private readonly NewHeapAspNetCommonOptions _options;
    private readonly IServiceCollection _serviceCollection;

    public NewHeapPlatformAspNetCommonConfigurator(
        IServiceCollection serviceCollection,
        NewHeapPlatformCommonConfigurator commonConfigurator,
        NewHeapAspNetCommonOptions options
    )
    {
        _serviceCollection = serviceCollection;
        _commonConfigurator = commonConfigurator;
        _options = options;

        AddDefault();
    }

    private void AddDefault()
    {
        //Must register the options object as a singleton so it can be injected into the DbContext etc.
        _serviceCollection.AddSingleton(_options);
        _serviceCollection.Configure(_options.SettingsAction);

        AddAuthenticationAuthorization();
        AddLocalization();
        AddHttpRelated();
        AddRequestLocalization();
        AddOpenTelementry();

        _serviceCollection.AddHealthChecks();

        #region Services
        _serviceCollection.AddScoped<RazorViewService>();
        _serviceCollection.AddSingleton<IAuthorizationHandler, ActiveDivisionAccessHandler>();
        _serviceCollection.AddSingleton<IHttpCollectionProcessingService, HttpCollectionRequestProcessingService>();
        #endregion
    }
    
    private void AddOpenTelementry()
    {
        _serviceCollection.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });
    }

    private void AddHttpRelated()
    {
        _serviceCollection.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        _serviceCollection.AddTransient<IConfigureOptions<MvcNewtonsoftJsonOptions>, MvcNewtonsoftJsonOptionsWrapper>();

        _serviceCollection.AddMvc(options =>
            {
                options.EnableEndpointRouting = false;

                _options.MvcOptionsAction?.Invoke(options);
            })
            .AddNewtonsoftJson( /* Options are configured by MvcNewtonsoftJsonOptionsWrapper */)
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization(options =>
            {
                options.DataAnnotationLocalizerProvider =
                    (type, factory) => factory.Create(typeof(SharedDataAnnotationRecources));

                _options.MvcDataAnnotationsLocalizationOptionsAction?.Invoke(options);
            })
            .ConfigureApiBehaviorOptions(options =>
            {
                options.SuppressConsumesConstraintForFormFileParameters = true;
                options.SuppressInferBindingSourcesForParameters = true;
                options.SuppressModelStateInvalidFilter = true;
                options.SuppressMapClientErrors = true;

                _options.ApiBehaviorOptionsAction?.Invoke(options);
            })
            ;

        _serviceCollection.AddScoped<ExceptionHandlerService>();

        _serviceCollection.AddMvcCore();
        _serviceCollection.AddControllers();

        _serviceCollection.AddCors(options =>
        {
            _options.CorsOptionsAction?.Invoke(options);
        });

        _serviceCollection.AddAutoMapper(options =>
        {
            options.AddMaps(typeof(NewHeapPlatformAspNetCommonConfigurator));

            _options.AutoMapperConfigurationAction?.Invoke(options);
        });
    }

    private void AddRequestLocalization()
    {
        _serviceCollection.Configure<RequestLocalizationOptions>(options =>
        {
            var settings = new NewHeapAspNetCommonSettings();
            _options.SettingsAction.Invoke(settings);

            var defaultCulture = !string.IsNullOrWhiteSpace(settings.DefaultCulture)
                ? settings.DefaultCulture
                : "en-US";

            var supportedCultures = (settings.SupportedCultures ?? [])
                .Select(x => new CultureInfo(x))
                .ToList();

            options.DefaultRequestCulture = new RequestCulture(defaultCulture, defaultCulture);
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;

            //Important: we insert at position 2 so that Culture via QueryString and Cookie will override this.
            // Disabled for now, I think we go with the approach that front-end should always send the culture
            //options.RequestCultureProviders.Insert(2, new CustomRequestCultureProvider(async context =>
            //{
            //    string culture = null;

            //    if (context?.User?.Identity?.IsAuthenticated == true)
            //    {
            //        var userManager = context.RequestServices.GetService<NhUserManager>();
            //        var userEmail = context.User.FindFirstValue(ClaimTypes.Email);

            //        if (userEmail != null)
            //        {
            //            var user = await userManager.FindByEmailAsync(userEmail);

            //            if (null != user && null != user.UserSettings)
            //            {
            //                culture = user.UserSettings.Culture;
            //            }

            //            culture = "en-US";
            //        }
            //    }

            //    return (culture != null) ? new ProviderCultureResult(culture) : null;
            //}));
        });
    }

    private void AddAuthenticationAuthorization()
    {
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
        JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

        #region Authentication

        var tokenValidationParams = new TokenValidationParameters
        {
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero
        };
        
        _options.JwtBearerOptionsTokenValidationParametersAction.Invoke(tokenValidationParams);
        
        _serviceCollection.AddSingleton(tokenValidationParams);
        
        
        _serviceCollection.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;

            _options.AuthenticationOptionsAction?.Invoke(options);
        }).AddJwtBearer(cfg =>
        {
            cfg.RequireHttpsMetadata = true;
            cfg.SaveToken = true;

            cfg.TokenValidationParameters = tokenValidationParams;

            cfg.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var path = context.HttpContext.Request.Path;
                    if (path.StartsWithSegments("/hub"))
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            context.Token = accessToken;
                        }

                        var divisionId = context.Request.Query["divisionId"];
                        if (!string.IsNullOrEmpty(divisionId))
                        {
                            context.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] = divisionId;
                        }
                    }

                    return Task.CompletedTask;
                }
            };
            
        });

        #endregion

        #region Authorization

        _serviceCollection.AddAuthorization(options =>
        {
            _options.AuthorizationOptionsAction?.Invoke(options);
        });

        #endregion
    }

    private void AddLocalization()
    {
        _serviceCollection.AddLocalization(options =>
        {
            options.ResourcesPath = "Resources";

            _options.LocalizationOptionsAction?.Invoke(options);
        });
    }

    public NewHeapPlatformAspNetCommonConfigurator AddAuthentication(Action<NhAuthenticationBuilder>? configure = null)
    {
        var builder = new NhAuthenticationBuilder();
        configure?.Invoke(builder);
        
        builder.Build(_serviceCollection);
        return this;
    }
    
    public NewHeapPlatformAspNetCommonConfigurator ConfigureCommon(
        Action<NewHeapPlatformCommonConfigurator> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        action.Invoke(_commonConfigurator);

        return this;
    }

    public static void ConfigureWithIdentityEntityFramework<TDbContext, TUserManager>(IServiceCollection serviceCollection)
        where TDbContext : NhIdentityDbContext
        where TUserManager : class, INhUserManager
    {
        void AddRepository<TEntity>()
            where TEntity : class
        {
            serviceCollection.AddScoped<IRepository<TEntity>>(serviceProvider =>
            {
                var dbContext = serviceProvider.GetRequiredService<TDbContext>();
                return new Repository<TEntity>(dbContext);
            });
        }

        #region Repositories
        AddRepository<User>();
        AddRepository<UserRole>();
        AddRepository<Division>();
        AddRepository<DivisionRole>();
        AddRepository<DivisionRoleClaim>();
        AddRepository<DivisionUser>();
        AddRepository<DivisionUserRole>();
        AddRepository<Log>();
        AddRepository<LogMessageArgument>();
        AddRepository<LogMessageTranslated>();
        AddRepository<LogFile>();
        #endregion

        serviceCollection.AddScoped<TUserManager, TUserManager>();

        // Do like this, allow sub projects to register their own 2.
        serviceCollection.AddScoped<INhUserManager>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TUserManager>();
        });

        serviceCollection.AddScoped<DivisionService>();
        serviceCollection.AddScoped<DivisionUserService>();
    }

    public NewHeapPlatformAspNetCommonConfigurator WithIdentityEntityFramework<TDbContext, TUserManager>(
        Action<DbContextOptionsBuilder> dbOptionsAction)
        where TDbContext : NhIdentityDbContext
        where TUserManager : class, INhUserManager
    {
        if(IdentityEntityFrameworkConfigured)
        {
            throw new InvalidOperationException("EntityFramework has already been configured.");
        }

        _serviceCollection.AddSingleton<InternalNhIdentityDbContextFactory<TDbContext>>();

        _serviceCollection
            .AddEntityFrameworkSqlServer()
            .AddDbContext<TDbContext>(dbOptionsAction);

        // Do like this, allow sub projects to register their own 2.
        _serviceCollection.AddScoped<NhIdentityDbContext>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TDbContext>();
        });

        ConfigureWithIdentityEntityFramework<TDbContext, TUserManager>(_serviceCollection);

        IdentityEntityFrameworkConfigured = true;

        return this;
    }

    public NewHeapPlatformAspNetCommonConfigurator WithIdentity<TDbContext>(
        Action<IdentityOptions>? identityOptionsAction = null)
          where TDbContext : NhIdentityDbContext
    {
        _serviceCollection.AddIdentity<User, UserRole>()
            .AddEntityFrameworkStores<TDbContext>()
            .AddDefaultTokenProviders()
            .AddErrorDescriber<MultiLanguageIdentityErrorDescriber>()
            ;

        Action<IdentityOptions> defaultIdentityOptionsAction = options =>
        {
            options.Password.RequiredLength = 6;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.User.RequireUniqueEmail = true;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromHours(1);
            options.Lockout.MaxFailedAccessAttempts = 7;
            options.Lockout.AllowedForNewUsers = true;
            options.SignIn.RequireConfirmedEmail = true;

            identityOptionsAction?.Invoke(options);
        };

        _serviceCollection.Configure(defaultIdentityOptionsAction);

        return this;
    }

    public NewHeapPlatformAspNetCommonConfigurator WithDbLogService(
        Action<DbLogServiceSettings> settingsAction)
    {
        _serviceCollection.Configure(settingsAction);
        _serviceCollection.AddScoped<DbLogService>();

        return this;
    }

    public NewHeapPlatformAspNetCommonConfigurator WithSignalR(Action<HubOptions>? hubOptionsAction = null)
    {
        _serviceCollection.AddSignalR(options =>
        {
            hubOptionsAction?.Invoke(options);
        });

        return this;
    }

    public NewHeapPlatformAspNetCommonConfigurator WithHangfire(
        string nameOrConnectionString,
        Action<IGlobalConfiguration>? hangfireOptionsAction = null,
        Action<ConsoleOptions>? consoleOptionsAction = null,
        Action<BackgroundJobServerOptions>? backgroundJobServerOptions = null
    )
    {
        _serviceCollection.AddHangfire(options =>
        {
            options.UseSqlServerStorage(nameOrConnectionString);

            hangfireOptionsAction?.Invoke(options);

            var consoleOptions = new ConsoleOptions();
            consoleOptionsAction?.Invoke(consoleOptions);
            options.UseConsole(consoleOptions);
        });

        _serviceCollection.AddHangfireServer(options =>
        {
            backgroundJobServerOptions?.Invoke(options);
        });

        return this;
    }
}