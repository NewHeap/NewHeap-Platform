using Hangfire;
using Hangfire.Console;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Identity.Describers;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Resolvers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.AspNet.Policy.AuthorizationHandlers;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Localization;
using NewHeap.Platform.Common.Translations;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Text.Json.Serialization;
using static NewHeap.Platform.AspNet.Common.Models.JsonQueryModelBinder;

namespace NewHeap.Platform.AspNet.Common;

public partial class NewHeapPlatformAspNetCommonConfigurator<
    TUser,
    TUserRole,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TLog,
    TLogMessageArgument,
    TLogFile,
    TLogMessageTranslated,
    TDbLogService,
    TDbContext,
    TUserManager,
    TDivisionService,
    TDivisionMutateModel,
    TDivisionUserService,
    TDivisionUserMutateModel
> : INewHeapPlatformAspNetCommonConfigurator<TUser, TUserRole, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TLog, TLogMessageArgument, TLogFile, TLogMessageTranslated, TDbLogService, TDbContext, TUserManager, TDivisionService, TDivisionMutateModel, TDivisionUserService, TDivisionUserMutateModel>, INewHeapNotificationConfigurator<TUser, TUserRole, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TLog, TLogMessageArgument, TLogFile, TLogMessageTranslated, TDbLogService, TDbContext, TUserManager, TDivisionService, TDivisionMutateModel, TDivisionUserService, TDivisionUserMutateModel> where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>, new()
    where TUserRole : NhUserRole, new()
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>,
    new()
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision,
        TUser>, new()
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision,
        TUser>, new()
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole,
        TDivision, TUser>, new()
    where TDivisionRoleClaim : NhDivisionRoleClaim, new()
    where TLog : NhLog<TUser, TLogMessageArgument, TLogMessageTranslated, TLogFile, TDivision, TDivisionUser,
        TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>, new()
    where TLogMessageArgument : NhLogMessageArgument, new()
    where TLogFile : NhLogFile, new()
    where TLogMessageTranslated : NhLogMessageTranslated, new()
    where TDbLogService : NhDbLogService<
        TLog,
        TUser,
        TLogMessageArgument,
        TLogMessageTranslated,
        TLogFile,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim
    >
    where TDbContext : NhIdentityDbContext<
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TUser,
        TUserRole,
        TLog,
        TLogMessageArgument,
        TLogFile,
        TLogMessageTranslated
    >
    where TUserManager : UserManager<TUser>, INhUserManager<TUser>
    where TDivisionService : NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
        TDivisionRoleClaim, TDivisionMutateModel>
    where TDivisionMutateModel : NhDivisionMutateModel
    where TDivisionUserService : NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole
        , TDivisionRoleClaim, TDivisionUserMutateModel>
    where TDivisionUserMutateModel : NhDivisionUserMutateModel
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
        _serviceCollection.AddSingleton<IHttpCollectionProcessingService, HttpCollectionProcessingService>();

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
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            ;

        _serviceCollection.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        _serviceCollection.AddScoped<ExceptionHandlerService>();

        _serviceCollection.AddMvcCore();
        _serviceCollection.AddControllers(options =>
        {
            var workerProvider =
                options.ModelBinderProviders.First(p => p.GetType() == typeof(ComplexObjectModelBinderProvider));
            options.ModelBinderProviders.Insert(options.ModelBinderProviders.IndexOf(workerProvider),
                new JsonQueryModelBinderProvider());
        });

        _serviceCollection.AddCors(options =>
        {
            _options.CorsOptionsAction?.Invoke(options);
        });

        _serviceCollection.AddAutoMapper(options =>
        {
            options.AddMaps(typeof(NewHeapPlatformAspNetCommonConfigurator<
                TUser,
                TUserRole,
                TDivision,
                TDivisionUser,
                TDivisionRole,
                TDivisionUserRole,
                TDivisionRoleClaim,
                TLog,
                TLogMessageArgument,
                TLogFile,
                TLogMessageTranslated,
                TDbLogService,
                TDbContext,
                TUserManager,
                TDivisionService,
                TDivisionMutateModel,
                TDivisionUserService,
                TDivisionUserMutateModel
            >));

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
            //        var userManager = context.RequestServices.GetService<INhUserManager>();
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
        _serviceCollection.TryAddSingleton<IStringLocalizerFactory, NhResourceManagerStringLocalizerFactory>();
        _serviceCollection.TryAddTransient(typeof(IStringLocalizer<>), typeof(StringLocalizer<>));

        _serviceCollection.Configure<NhLocalizationOptions>(opts =>
        {
            opts.AssemblyNameLocalizationOptions.Add(new NhLocalizationOptions.Entry
            {
                AssemblyName = typeof(NewHeapPlatformCommonConfigurator).Assembly.GetName(),
                Options = new LocalizationOptions { ResourcesPath = "Resources" },
                Order = 100
            });

            opts.AssemblyNameLocalizationOptions.Add(new NhLocalizationOptions.Entry
            {
                AssemblyName = typeof(NewHeapPlatformAspNetCommonApplicationBuilder).Assembly.GetName(),
                Options = new LocalizationOptions { ResourcesPath = "Resources" },
                Order = 99
            });

            _options.LocalizationOptionsAction?.Invoke(opts);
        });
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        AddAuthentication<TUserViewModel, TDivisionViewModel, TClaimViewModel>(
            Action<NhAuthenticationBuilder<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
                TDivisionRoleClaim, TUserViewModel, TDivisionViewModel, TClaimViewModel>>? configure = null)
        where TUserViewModel : NhUserViewModel<TDivisionViewModel>
        where TDivisionViewModel : NhDivisionViewModel
        where TClaimViewModel : NhClaimViewModel
    {
        var builder =
            new NhAuthenticationBuilder<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
                TDivisionRoleClaim, TUserViewModel, TDivisionViewModel, TClaimViewModel>();
        configure?.Invoke(builder);

        builder.Build(_serviceCollection);
        return this;
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        ConfigureCommon(
            Action<NewHeapPlatformCommonConfigurator> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        action.Invoke(_commonConfigurator);

        return this;
    }

    public static void ConfigureWithIdentityEntityFramework(IServiceCollection serviceCollection)
    {
        #region Repositories

        serviceCollection.AddScopedNhDbRepository<TUser>();
        serviceCollection.AddScopedNhDbRepository<TUserRole>();
        serviceCollection.AddScopedNhDbRepository<TDivision>();
        serviceCollection.AddScopedNhDbRepository<TDivisionRole>();
        serviceCollection.AddScopedNhDbRepository<TDivisionRoleClaim>();
        serviceCollection.AddScopedNhDbRepository<TDivisionUser>();
        serviceCollection.AddScopedNhDbRepository<TDivisionUserRole>();
        serviceCollection.AddScopedNhDbRepository<TLog>();
        serviceCollection.AddScopedNhDbRepository<TLogMessageArgument>();
        serviceCollection.AddScopedNhDbRepository<TLogMessageTranslated>();
        serviceCollection.AddScopedNhDbRepository<TLogFile>();
        serviceCollection.AddScopedNhDbRepository<NhNotification>();
        serviceCollection.AddScopedNhDbRepository<NhNotificationDelivery>();
        serviceCollection.AddScopedNhDbRepository<NhUserNotification>();
        serviceCollection.AddScopedNhDbRepository<NhUserNotificationMessage>();
        serviceCollection.AddScopedNhDbRepository<NhUserAuthRefreshToken>();

        #endregion

        serviceCollection.AddScoped<TUserManager, TUserManager>();

        // Important, override the default UserManager with our custom one.
        //Microsoft.AspNetCore.Identity.UserManager
        serviceCollection.AddScoped<UserManager<TUser>>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TUserManager>();
        });

        // Do like this, allow sub projects to register their own 2.
        serviceCollection.AddScoped<INhUserManager>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TUserManager>();
        });

        serviceCollection.AddScoped<INhUserManager<TUser>>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TUserManager>();
        });

        serviceCollection.AddScoped<TDivisionService>();
        serviceCollection.AddScoped<TDivisionUserService>();
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        WithEvents(Action<NhEventConfigurationBuilder> configure)
    {
        var builder = new NhEventConfigurationBuilder(_serviceCollection);
        configure.Invoke(builder);
        return this;
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        WithIdentityEntityFramework(Action<DbContextOptionsBuilder> dbOptionsAction)
    {
        if (IdentityEntityFrameworkConfigured)
        {
            throw new InvalidOperationException("EntityFramework has already been configured.");
        }

        _serviceCollection.AddSingleton<InternalNhIdentityDbContextFactory<
            TDbContext,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TUser,
            TUserRole,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated
        >>();

        _serviceCollection
            .AddDbContext<TDbContext>(dbOptionsAction);

        _serviceCollection.AddScoped<INhIdentityDbContext>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TDbContext>();
        });

        // Do like this, allow sub projects to register their own 2.
        _serviceCollection.AddScoped<NhIdentityDbContext<
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TUser,
            TUserRole,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated
        >>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TDbContext>();
        });

        ConfigureWithIdentityEntityFramework(_serviceCollection);

        IdentityEntityFrameworkConfigured = true;

        return this;
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        WithIdentity(
            Action<IdentityOptions>? identityOptionsAction = null)
    {
        _serviceCollection.AddIdentity<TUser, TUserRole>()
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
            options.User.AllowedUserNameCharacters = "";
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromHours(1);
            options.Lockout.MaxFailedAccessAttempts = 7;
            options.Lockout.AllowedForNewUsers = true;
            options.SignIn.RequireConfirmedEmail = true;

            identityOptionsAction?.Invoke(options);
        };

        _serviceCollection.Configure(defaultIdentityOptionsAction);
        _serviceCollection
            .AddSingleton<IAuthorizationHandler, ActiveDivisionAccessHandler<TUser, TDivision, TDivisionUser,
                TDivisionRole, TDivisionUserRole, TDivisionRoleClaim>>();

        return this;
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        WithDbLogService(
            Action<DbLogServiceSettings> settingsAction)
    {
        _serviceCollection.Configure(settingsAction);

        _serviceCollection.AddScoped<INhDbLogService, TDbLogService>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<TDbLogService>();
        });

        _serviceCollection.AddScoped<TDbLogService>();

        return this;
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        WithSignalR(Action<HubOptions>? hubOptionsAction = null)
    {
        _serviceCollection.AddSignalR(options =>
        {
            hubOptionsAction?.Invoke(options);
        });

        return this;
    }

    public INewHeapPlatformAspNetCommonConfigurator<
            TUser,
            TUserRole,
            TDivision,
            TDivisionUser,
            TDivisionRole,
            TDivisionUserRole,
            TDivisionRoleClaim,
            TLog,
            TLogMessageArgument,
            TLogFile,
            TLogMessageTranslated,
            TDbLogService,
            TDbContext,
            TUserManager,
            TDivisionService,
            TDivisionMutateModel,
            TDivisionUserService,
            TDivisionUserMutateModel
        >
        WithHangfire(
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

    public INewHeapNotificationConfigurator<
        TUser,
        TUserRole,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TLog,
        TLogMessageArgument,
        TLogFile,
        TLogMessageTranslated,
        TDbLogService,
        TDbContext,
        TUserManager,
        TDivisionService,
        TDivisionMutateModel,
        TDivisionUserService,
        TDivisionUserMutateModel
    > WithNotifications(Action<NhNotificationSettings> settingsAction)
    {
        _serviceCollection.Configure(settingsAction);
        _serviceCollection.AddScoped<INhNotificationService, NhNotificationService>();
        _serviceCollection.AddHostedService<NhNotificationProcessingService>();

        //Default dispatchers
        _serviceCollection.AddScoped<INhNotificationDispatcher, NhEmailNotificationDispatcher>();
        _serviceCollection.AddScoped<INhNotificationDispatcher, NhUserNotificaitonNotificationDispatcher>();
        _serviceCollection.Configure<NhEmailNotificationSettings>(x => { });

        // User notifications
        _serviceCollection.AddScoped<INhUserNotificationService, NhUserNotificationService>();

        return this;
    }

    public INewHeapNotificationConfigurator<
            TUser, TUserRole, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole,
            TDivisionRoleClaim, TLog, TLogMessageArgument, TLogFile, TLogMessageTranslated, TDbLogService, TDbContext,
            TUserManager, TDivisionService, TDivisionMutateModel, TDivisionUserService, TDivisionUserMutateModel>
        ConfigureEmailNotificationSettings(Action<NhEmailNotificationSettings> configure)
    {
        _serviceCollection.Configure<NhEmailNotificationSettings>(configure);
        return this;
    }
}
