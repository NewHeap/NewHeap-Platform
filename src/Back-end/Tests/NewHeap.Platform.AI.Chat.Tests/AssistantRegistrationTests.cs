using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task Startup_fails_when_turns_use_the_application_budget_but_the_application_registered_none()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            builder => AddAssistant(builder.Services, assistant => assistant.UseApplicationBudgetInTurns()),
            applicationBudget: false));

        Assert.Contains("UseApplicationBudgetInTurns needs a budget manager of the application", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_when_the_access_policy_is_unknown()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
            AddAssistant(builder.Services, assistant => assistant.UseAccessPolicy("app.unknown"))));

        Assert.Contains("app.unknown", exception.Message);
    }

    [Fact]
    public async Task A_named_subject_type_starts_and_the_parameterless_registration_keeps_its_configuration()
    {
        await using var app = await StartAsync(builder =>
        {
            builder.Services.AddSingleton(new SearchableProjects());
            AddAssistant(builder.Services, assistant => assistant
                .AddSubjectType<SearchableProjectResolver>(type => type
                    .Named(new() { ["en"] = "Project", ["nl"] = "Opdracht" }, fallbackLanguage: "en")
                    .OpenAt("/projects/{id}"))
                // Registering the same resolver again, as another composition may, is harmless.
                .AddSubjectType<SearchableProjectResolver>()
                .AddSubjectType<SearchableProjectResolver>(type => type
                    .Named(new() { ["en"] = "Project", ["nl"] = "Opdracht" }, fallbackLanguage: "en")
                    .OpenAt("/projects/{id}")));
        });

        var state = app.Services.GetRequiredService<NhAssistantRegistrationState>();
        // Next to the platform's own whole application, one configuration of the application's type.
        var options = Assert.Single(state.SubjectTypes.Values, options => options.ResolverType == typeof(SearchableProjectResolver));
        Assert.Equal("/projects/{id}", options.LinkTemplate);
    }

    [Fact]
    public void A_resolver_cannot_be_configured_twice_differently()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AddAssistant(new ServiceCollection(), assistant => assistant
                .AddSubjectType<SearchableProjectResolver>(type => type.OpenAt("/projects/{id}"))
                .AddSubjectType<SearchableProjectResolver>(type => type.OpenAt("/other/{id}"))));

        Assert.Contains(nameof(SearchableProjectResolver), exception.Message);
    }

    [Fact]
    public async Task Startup_fails_for_a_subject_type_in_the_platforms_reserved_prefix()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
            AddAssistant(builder.Services, assistant => assistant.AddSubjectType<ReservedSubjectResolver>())));

        Assert.Contains("nh-system", exception.Message);
        Assert.Contains("reserved", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_for_two_resolvers_of_one_subject_type()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
        {
            builder.Services.AddSingleton(new SearchableProjects());
            builder.Services.AddSingleton(new TestSubjects());
            AddAssistant(builder.Services, assistant => assistant
                .AddSubjectType<SearchableProjectResolver>()
                .AddSubjectType<TestSubjectResolver>());
        }));

        Assert.Contains("'project'", exception.Message);
        Assert.Contains(nameof(TestSubjectResolver), exception.Message);
    }

    [Fact]
    public async Task Startup_fails_for_a_subject_type_configuration_without_its_resolver()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(builder =>
        {
            AddAssistant(builder.Services, assistant => assistant
                .AddSubjectType<TicketResolver>(type => type.Named(new() { ["en"] = "Ticket" }, fallbackLanguage: "en")));
            // The application removed its resolver afterwards, so the names describe nothing; the
            // platform's own resolver of the whole application stays.
            builder.Services.Remove(builder.Services.Single(descriptor =>
                descriptor.ServiceType == typeof(INhAiSubjectAccessResolver) && descriptor.ImplementationType == typeof(TicketResolver)));
        }));

        Assert.Contains(nameof(TicketResolver), exception.Message);
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
                || property.PropertyType == typeof(long?)
                || property.PropertyType == typeof(bool?)
                || property.PropertyType == typeof(DateTimeOffset)
                || property.PropertyType == typeof(DateTimeOffset?)
                || property.PropertyType == typeof(NhAssistantAuditEventKind)
                || property.PropertyType == typeof(NhAiGrantSource)
                || property.Name is "ActorId" or "AgentId" or "ToolId" or "ResultCode" or "ObjectId"
                    or "ApplicationContextVersion" or "ApplicationContextHash" or "InstructionsVersion"
                    or "InstructionsHash" or "PreferencesHash" or "TriggerId" or "SubjectType" or "SubjectId"
                    or "InitiatorActorId" or "ApprovalPolicyId" or "AskingAgentId" or "SkillId" or "ApplicationMapHash" or "RetrievedContextHash" or "McpConnectionOwner"
                    // A file is named by the application's id, its normalized media type (a code), the
                    // SHA-256 of what the model or reader read and the reader's id; never by its content.
                    or "FileId" or "FileMediaType" or "FileHash" or "FileReaderId"
                    // Decided in the review of files in model turns (D35): the file's name is the one
                    // free text the event carries, so an audit reader recognises a file without asking
                    // the application. It is bounded, normalized and stripped of control and format
                    // characters (see the test below); no other free text is allowed.
                    or "FileName"
                    or "EqualityContract",
            $"{property.Name} is not an identifier, code or timestamp."));
    }

    [Theory]
    [InlineData("Launch plan.pdf", "Launch plan.pdf")]
    [InlineData("  Launch\tplan \r\n v2.pdf  ", "Launch plan v2.pdf")]
    [InlineData("Plan\u0000\u0007.pdf", "Plan.pdf")]
    [InlineData("Invoice\u202Efdp.exe", "Invoicefdp.exe")]
    [InlineData("Cafe\u0301.pdf", "Caf\u00E9.pdf")]
    [InlineData("\u200B\u2028", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_audit_names_a_file_with_its_normalized_name(string? name, string? expected)
    {
        Assert.Equal(expected, Runtime.NhAssistantTurnFiles.AuditNameOf(name));
    }

    [Fact]
    public void The_audit_bounds_a_file_name_without_splitting_a_character()
    {
        var name = new string('a', NhAssistantAuditEvent.MaxFileNameLength - 1) + "\U0001F4C4" + "b";

        var audited = Runtime.NhAssistantTurnFiles.AuditNameOf(name)!;

        Assert.Equal(new string('a', NhAssistantAuditEvent.MaxFileNameLength - 1), audited);
        Assert.Equal(
            NhAssistantAuditEvent.MaxFileNameLength,
            Runtime.NhAssistantTurnFiles.AuditNameOf(new string('x', 1_000))!.Length);
    }

    private sealed class ReservedSubjectResolver : INhAiSubjectAccessResolver
    {
        public string SubjectType => "nh-system";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(null);
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(NhAiSubjectAccess.None);
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
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
        IDictionary<string, string?>? configuration = null,
        bool applicationBudget = true)
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
            if (applicationBudget)
            {
                ai.UseBudgetManager<NhAiTestBudgetManager>();
            }
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
