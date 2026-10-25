using Hangfire;
using Hangfire.PostgreSql;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DotNetCore.CAP;
using NewHeap.Platform.AspNet;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.OpenApiSchemaTransformers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Mcp;
using ModelContextProtocol.Server;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Events.Cap;
using NewHeap.Media;
using Scalar.AspNetCore;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Api.Jobs;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Core;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Utilities;
using SampleProjectManagement.DAL;
using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Localization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.UseNewHeapAspnetCommonConfiguration(args);
builder.AddNewHeapPlatformCachingDefault(options =>
{
    options.DefaultEntryOptions.Duration = TimeSpan.FromMinutes(2);
    options.DefaultEntryOptions.JitterMaxDuration = TimeSpan.FromSeconds(10);
});
builder.Services.AddOpenApi("v1", options =>
{
    options.AddSchemaTransformer<OneOfSchemaTransformer>();
    options.AddOperationTransformer<SampleProjectManagement.Api.Models.ProjectCollectionOpenApiTransformer>();
});
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[]
    {
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.GetCultureInfo("nl-NL")
    };
    options.DefaultRequestCulture = new RequestCulture(supportedCultures[0]);
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

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
    // SPM-097: controllers share the one endpoint-routing table with every other endpoint, so their
    // authorization, CORS and other endpoint metadata apply as for minimal APIs.
    .UseEndpointRoutingOnly()
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

        // The assistant endpoints and hub use this policy as their access policy. Minimal APIs
        // do not inherit the bearer scheme of NhBaseController, and WithIdentity makes the
        // Identity cookie the default scheme, so the policy names the bearer scheme itself.
        options.AddPolicy(
            "app.active-division.project.view",
            policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireActiveDivisionAccess(
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

        options.AddPolicy(
            SampleAuthorizationPolicies.BackgroundOperationAdministration,
            policy => policy.RequireClaim(
                NhPlatformClaimTypes.Permission,
                SampleAuthorizationPolicies.BackgroundOperationAdministration));

        options.AddPolicy(
            SampleAuthorizationPolicies.TwoFactorAdministration,
            policy => policy.RequireClaim(
                NhPlatformClaimTypes.Permission,
                SampleAuthorizationPolicies.TwoFactorAdministration));
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
        // Authenticator, recovery codes, e-mail codes, passkeys, remembered devices and the
        // security-officer requirement; see SampleTwoFactorComposition.
        options.AddTwoFactor(twoFactor => twoFactor.ConfigureSampleTwoFactor(builder.Configuration));
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
                .AddSubscriber<ProjectTaskEventConsumer, ProjectTaskUpdatedEvent>()
                .AddSubscriber<ProjectStatusEventConsumer, ProjectStatusChangedEvent>()
                .AddCustomTopicSubscriber<PriorityProjectEventConsumer>();
        });
    })
    .WithIdentityEntityFramework(options =>
    {
        switch (databaseProvider)
        {
            case DatabaseProvider.SqlServer:
                options.UseNewHeapSqlServer(connectionString);
                break;
            case DatabaseProvider.PostgreSql:
                options.UseNewHeapPostgreSql(connectionString, postgres =>
                {
                    var migrationsAssembly = builder.Configuration["Database:PostgreSqlMigrationsAssembly"];
                    if (!string.IsNullOrWhiteSpace(migrationsAssembly))
                    {
                        postgres.MigrationsAssembly(migrationsAssembly);
                    }
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(databaseProvider));
        }
    })
    .WithIdentity(_ => { })
    .WithDbLogService(options =>
    {
        builder.Configuration
            .GetSection($"{NewHeapAspNetCommonOptions.DefaultSettingsPrefix}:DbLogServiceSettings")
            .Bind(options);
    })
    .WithHangfire(options =>
    {
        switch (databaseProvider)
        {
            case DatabaseProvider.SqlServer:
                options.UseSqlServerStorage(connectionString);
                break;
            case DatabaseProvider.PostgreSql:
                options.UsePostgreSqlStorage(storage => storage.UseNpgsqlConnection(connectionString));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(databaseProvider));
        }
    })
    .WithBackgroundOperations(operations =>
    {
        operations.Options.OperationUrlPrefix = "/background-operations";
        operations.Options.ProgressFlushInterval = TimeSpan.FromMilliseconds(250);
        operations
            .WithGlobalConcurrency(8)
            .WithDefaultQueueConcurrency(6)
            // Notifies only outcomes and requests for attention (NewHeap default) and
            // threads repeated portfolio work per division.
            .UseNotificationPolicy<ProjectOperationNotificationPolicy>()
            // Lets administrators list, inspect, cancel and retry every user's
            // operations in the active division under /background-operations/administration.
            .UseAdministrationPolicy(SampleAuthorizationPolicies.BackgroundOperationAdministration);
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
        // Enrollment reminders and session revocation for the two-factor policy.
        operations.AddTwoFactorOperations<NhUser>();
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
        // Headless agent runs and the execution of approved agent proposals; see
        // SampleIntakeReviewerComposition. SPM-326: they work on a queue of their own that keeps
        // two of the eight slots for itself and runs at most four operations at the same time.
        operations.AddSampleAgentRuns();
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
builder.Services.AddSampleAiBridge();
// SPM-290: the project planner reads through the gateway in headless runs with the project owner's
// rights; the built-in issuer creates the owner's short-lived token with the NewHeap authentication service.
builder.Services.AddNewHeapPlatformAIDelegatedReads();
builder.Services.AddSampleAssistant(builder.Environment, builder.Configuration);
builder.Services.AddSampleIntakeReviewer();
// SPM-314: the order desk executes the intake reviewer's approved equipment orders and reports how they went.
builder.Services.AddSampleEquipmentOrders();
builder.Services.AddSampleStatusAdvisor();
// SPM-294: the closer answers to the technical account in configuration, when there is one.
builder.Services.AddSampleProjectCloser(builder.Configuration[SampleProjectCloserComposition.ServiceAccountKey]);
builder.Services.AddSampleProjectPlanner();
builder.Services.AddSampleProjectPlannerVariant();
// SPM-320: skills defined as data may take their instructions from the sample's own text assets.
builder.Services.AddSampleInstructionAssets();
builder.Services.AddSampleProjectStarterBlocks();
// SPM-318 and SPM-319: hand-offs and shadow variants administrators define as data, within ceilings in code.
builder.Services.AddSampleAgentLinksAsData();
// SPM-305: assistant turns read files of the media library: chat uploads and project documents.
builder.Services.AddSampleAssistantFiles();
// SPM-306: the document reader reads a project's documents when one is added; it opts in to files.
builder.Services.AddSampleProjectDocumentReader();
if (builder.Environment.IsDevelopment())
{
    // After the assistant's own start-up work: its schema exists when the project starter is created.
    builder.Services.AddHostedService<SampleRuntimeAgentSeeder>();
}
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithNewHeapPlatformAITools();
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
    // SPM-306: a document added to a project's documents folder starts the document reader.
    media.AddEventHandler<ProjectDocumentEventHandler>();
});
builder.Services.AddScoped<ProjectMediaSampleService>();
builder.Services.AddScoped<AccountSampleService>();
builder.Services.AddScoped<OperationsSampleService>();
builder.Services.AddScoped<SampleAgentMaintenanceService>();
// SPM-326: the intake review a project's creation started, for the maintenance page.
builder.Services.AddScoped<SampleIntakeReviewLookup>();
builder.Services.AddScoped<SampleAgentConfigurationService>();
builder.Services.AddScoped<SampleInstructionAssetService>();
// SPM-329 and SPM-330: agents read as in effect, changed from code and a shadow variant promoted.
builder.Services.AddScoped<SampleAgentDefinitionService>();
// SPM-331: the operations code connects MCP servers, finds who uses a text and compares packages through services.
builder.Services.AddScoped<SampleAssistantOperationsService>();
// SPM-268: approvers answer the intake reviewer's proposals with a messaging reply, which decides
// as that person through the platform's proposal decision service.
builder.Services.AddScoped<SampleApprovalReplyChannel>();
builder.Services.AddScoped<ObservabilitySampleService>();
builder.Services.AddSingleton<NewHeap.Platform.AspNet.Common.Utilities.IStartupConfiguration, SampleStartupConfiguration>();
builder.Services.AddScoped<ProjectMaintenanceJob>();
builder.Services.AddNhApiClient<SampleProjectManagementApi>(
    builder.Configuration.GetSection("ApiClients:SampleProjectManagement"));
builder.Services.AddScoped<
    ISampleProjectManagementApiService,
    SampleProjectManagementApiService>();

var app = builder.Build();

app.UseRequestLocalization();

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
                endpoints.MapMcp("/mcp").RequireAuthorization();
                endpoints.MapSampleAssistant();
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
        authentication.AddTwoFactorEndpoints();
        authentication.AddPasskeyEndpoints();
    });

app.MapNhMediaEndpoints("project-media", options =>
    options.ConfigureAllRoutes(route => route.AllowAnonymous()));

app.MapDefaultEndpoints();
app.Run();
