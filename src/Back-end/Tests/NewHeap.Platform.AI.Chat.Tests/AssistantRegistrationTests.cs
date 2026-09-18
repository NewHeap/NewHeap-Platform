using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantRegistrationTests
{
    [Fact]
    public async Task Registering_the_assistant_twice_is_idempotent()
    {
        await using var app = await StartAsync(builder =>
        {
            AddAssistant(builder.Services);
            AddAssistant(builder.Services);
        });

        var services = app.Services;
        Assert.Single(services.GetServices<IHostedService>().OfType<NhAssistantStartupValidator>());
        Assert.Single(services.GetServices<NhAssistantRegistrationState>());
        using var scope = services.CreateScope();
        var gate = Assert.IsType<NhAssistantInvocationGate>(scope.ServiceProvider.GetRequiredService<INhAiToolInvocationGate>());
        Assert.IsType<NhAssistantBudgetManager>(scope.ServiceProvider.GetRequiredService<INhAiBudgetManager>());
        Assert.IsType<NhAssistantIdempotencyManager>(scope.ServiceProvider.GetRequiredService<INhAiIdempotencyManager>());
        Assert.IsType<NhAssistantApprovalEvidenceProvider>(scope.ServiceProvider.GetRequiredService<INhAiApprovalEvidenceProvider>());
        Assert.Single(scope.ServiceProvider.GetServices<INhAiAuditSink>().OfType<NhAssistantAuditRelay>());
        Assert.IsType<NhAiTestBudgetManager>(
            scope.ServiceProvider.GetRequiredKeyedService<INhAiBudgetManager>(NhAssistantFallbacks.ServiceKey));
        Assert.Single(services.GetRequiredService<NhAssistantAgentRegistry>().Agents);
        Assert.NotNull(gate);
    }

    [Fact]
    public async Task Startup_fails_when_the_agent_profile_is_missing()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
            AddAssistant(builder.Services, assistant => assistant.AddAgent(
                AssistantTestData.Agent("other-agent") with { ProfileName = "missing-profile" }))));

        Assert.Contains("missing-profile", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_when_the_declared_chat_profile_is_missing()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
            AddAssistant(builder.Services, assistant => assistant.UseChatProfile("unknown-chat"))));

        Assert.Contains("unknown-chat", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_without_the_aspnet_ai_integration()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            builder => AddAssistant(builder.Services),
            addAspNet: false));

        Assert.Contains("AddNewHeapPlatformAIAspNet", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_when_a_manager_is_replaced_after_the_assistant()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
        {
            AddAssistant(builder.Services);
            builder.Services.AddNewHeapPlatformAI(ai => ai.UseBudgetManager<NhAiTestBudgetManager>());
        }));

        Assert.Contains("after the application's own AI registrations", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_when_the_access_policy_is_unknown()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
            AddAssistant(builder.Services, assistant => assistant.UseAccessPolicy("app.unknown"))));

        Assert.Contains("app.unknown", exception.Message);
    }

    [Fact]
    public async Task Options_are_bound_from_the_assistant_configuration_section()
    {
        await using var app = await StartAsync(
            builder => AddAssistant(builder.Services),
            configuration: new Dictionary<string, string?>
            {
                ["NewHeap:AI:Assistant:Enabled"] = "true",
                ["NewHeap:AI:Assistant:AccessPolicy"] = "app.custom.access"
            });

        var options = app.Services.GetRequiredService<IOptions<NhAssistantOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal("app.custom.access", options.AccessPolicy);
        Assert.Equal(
            "app.custom.access",
            NhAssistantEndpointOptions.ResolveAccessPolicy(
                app.Services.GetRequiredService<NhAssistantRegistrationState>(),
                options));
    }

    [Fact]
    public void The_business_audit_event_carries_identifiers_codes_and_timestamps_only()
    {
        var properties = typeof(NhAssistantAuditEvent)
            .GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();

        Assert.All(properties, property => Assert.True(
            property.PropertyType == typeof(Guid)
                || property.PropertyType == typeof(Guid?)
                || property.PropertyType == typeof(int)
                || property.PropertyType == typeof(int?)
                || property.PropertyType == typeof(DateTimeOffset)
                || property.PropertyType == typeof(DateTimeOffset?)
                || property.PropertyType == typeof(NhAssistantAuditEventKind)
                || property.Name is "ActorId" or "AgentId" or "ToolId" or "ResultCode" or "ObjectId"
                    or "ApplicationContextVersion" or "ApplicationContextHash" or "InstructionsVersion"
                    or "InstructionsHash" or "PreferencesHash" or "EqualityContract",
            $"{property.Name} is not an identifier, code or timestamp."));
    }

    private static void AddAssistant(
        IServiceCollection services,
        Action<NhAssistantBuilder>? configure = null)
    {
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_registration", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" });
            configure?.Invoke(assistant);
        });
    }

    private static async Task<WebApplication> StartAsync(
        Action<WebApplicationBuilder> configure,
        bool addAspNet = true,
        IDictionary<string, string?>? configuration = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration ?? new Dictionary<string, string?>());
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NhAssistantOptions.DefaultAccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("app.custom.access", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(NhAssistantOptions.DefaultAdminPolicy, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton<TestProjectToolRecorder>();
        builder.Services.AddKeyedSingleton<IChatClient>("project-chat-model", new NhAiScriptedChatClient());
        builder.Services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local"));
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });
        if (addAspNet)
        {
            builder.Services.AddNewHeapPlatformAIAspNet(_ => { });
        }
        configure(builder);
        var app = builder.Build();
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        return app;
    }
}
