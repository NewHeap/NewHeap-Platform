using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiFoundationTests
{
    [Fact]
    public void Registration_is_idempotent_for_an_identical_named_profile()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(AddAssistantProfile);
        services.AddNewHeapPlatformAI(AddAssistantProfile);

        using var provider = services.BuildServiceProvider();
        var profile = Assert.Single(
            provider.GetRequiredService<INhAiModelProfileRegistry>().Profiles);

        Assert.Equal("assistant", profile.Name);
        Assert.Equal("assistant-model", profile.KeyedClientKey);
    }

    [Fact]
    public void Registration_rejects_a_conflicting_named_profile()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(AddAssistantProfile);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(
                "assistant",
                profile => profile.UseKeyedClient("different-model"))));

        Assert.Contains("different contract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_selects_a_policy_compatible_fallback_profile()
    {
        var services = new ServiceCollection();
        var primaryClient = new NhAiDeterministicChatClient("primary");
        var fallbackClient = new NhAiDeterministicChatClient("fallback");
        services.AddKeyedSingleton<IChatClient>("primary-model", primaryClient);
        services.AddKeyedSingleton<IChatClient>("fallback-model", fallbackClient);
        services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("primary", profile => profile
                .UseKeyedClient("primary-model")
                .WithFallbackProfiles("fallback"));
            ai.AddChatProfile("fallback", profile => profile
                .UseKeyedClient("fallback-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local"));
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });

        using var provider = services.BuildServiceProvider();
        await StartAiValidationAsync(provider);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiModelProfileResolver>()
            .ResolveChatAsync(new NhAiModelResolutionRequest(
                "primary",
                NhAiModelCapability.FunctionCalling,
                NhAiDataClassification.Internal,
                "project-assistance",
                "local"));

        Assert.True(result.Success);
        Assert.Equal("fallback", result.Data.Profile.Name);
        Assert.Same(fallbackClient, result.Data.Client);
        Assert.Equal(
            ["profile:primary:capability-mismatch", "profile:fallback:selected"],
            result.Data.DecisionTrace);
    }

    [Fact]
    public async Task Startup_validation_rejects_an_unregistered_keyed_client()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(AddAssistantProfile);
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAiValidationAsync(provider));

        Assert.Contains("unregistered keyed IChatClient", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_validation_rejects_a_missing_required_capability()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>(
            "assistant-model",
            new NhAiDeterministicChatClient("response"));
        services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile(
                "assistant",
                profile => profile.UseKeyedClient("assistant-model"));
            ai.RequireProfile("assistant", NhAiModelCapability.Vision);
        });
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAiValidationAsync(provider));

        Assert.Contains("does not declare all required capabilities", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_validation_rejects_a_required_idempotency_manager_gap()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<ProtectedMutationCatalog>()
                .UseBudgetManager<NhAiTestBudgetManager>());
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAiValidationAsync(provider));

        Assert.Contains("requires a configured idempotency manager", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_validation_rejects_an_unregistered_verifier()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(ai => ai
            .AddGeneratedToolCatalog<ProtectedMutationCatalog>()
            .UseBudgetManager<NhAiTestBudgetManager>()
            .UseIdempotencyManager<StartupIdempotencyManager>());
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAiValidationAsync(provider));

        Assert.Contains("references unregistered verifier", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_validation_accepts_complete_mutation_guards()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(ai => ai
            .AddGeneratedToolCatalog<ProtectedMutationCatalog>()
            .UseBudgetManager<NhAiTestBudgetManager>()
            .UseIdempotencyManager<StartupIdempotencyManager>()
            .AddVerifier<StartupVerifier>());
        using var provider = services.BuildServiceProvider();

        await StartAiValidationAsync(provider);
    }

    [Fact]
    public async Task Context_contributors_run_in_order_and_build_bounded_scope()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI();
        services.AddSingleton<INhAiInvocationContextContributor>(
            new OrderedContributor(20, "second", addExecutionScope: true));
        services.AddSingleton<INhAiInvocationContextContributor>(
            new OrderedContributor(10, "first", addExecutionScope: false));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var context = await scope.ServiceProvider
            .GetRequiredService<INhAiInvocationContextFactory>()
            .CreateAsync(new NhAiInvocationContextSeed(
                "actor-1",
                "project-assistance",
                "owner-1"));

        Assert.True(context.TryGetScopeValue("sequence", out var sequence));
        Assert.Equal("second", sequence);
        Assert.Equal("owner-1", context.AccountableOwnerId);
        Assert.Equal("division", Assert.Single(context.ExecutionScopes).Type);
        Assert.Contains("projects-read", context.CapabilityGrants);
    }

    [Fact]
    public void Task_result_mapping_uses_explicit_safe_classification_without_message_parsing()
    {
        const string protectedMessage = "customer-content-must-not-become-a-code";
        var result = TaskResult<string>.Failed(protectedMessage);
        var mapper = new NhAiTaskResultMapper();

        var safeOutcome = mapper.Map(
            result,
            new NhAiOutcomeClassification(
                NhAiOutcomeKind.Conflict,
                "project-conflict"));

        Assert.Equal(NhAiOutcomeKind.Conflict, safeOutcome.Kind);
        Assert.Equal("project-conflict", safeOutcome.Code);
        Assert.Equal(1, safeOutcome.ErrorCount);
        Assert.DoesNotContain(
            protectedMessage,
            JsonSerializer.Serialize(safeOutcome),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_audit_record_does_not_capture_result_content()
    {
        const string protectedResult = "raw-model-or-tool-content";
        var sink = new RecordingAuditSink();
        var context = new NhAiInvocationContext(
            "actor-1",
            "project-assistance",
            new Dictionary<string, string>());
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context),
            [sink],
            new NhAiTestBudgetManager());

        var result = await invoker.InvokeAsync(
            Descriptor,
            (_, _) => Task.FromResult(TaskResult<string>.Succeeded(protectedResult)));

        Assert.True(result.Success);
        Assert.Equal(NhAiOutcomeKind.Succeeded, Assert.IsType<NhAiAuditRecord>(sink.Record).Outcome);
        Assert.DoesNotContain(
            protectedResult,
            JsonSerializer.Serialize(sink.Record),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deterministic_clients_record_requests_without_external_providers()
    {
        var chatClient = new NhAiDeterministicChatClient("deterministic-response");
        var embeddingGenerator = new NhAiDeterministicEmbeddingGenerator();

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "deterministic-request")]);
        var embeddings = await embeddingGenerator.GenerateAsync(["one", "two"]);

        Assert.NotNull(response);
        Assert.Single(chatClient.Requests);
        Assert.Equal(2, embeddings.Count);
        Assert.Equal(["one", "two"], embeddingGenerator.Inputs);
    }

    private static readonly NhAiToolDescriptor Descriptor = new(
        "projects.search",
        1,
        "Search authorized projects.",
        typeof(string),
        typeof(string),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local,
        true,
        ["project-read"]);

    private static void AddAssistantProfile(NhAiBuilder ai)
    {
        ai.AddChatProfile("assistant", profile => profile
            .UseKeyedClient("assistant-model")
            .RequireCapabilities(NhAiModelCapability.FunctionCalling)
            .WithBudget(4_096, 1_024, 8));
    }

    private static async Task StartAiValidationAsync(IServiceProvider provider)
    {
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private sealed class OrderedContributor(
        int order,
        string value,
        bool addExecutionScope) : INhAiInvocationContextContributor
    {
        public int Order => order;

        public ValueTask ContributeAsync(
            NhAiInvocationContextBuilder builder,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.SetScopeValue("sequence", value);
            if (addExecutionScope)
            {
                builder
                    .AddExecutionScope("division", "division-1")
                    .GrantCapability("projects-read");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuditSink : INhAiAuditSink
    {
        public NhAiAuditRecord? Record { get; private set; }

        public ValueTask WriteAsync(
            NhAiAuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record = record;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ProtectedMutationCatalog : INhAiToolCatalog
    {
        public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

        public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } =
        [
            new NhAiToolDescriptor(
                "projects.change-status",
                1,
                "Change project status.",
                typeof(string),
                typeof(string),
                NhAiToolEffect.IdempotentMutation,
                NhAiToolExposure.Local,
                true,
                ["project-manage"])
            {
                Idempotency = NhAiIdempotencySupport.Required,
                VerifierId = "project-status"
            }
        ];

        public NhAiToolCatalogManifest Manifest { get; } = new(
            "test-catalog",
            1,
            "test-hash",
            []);

        public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
        {
            return [];
        }
    }

    public sealed class StartupIdempotencyManager : INhAiIdempotencyManager
    {
        public ValueTask<NhAiIdempotencyLease> AcquireAsync(
            NhAiIdempotencyRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiIdempotencyLease(
                NhAiIdempotencyDecisionKind.Acquired,
                "acquired"));
        }

        public ValueTask CompleteAsync(
            NhAiIdempotencyLease lease,
            NhAiOutcomeKind outcome,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    public sealed class StartupVerifier : INhAiToolVerifier
    {
        public string Id => "project-status";

        public ValueTask<NhAiVerificationResult> VerifyAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            object? executionResult,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiVerificationResult(true, "verified"));
        }
    }
}
