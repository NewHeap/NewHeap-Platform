using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DotNetCore.CAP;
using NewHeap.Platform.AspNet;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.OpenApiSchemaTransformers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Events.Cap;
using NewHeap.Media;
using Scalar.AspNetCore;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Api.Jobs;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Core;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.UseNewHeapAspnetCommonConfiguration(args);
builder.AddNewHeapPlatformCachingDefault(options =>
{
    options.DefaultEntryOptions.Duration = TimeSpan.FromMinutes(2);
    options.DefaultEntryOptions.JitterMaxDuration = TimeSpan.FromSeconds(10);
});
builder.Services.AddOpenApi("v1", options =>
    options.AddSchemaTransformer<OneOfSchemaTransformer>());

var databaseProvider = builder.Configuration.GetDatabaseProvider();
var connectionString = builder.Configuration.GetDatabaseConnectionString();
var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException(
        "Connection string 'rabbitmq' is required. Start the API through the Aspire AppHost.");
var rabbitMqUri = new Uri(rabbitMqConnectionString);
var rabbitMqCredentials = rabbitMqUri.UserInfo.Split(':', 2);
var mediaStoragePath = Path.Combine(
    Path.GetTempPath(),
    "NewHeap",
    "SampleProjectManagement",
    "media");

var platformOptions = NewHeapAspNetCommonOptions
    .Builder(builder.Configuration)
    .ConfigureAutoMapper(options => options.AddMaps(typeof(AutomapperProfileConfiguration).Assembly))
    .ConfigureAuthorization(options =>
    {
        options.AddPolicy(
            "app.project.view",
            policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.project.view"));

        options.AddPolicy(
            "app.project.manage",
            policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.project.manage"));

        options.AddPolicy(
            "app.project.edit-or-admin",
            policy => policy.RequireAssertion(context =>
                context.User.HasClaim(NhPlatformClaimTypes.Permission, "app.project.manage") ||
                context.User.IsInRole("administrator")));

        options.AddPolicy(
            "app.active-division.project.view",
            policy => policy.RequireActiveDivisionAccess(
                null,
                new Claim(NhPlatformClaimTypes.DivisionPermission, "project.view")));

        options.AddPolicy(
            "app.active-division.project.manage",
            policy => policy.RequireActiveDivisionAccess(
                null,
                new Claim(NhPlatformClaimTypes.DivisionPermission, "project.manage")));

        options.AddPolicy(
            SampleAuthorizationPolicies.ProjectConfidentialView,
            policy => policy.RequireAnyProjectActiveDivisionAccess("confidential.view"));
    })
    .Build();

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
        SampleProjectManagementDbContext,
        NhUserManager,
        NhDivisionService,
        NhDivisionMutateModel,
        NhDivisionUserService,
        NhDivisionUserMutateModel>(platformOptions)
    .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(options =>
    {
        options.WithAuthenticationService<SampleAuthenticationService>();
        options.AddMicrosoftOAuth(oauth =>
        {
            builder.Configuration.GetSection("NewHeap:PlatformCommon:MicrosoftAuthSettings")
                .Bind(oauth.Settings);
        });
        options.AddUserNamePasswordAuthentication(authentication =>
        {
            authentication.EnableRefreshToken = true;
            authentication.EnableDivisions = true;
            authentication.EnableImpersonate = true;
            authentication.AccessTokenCookieName = "sample_project_management_auth";
            authentication.RefreshTokenCookieName = "sample_project_management_refresh";
            authentication.Enabled = true;
        });
    })
    .ConfigureCommon(common =>
    {
        common.WithMail(mail =>
            builder.Configuration.GetSection("NewHeap:PlatformCommon:MailServiceSettings").Bind(mail));
    })
    .WithEvents(events =>
    {
        events.AddCap(cap =>
        {
            cap.WithOptions(options =>
                {
                    // CAP persists the event in the same SQL transaction as the
                    // domain write. RabbitMQ is only contacted after commit.
                    options.UseConfiguredEntityFramework<SampleProjectManagementDbContext>(databaseProvider);
                    options.UseRabbitMQ(rabbitMq =>
                    {
                        rabbitMq.HostName = rabbitMqUri.Host;
                        rabbitMq.Port = rabbitMqUri.Port;
                        rabbitMq.UserName = Uri.UnescapeDataString(rabbitMqCredentials[0]);
                        rabbitMq.Password = rabbitMqCredentials.Length > 1
                            ? Uri.UnescapeDataString(rabbitMqCredentials[1])
                            : "";
                        rabbitMq.VirtualHost = string.IsNullOrWhiteSpace(rabbitMqUri.AbsolutePath.Trim('/'))
                            ? "/"
                            : Uri.UnescapeDataString(rabbitMqUri.AbsolutePath.Trim('/'));
                    });
                    options.UseStorageLock = true;
                    options.FailedRetryCount = 3;
                })
                .WithPublishing()
                .AddSubscriber<ProjectEventConsumer, ProjectCreatedEvent>()
                .AddCustomTopicSubscriber<PriorityProjectEventConsumer>();
        });
    })
    .WithIdentityEntityFramework(options => options.UseConfiguredDatabase(builder.Configuration))
    .WithIdentity(_ => { })
    .WithDbLogService(options =>
    {
        builder.Configuration
            .GetSection($"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:DbLogServiceSettings")
            .Bind(options);
    })
    .WithHangfire(connectionString, databaseProvider: databaseProvider)
    .WithBackgroundOperations(operations =>
    {
        operations.Options.OperationUrlPrefix = "/background-operations";
        operations.Options.ProgressFlushInterval = TimeSpan.FromMilliseconds(250);
        operations
            .WithGlobalConcurrency(8)
            .WithDefaultQueueConcurrency(6);
        operations.Add<ProjectPortfolioAnalysisRequest, ProjectPortfolioAnalysisOperation>(
            "sample-project-portfolio-analysis",
            operation => operation
                .WithRetry(2)
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .WithTypeConcurrency(4)
                .ExclusivePer(
                    request => NhBackgroundOperationResourceKey.ForDivisionAction(
                        "analyze-project-portfolio",
                        request.DivisionId),
                    NhBackgroundOperationConflictBehavior.ReturnExisting)
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
        // No fan-out-specific registration is needed. The child inherits
        // owner/division/priority/correlation and uses its normal handler queue.
        operations.Add<ProjectAnalysisChildRequest, ProjectAnalysisChildOperation>(
            "sample-project-analysis-child");
        operations.Add<ProjectAiPortfolioReportRequest, ProjectAiPortfolioReportOperation>(
            "sample-project-ai-portfolio-report",
            operation => operation
                .WithSoftTimeout(TimeSpan.FromMinutes(5))
                .ExclusivePer(
                    request => NhBackgroundOperationResourceKey.ForDivisionAction(
                        "generate-ai-portfolio-report",
                        request.DivisionId),
                    NhBackgroundOperationConflictBehavior.ReturnExisting)
                .RequireIdempotency(NhBackgroundOperationIdempotency.IdempotentWithKey));
    })
    .WithNotifications(NotificationProcessingSample.Configure)
    .ConfigureEmailNotificationSettings(options =>
    {
        options.AllowDefaultFromAddress = true;
        options.DefaultFromAddress = "no-reply@sample.local";
        options.DefaultFromName = "Sample Project Management";
    });

builder.Services.AddSampleProjectManagementCore();
builder.Services.AddSampleProjectManagementAi();
builder.Services.AddNewHeapPlatformAIAspNet(ai => ai
    .UseToolInvocationPurpose("project-assistance")
    .AddActiveDivisionScope("app.active-division.project.view")
    .AddCapabilityGrant(
        ProjectAiTools.ReadCapability,
        "app.active-division.project.view")
    .AddCapabilityGrant(
        ProjectAiTools.ManageCapability,
        "app.active-division.project.manage"));
builder.Services.AddScoped<IClaimsTransformation, SampleRuntimeClaimsTransformation>();
builder.Services.AddSingleton<IAuthorizationHandler, ProjectAccessHandler>();
builder.Services.AddSingleton<SampleEventLog>();
builder.Services.AddSingleton<SampleStartupState>();
builder.Services.AddSingleton<SampleMediaEventLog>();
builder.Services.AddSingleton<SampleMediaThumbnailStore>();
builder.Services.AddSingleton<SampleMediaAuthorizationLog>();
builder.Services.AddNhMedia(media =>
{
    // The library owns its PostgreSQL schema and migrations. The sample deliberately
    // adds no DAL migration and uses the temporary file-system store only for binary content.
    media.UsePostgreSqlFileStructureStorage(connectionString, options =>
    {
        options.Scheme = "samplemedia";
        options.RunMigrations = true;
    });
    media.UseFileSystemMediaStorage(mediaStoragePath, createDirectoryIfNotExists: true);
    media.AddAuthentication<ProjectMediaAuthorizationModule>();
    media.AddThumbnailService<ProjectMediaThumbnailService>();
    media.AddEventHandler<ProjectMediaEventHandler>();
});
builder.Services.AddScoped<ProjectMediaSampleService>();
builder.Services.AddScoped<AccountSampleService>();
builder.Services.AddScoped<OperationsSampleService>();
builder.Services.AddScoped<ObservabilitySampleService>();
builder.Services.AddSingleton<NewHeap.Platform.AspNet.Common.Utilities.IStartupConfiguration, SampleStartupConfiguration>();
builder.Services.AddScoped<ProjectMaintenanceJob>();
builder.Services.AddNhApiClient<SampleProjectManagementApi>(
    builder.Configuration.GetSection("ApiClients:SampleProjectManagement"));
builder.Services.AddScoped<
    ISampleProjectManagementApiService,
    SampleProjectManagementApiService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();

    if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Database:ResetOnStartup"))
    {
        await dbContext.Database.EnsureDeletedAsync();
    }

    await dbContext.Database.MigrateAsync();

    if (app.Environment.IsDevelopment())
    {
        await SampleDevelopmentIdentitySeeder.SeedAsync(scope.ServiceProvider);
    }
}

app.UseNewHeapPlatformAspNetCommon(
        app.Environment,
        app.Services,
        NewHeapPlatformAspNetCommonApplicationBuilderOptions.Builder
            .UseHsTs(!app.Environment.IsDevelopment())
            .UseHttpsRedirection(!app.Environment.IsDevelopment())
            .UseEndpoints(endpoints =>
            {
                endpoints.MapOpenApi();
                endpoints.MapScalarApiReference("/scalar", options =>
                {
                    options.WithOpenApiRoutePattern("/openapi/{documentName}.json");
                    options.Servers =
                    [
                        new ScalarServer(builder.Configuration[
                            "NewHeap:PlatformAspNetCommon:Settings:SelfBaseUrl"]!)
                    ];
                    options.Layout = ScalarLayout.Modern;
                    options.Theme = ScalarTheme.BluePlanet;
                    options.Title = "Sample Project Management API";
                    options.Authentication = new ScalarAuthenticationOptions
                    {
                        PreferredSecuritySchemes = ["bearer"]
                    };
                });
            })
            .Build())
    .UseNhAuthentication<
        NhUser,
        NhDivision,
        NhDivisionUser,
        NhDivisionRole,
        NhDivisionUserRole,
        NhDivisionRoleClaim,
        NhUserViewModel<NhDivisionViewModel>,
        NhDivisionViewModel,
        NhClaimViewModel>(authentication =>
    {
        authentication.AddUserNamePasswordEndpoint();
        authentication.AddMicrosoftOauthEndpoints();
    });

app.MapNhMediaEndpoints("project-media", options =>
    options.ConfigureAllRoutes(route => route.AllowAnonymous()));

app.MapDefaultEndpoints();
app.Run();
