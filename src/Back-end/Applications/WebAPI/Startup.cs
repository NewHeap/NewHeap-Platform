using Hangfire;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Media;
using NewHeap.Media.EventHandlers;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models.Options;
using System;
using System.Security.Claims;
using WebAPI.DAL;
using WebAPI.Services;
using WebAPI.Jobs;
using WebAPI.DAL.Entities;
using Scalar.AspNetCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Events.Cap;
using NhMedia;
using System.Threading.Tasks;
using WebAPI.Consumers;
using WebAPI.EventHandlers;
using static NewHeap.Platform.Common.Constants;
using NewHeap.Platform.Common.Utilities;
using NewHeap.Platform.Common;


namespace WebAPI;

public class Startup
{
#if DEBUG
    public static readonly ILoggerFactory AppLoggerFactory = LoggerFactory.Create(builder => { builder.AddConsole(); });
#endif
    private readonly IWebHostEnvironment _currentEnvironment;

    public Startup(IConfiguration configuration, IWebHostEnvironment env)
    {
        _currentEnvironment = env;
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOpenApi("v1");
        services.AddHostedService<ExampleProducer>();
        var newHeapPlatformOptions = NewHeapAspNetCommonOptions.Builder(Configuration)
            .ConfigureAutoMapper(options => options.AddMaps(typeof(Startup)))
            .ConfigureJwtBearerValidationOptions(opt =>
            {
                opt.ConfigureNhJwtBearerValidationOptions(Configuration);
            })
            .ConfigureAuthorization(options =>
            {
                options.AddPolicy("nh-media", p => p.Requirements.Add(new ShouldBeNhMediaEndpointRequirement()));

                // Optional, default is configured, only override if needed
                options.AddPolicy("app.developer.general",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.developer.general"));
                options.AddPolicy("app.division.view",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.division.view"));
                options.AddPolicy("app.division.manage",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.division.manage"));

                options.AddPolicy("app.division.access-all",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission,
                        DivisionPermissionClaimValues.AccessAll));

                options.AddPolicy("app.address.view",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.address.view"));
                options.AddPolicy("app.address.manage",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.address.manage"));

                options.AddPolicy("app.user.view",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.user.view"));
                options.AddPolicy("app.user.manage",
                    policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.user.manage"));

                // Sample division permission policy
                options.AddPolicy("app.active-division.general.view",
                    policy => policy.RequireActiveDivisionAccess(null,
                        new Claim(NhPlatformClaimTypes.DivisionPermission, "general.view")));
            })
            .ConfgureCommonOptions(NewHeapCommonOptions
                .Builder(Configuration)
                .UseOtlpUseExporter(!string.IsNullOrWhiteSpace(Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                .Build()
            )
            .Build();

        services
            .AddNhMedia(opt =>
            {
                opt.AddEventHandler<MediaLibraryEventHandler>();
                opt.UseSqlServerFileStructureStorage(Configuration.GetConnectionString("DefaultConnection")!, db =>
                {
                    db.Scheme = "medialibrary";
                    db.RunMigrations = true; // Defaults to true, here for demonstration purposes
                });
                // opt.AddAuthentication<LoggedInUserMediaAuthorizationModule>();
                opt.UseFileSystemMediaStorage(Configuration);
            })
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
                AppDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel
            >(newHeapPlatformOptions)
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(options =>
            {
                options.AddMicrosoftOAuth(opt =>
                {
                    Configuration.GetSection($"{NewHeapCommonOptions.DefaultSettingsPrefix}:MicrosoftAuthSettings")
                        .Bind(opt.Settings);
                });

                options.AddUserNamePasswordAuthentication(authOptions =>
                {
                    authOptions.EnableRefreshToken = true;
                    authOptions.AccessTokenCookieName = "nh_auth_cookie";
                    authOptions.RefreshTokenCookieName = "nh_refresh_cookie";
                    authOptions.EnableDivisions = true;
                    authOptions.EnableImpersonate = true;
                    authOptions.AuthenticationServiceKey = "";
                    authOptions.Enabled = true;
                });
            })
            .ConfigureCommon(commonConfig =>
            {
                commonConfig
                    .WithMail(x =>
                        Configuration.GetSection($"{NewHeapCommonOptions.DefaultSettingsPrefix}:MailServiceSettings")
                            .Bind(x))
                    ;
            })
            .WithEvents(events =>
            {
                events.AddCap(cap =>
                {
                    cap.WithOptions(capOptions =>
                        {
                            capOptions.UseEntityFramework<AppDbContext>();
                            capOptions.UseRabbitMQ(r =>
                            {
                                r.HostName = "localhost";
                                r.Password = "guest";
                                r.UserName = "guest";
                                r.VirtualHost = "nh-default";
                            });
                        })
                        .WithPublishing()
                        .AddSubscriber<ExampleConsumer, ExampleEvent>()
                        .AddCustomTopicSubscriber<ExampleCustomTopicConsumer>()
                        ;
                });
            })
            .WithIdentityEntityFramework(x =>
            {
                x.UseSqlServer(Configuration.GetConnectionString("DefaultConnection"))
#if DEBUG
                    .UseLoggerFactory(AppLoggerFactory);
#endif
            })
            .WithIdentity(x =>
            {
            })
            .WithDbLogService(x =>
            {
                Configuration.GetSection($"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:DbLogServiceSettings")
                    .Bind(x);
            })
            .WithSignalR(options =>
            {
                //Optional, default is configured, only override if needed
            })
            .WithHangfire(
                Configuration.GetConnectionString("DefaultConnection")!,
                hangfireOptions =>
                {
                    //Optional, default is configured, only override if needed
                }, consoleOptions =>
                {
                    //Optional, default is configured, only override if needed
                }, backgroundJobServerOptions => {
                    if ((Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "").Equals("development", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var name = $"DEV-{Environment.MachineName}".ToLower().Trim().SafeMaxStringLength(50);
                        backgroundJobServerOptions.ServerName = name;
                        backgroundJobServerOptions.Queues = [NhHangfireUtil.GetQueueName()];
                    }
                })
            .WithNotifications(x =>
            {
                x.ProcessingMaxRetryAttempts = 3;
                x.ProcessingRetentionPeriod = TimeSpan.FromDays(30);
                x.ProcessingCleanupInterval = TimeSpan.FromHours(1);
                x.ProcessingLockTimeout = TimeSpan.FromMinutes(1);
            })
            .ConfigureEmailNotificationSettings(x =>
            {
                x.AllowDefaultFromAddress = true;
                x.DefaultFromAddress = "info@newheap.com";
                x.DefaultFromName = "NewHeap";
            })
            ;

        services.AddScopedNhDbRepository<Address>();
        services.AddScoped<AddressService>();
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IServiceProvider services)
    {
        //app.UseCors(c =>
        //{
        //    c.AllowAnyOrigin();
        //    c.AllowAnyMethod();
        //    c.AllowAnyHeader();
        //});
        app.UseNewHeapPlatformAspNetCommon(env, services,
                NewHeapPlatformAspNetCommonApplicationBuilderOptions.Builder
                    .UseEndpoints(e =>
                    {
                        e.MapOpenApi();
                        e.MapScalarApiReference("/scalar");
                        e.MapNhMediaEndpoints(options =>
                        {
                            // options.ConfigureAllRoutes(builder => builder.RequireAuthorization(p =>
                            // {
                            //     
                            //     // p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                            //     // p.RequireAuthenticatedUser();
                            // }));
                        });
                    })
                    .UseHsTs(true)
                    .UseHttpsRedirection(true)
                    .UseHsTs(!env.IsDevelopment())
                    .UseHttpsRedirection(!env.IsDevelopment())
                    .UseMiddlewares(() =>
                    {
                        // Optional, default is configured, only override if needed
                    })
                    .Build()
            )
            .UseNhAuthentication<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole,
                NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel,
                NhClaimViewModel>(configure =>
            {
                configure.AddUserNamePasswordEndpoint();
                configure.AddMicrosoftOauthEndpoints();
                // Remove account information endpoint if not needed
                //configure.RemoveEndpoint<NhAccountInformationEndpointHandler<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>>();
            })
            .UseStaticFiles(options =>
            {
                // Optional, default is configured, only override if needed
            })
            .UseExceptionHandler(options =>
            {
                // Optional, default is configured, only override if needed
            })
            .UseHangfireDashboard("/hangfire", options =>
            {
                // Optional, default is configured, only override if needed
            })
            ;



        NhHangfireUtil.BackgroundJob.Enqueue<DatabaseJobs>(x => x.Seed());
        //NhHangfireUtil.RecurringJob.AddOrUpdate<DatabaseJobs>("ZAAD", x => x.Seed(), "* * * *");
    }
}

public class ShouldBeNhMediaEndpointRequirement : IAuthorizationRequirement
{
}

public class ShouldBeNhMediaEndpointRequirementHandler : AuthorizationHandler<ShouldBeNhMediaEndpointRequirement>
{
    private readonly IHttpContextAccessor _accessor;

    public ShouldBeNhMediaEndpointRequirementHandler(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ShouldBeNhMediaEndpointRequirement requirement)
    {
        var httpContext = _accessor.HttpContext;
        if (httpContext?.IsNhMediaEndpoint() == true)
        {
            context.Succeed(requirement);
        }
    }
}