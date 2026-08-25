using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AI.Generators;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiToolTests
{
    [Fact]
    public async Task Invoker_executes_after_gate_returns_authorized_context()
    {
        var budgetManager = new NhAiTestBudgetManager();
        var context = new NhAiInvocationContext(
            "actor-1",
            "test",
            new Dictionary<string, string> { ["division-id"] = Guid.Empty.ToString() });
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context),
            budgetManager);

        var result = await invoker.InvokeAsync(
            Descriptor,
            (invocationContext, _) => Task.FromResult(
                TaskResult<string>.Succeeded(invocationContext.ActorId)));

        Assert.True(result.Success);
        Assert.Equal("actor-1", result.Data);
        Assert.Single(budgetManager.Requests);
    }

    [Fact]
    public async Task Invoker_rejects_oversized_input_before_budget_or_tool_execution()
    {
        var executed = false;
        var budgetManager = new NhAiTestBudgetManager();
        var descriptor = Descriptor with { MaxInputBytes = 16 };
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(
                new NhAiInvocationContext("actor-1", "test", new Dictionary<string, string>())),
            budgetManager);

        var result = await invoker.InvokeAsync(
            descriptor,
            new ReadInput(new string('x', 128)),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Empty(budgetManager.Requests);
    }

    [Fact]
    public async Task Invoker_rejects_unserializable_input_as_a_safe_outcome()
    {
        var executed = false;
        var cyclic = new CyclicInput();
        cyclic.Self = cyclic;
        var budgetManager = new NhAiTestBudgetManager();
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(
                new NhAiInvocationContext("actor-1", "test", new Dictionary<string, string>())),
            budgetManager);

        var result = await invoker.InvokeAsync(
            Descriptor,
            cyclic,
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Empty(budgetManager.Requests);
    }

    [Fact]
    public async Task Invoker_does_not_execute_after_gate_denies_context()
    {
        var executed = false;
        var invoker = new NhAiToolInvoker(NhAiTestInvocationGate.Denied("Denied."));

        var result = await invoker.InvokeAsync(
            Descriptor,
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(result.Success);
        Assert.False(executed);
    }

    [Fact]
    public async Task Discovery_is_default_deny_and_requires_an_explicit_policy()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<GeneratedToolNhAiCatalog>());
        using var deniedProvider = services.BuildServiceProvider();
        using var deniedScope = deniedProvider.CreateScope();
        var request = new NhAiToolDiscoveryRequest(
            new NhAiInvocationContext(
                "actor-1",
                "test",
                new Dictionary<string, string>()),
            NhAiToolExposure.Local);

        var hidden = await deniedScope.ServiceProvider
            .GetRequiredService<INhAiToolDiscoveryService>()
            .DiscoverAsync(request);

        Assert.Empty(hidden);

        var allowedServices = new ServiceCollection();
        allowedServices.AddNewHeapPlatformAI(ai =>
            ai.AddGeneratedToolCatalog<GeneratedToolNhAiCatalog>());
        allowedServices.AddScoped<INhAiToolDiscoveryPolicy>(
            _ => NhAiTestDiscoveryPolicy.Allowed());
        using var allowedProvider = allowedServices.BuildServiceProvider();
        using var allowedScope = allowedProvider.CreateScope();

        var visible = await allowedScope.ServiceProvider
            .GetRequiredService<INhAiToolDiscoveryService>()
            .DiscoverAsync(request);

        Assert.Equal("generated.read", Assert.Single(visible).Id);
    }

    [Fact]
    public async Task Generator_emits_and_executes_microsoft_extensions_ai_function()
    {
        var catalog = new GeneratedToolNhAiCatalog();
        var provider = new TestServiceProvider(
            new GeneratedTool(),
            new NhAiToolInvoker(
                NhAiTestInvocationGate.Authorized(
                    new NhAiInvocationContext("actor-1", "test", new Dictionary<string, string>())),
                new NhAiTestBudgetManager()));

        var descriptor = Assert.Single(catalog.Descriptors);
        var function = Assert.Single(catalog.CreateFunctions(provider));

        Assert.Equal("generated.read", descriptor.Id);
        Assert.Equal(1, descriptor.Version);
        Assert.Equal(NhAiToolEffect.ReadOnly, descriptor.Effect);
        Assert.Equal(NhAiToolExposure.Local, descriptor.Exposure);
        Assert.Equal("generated_read_v1", function.Name);
        Assert.Equal("generated", descriptor.CatalogId);
        Assert.Equal(64, descriptor.SchemaHash.Length);
        Assert.Contains("\"value\"", descriptor.InputSchemaJson, StringComparison.Ordinal);
        Assert.Equal("generated", catalog.Manifest.CatalogId);
        Assert.Equal(descriptor.SchemaHash, Assert.Single(catalog.Manifest.Tools).SchemaHash);

        var output = await function.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = new ReadInput("value")
        });
        var json = Assert.IsType<System.Text.Json.JsonElement>(output);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("value:actor-1", json.GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("BadId", true, "NHAI002")]
    [InlineData("read", false, "NHAI004")]
    public void Generator_reports_contract_diagnostics(
        string methodId,
        bool includeDescription,
        string expectedDiagnostic)
    {
        var description = includeDescription
            ? "[System.ComponentModel.Description(\"Read generated data.\")]"
            : string.Empty;
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            [NhAiToolSet("generated")]
            public sealed class InvalidTool
            {
                [NhAiTool("{{methodId}}", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
                {{description}}
                public Task<TaskResult<string>> ReadAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var references = trustedAssemblies
            .Append(MetadataReference.CreateFromFile(typeof(NhAiToolAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TaskResult<>).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorDiagnostics",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NhAiToolGenerator());

        driver = driver.RunGenerators(compilation);

        Assert.Contains(driver.GetRunResult().Diagnostics, diagnostic => diagnostic.Id == expectedDiagnostic);
    }

    [Theory]
    [InlineData("0", "NhAiToolExposure.Local", "NHAI006")]
    [InlineData("1", "NhAiToolExposure.Mcp", "NHAI007")]
    public void Generator_rejects_invalid_versions_and_unguarded_remote_exposure(
        string version,
        string exposure,
        string expectedDiagnostic)
    {
        var source = $$"""
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            [NhAiToolSet("generated")]
            public sealed class InvalidTool
            {
                [NhAiTool("read", {{version}}, NhAiToolEffect.ReadOnly, {{exposure}})]
                [Description("Read generated data.")]
                public Task<TaskResult<string>> ReadAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var references = trustedAssemblies
            .Append(MetadataReference.CreateFromFile(typeof(NhAiToolAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TaskResult<>).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorSecurityDiagnostics",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NhAiToolGenerator());

        driver = driver.RunGenerators(compilation);

        Assert.Contains(driver.GetRunResult().Diagnostics, diagnostic => diagnostic.Id == expectedDiagnostic);
    }

    [Theory]
    [InlineData(
        "NhAiToolEffect.IdempotentMutation",
        "Idempotency = NhAiIdempotencySupport.Supported",
        "NHAI008")]
    [InlineData(
        "NhAiToolEffect.Mutation",
        "Idempotency = NhAiIdempotencySupport.Required",
        "NHAI008")]
    [InlineData(
        "NhAiToolEffect.ExternalSideEffect",
        "Approval = NhAiApprovalRequirement.Required",
        "NHAI008")]
    [InlineData(
        "NhAiToolEffect.ReadOnly",
        "TimeoutSeconds = 0",
        "NHAI009")]
    [InlineData(
        "NhAiToolEffect.Destructive",
        "Approval = NhAiApprovalRequirement.Required, VerifierId = \"BadVerifier\"",
        "NHAI002")]
    public void Generator_rejects_unsafe_effects_bounds_and_verifier_ids(
        string effect,
        string namedArguments,
        string expectedDiagnostic)
    {
        var source = $$"""
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            [NhAiToolSet("generated")]
            public sealed class InvalidTool
            {
                [NhAiTool("mutate", 1, {{effect}}, NhAiToolExposure.Local, {{namedArguments}})]
                [Description("Mutate generated data.")]
                public Task<TaskResult<string>> MutateAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var references = trustedAssemblies
            .Append(MetadataReference.CreateFromFile(typeof(NhAiToolAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TaskResult<>).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorGuardDiagnostics",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NhAiToolGenerator());

        driver = driver.RunGenerators(compilation);

        Assert.Contains(
            driver.GetRunResult().Diagnostics,
            diagnostic => diagnostic.Id == expectedDiagnostic);
    }

    private static readonly NhAiToolDescriptor Descriptor = new(
        "generated.read",
        1,
        "Read generated data.",
        typeof(ReadInput),
        typeof(string),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local,
        false,
        []);

    private sealed class TestServiceProvider(params object[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return services.SingleOrDefault(service => serviceType.IsInstanceOfType(service));
        }
    }
}

public sealed record ReadInput(string Value);

public sealed class CyclicInput
{
    public CyclicInput? Self { get; set; }
}

[NhAiToolSet("generated")]
public sealed class GeneratedTool
{
    [NhAiTool("read", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Read generated data.")]
    public Task<TaskResult<string>> ReadAsync(
        ReadInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(TaskResult<string>.Succeeded(input.Value + ":" + context.ActorId));
    }
}
