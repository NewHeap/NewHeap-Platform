using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Media;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models.Options;
using System;
using System.Security.Claims;
using System.Text;
using WebAPI.DAL;
using WebAPI.Managers;

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
        var newHeapPlatformOptions = NewHeapAspNetCommonOptions.Builder(Configuration)
            .ConfigureAutoMapper(options => options.AddMaps(typeof(Startup)))
            
            .ConfigureAuthorization(options =>
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
            })
            
            .ConfgureCommonOptions(NewHeapCommonOptions
                .Builder(Configuration)
                .UseOtlpUseExporter(!string.IsNullOrWhiteSpace(Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                .Build()
            )
            .Build();

        services
            // .AddNhMedia(opt =>
            // {
            //     opt.UseSqlServerFileStructureStorage(Configuration.GetConnectionString("DefaultConnection")!, db =>
            //     {
            //         db.Scheme = "medialibrary";
            //         db.RunMigrations = true; // Defaults to true, here for demonstration purposes
            //     });
            //     opt.UseFileSystemMediaStorage(Configuration["Media:FileSystemRoot"]!);
            // })
            .AddNewHeapPlatformAspNetCommon(newHeapPlatformOptions)
            .AddAuthentication(options =>
            {
                options.WithAuthenticationService<MockAuthenticationService>();
                options.AddUserNamePasswordAuthentication(authOptions =>
                {
                    authOptions.EnableRefreshToken = true;
                    authOptions.AccessTokenCookieName = "nh_auth_cookie";
                    authOptions.RefreshTokenCookieName = "nh_refresh_cookie";
                });
            })
            .ConfigureCommon(commonConfig =>
            {
                commonConfig
                    .WithMail(x => Configuration.GetSection($"{NewHeapCommonOptions.DefaultSettingsPrefix}:MailServiceSettings").Bind(x))
                    .WithMicrosoftAuth(x =>
                        Configuration.GetSection($"{NewHeapCommonOptions.DefaultSettingsPrefix}:MicrosoftAuthSettings").Bind(x))
                    ;
            })
            .WithIdentityEntityFramework<AppDbContext, NhUserManager>(x =>
            {
                x.UseSqlServer(Configuration.GetConnectionString("DefaultConnection"))
#if DEBUG
                .UseLoggerFactory(AppLoggerFactory);
#endif
            })
            .WithIdentity<AppDbContext>(x =>
            {

            })
            .WithDbLogService(x =>
            {
                Configuration.GetSection($"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:DbLogServiceSettings").Bind(x);
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
                })
            
            ;
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IServiceProvider services)
    {
        app.UseNewHeapPlatformAspNetCommon(env, services,
                NewHeapPlatformAspNetCommonApplicationBuilderOptions.Builder
                    .UseHsTs(!env.IsDevelopment())
                    .UseHttpsRedirection(!env.IsDevelopment())
                    .Build()
            )
            .UseNhAuthentication(configure =>
            {
                configure.AddUserNamePasswordEndpoint();
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
    }
}