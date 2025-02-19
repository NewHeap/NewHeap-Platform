using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models.Options;
using System;
using System.Security.Claims;
using System.Text;
using WebAPI.DAL;

namespace WebAPI;

public class Startup
{
#if DEBUG
    public static readonly ILoggerFactory AppLoggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole();
    });
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
        services.AddNewHeapPlatformAspNetCommon<AppDbContext>(new NewHeapAspNetCommonOptions
            {
                CommonOptions =
                    new NewHeapCommonOptions
                    {
                        SettingsAction = x => Configuration.GetSection("NewHeap:PlatformCommon:Settings").Bind(x)
                    },
                SettingsAction = x => Configuration.GetSection("NewHeap:PlatformAspNetCommon:Settings").Bind(x),
                DbOptionsAction = x => x
                    .UseSqlServer(Configuration.GetConnectionString("DefaultConnection"))
#if DEBUG
                    .UseLoggerFactory(AppLoggerFactory),
                IdentityOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                },
                AuthenticationOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                },
                JwtBearerOptionsTokenValidationParametersAction = options =>
                {
                    options.ValidIssuer =
                        Configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"];
                    options.ValidAudience =
                        Configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:ValidAudience"];
                    options.IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(
                            Configuration["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"]));
                },
                JwtBearerOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                },
                AuthorizationOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                    options.AddPolicy("app.developer.general",
                        policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.developer.general"));
                    options.AddPolicy("app.division.view",
                        policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.division.view"));
                    options.AddPolicy("app.division.manage",
                        policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.division.manage"));

                    // Sample division permission policy
                    options.AddPolicy("app.active-division.general.view",
                        policy => policy.RequireActiveDivisionAccess(null,
                            new Claim(NhPlatformClaimTypes.DivisionPermission, "general.view")));
                },
                LocalizationOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                    //options.ResourcesPath = "Resources"; // Default
                },
                MvcOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                },
                MvcDataAnnotationsLocalizationOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                },
                ApiBehaviorOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                },
                CorsOptionsAction = options =>
                {
                    // Optional, default is configured, only override if needed
                },
                AutoMapperConfigurationAction = options =>
                {
                    // Optional, default is configured, only override if needed
                    options.AddMaps(typeof(Startup));
                },
                DbLogSettingsAction = x =>
                    Configuration.GetSection("NewHeap:PlatformAspNetCommon:DbLogServiceSettings").Bind(x)
            })
            .ConfigureCommon(commonConfig =>
            {
                commonConfig
                    .WithMail(x => Configuration.GetSection("NewHeap:PlatformCommon:MailServiceSettings").Bind(x))
                    .WithMicrosoftAuth(x =>
                        Configuration.GetSection("NewHeap:PlatformCommon:MicrosoftAuthSettings").Bind(x))
                    ;
            })
            .WithSignalR(options =>
            {
                //Optional, default is configured, only override if needed
            })
            .WithHangfire(
                Configuration.GetConnectionString("DefaultConnection"),
                hangfireOptions =>
                {
                    //Optional, default is configured, only override if needed
                }, consoleOptions =>
                {
                    //Optional, default is configured, only override if needed
                })
            ;
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IServiceProvider services)
    {
        app.UseNewHeapPlatformAspNetCommon(env, services,
                NhPlatformAspNetCommonOptionsBuilder.Create
                    .UseHsTs(!env.IsDevelopment())
                    .UseHttpsRedirection(!env.IsDevelopment())
                    .UseMvc(routes => { })
                    .UseEndpoints(routes => { })
                    .UseCors(builder => { })
                    .Build()
            )
            .UseStaticFiles(options =>
            {
                // Optional, default is configured, only override if needed
            })
            .UseExceptionHandler(options =>
            {
                // Optional, default is configured, only override if needed
            })
            .UserHangfireDashboard("/hangfire", options =>
            {
                // Optional, default is configured, only override if needed
            })
            ;
    }
}