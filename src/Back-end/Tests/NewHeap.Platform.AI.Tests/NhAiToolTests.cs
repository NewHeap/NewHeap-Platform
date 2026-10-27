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
        Assert.Equal("generated.read", function.Name);
        Assert.Equal("generated.read", descriptor.ExportName);
        Assert.Equal("generated", descriptor.CatalogId);
        Assert.Equal(64, descriptor.SchemaHash.Length);
        Assert.Contains("\"value\"", descriptor.InputSchemaJson, StringComparison.Ordinal);
        Assert.Equal("generated", catalog.Manifest.CatalogId);
        Assert.Equal(descriptor.SchemaHash, Assert.Single(catalog.Manifest.Tools).SchemaHash);
        Assert.Equal("generated.read", Assert.Single(catalog.Manifest.Tools).ExportName);

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
    [InlineData(
        "NhAiToolEffect.Destructive",
        "Approval = NhAiApprovalRequirement.ConsumerAuthoritative, Idempotency = NhAiIdempotencySupport.Required, VerifierId = \"verifier\"",
        "NHAI008")]
    [InlineData(
        "NhAiToolEffect.IdempotentMutation",
        "Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative, ExportSchema = NhAiToolExportSchema.Flat",
        "NHAI012")]
    [InlineData(
        "NhAiToolEffect.Mutation",
        "Approval = NhAiApprovalRequirement.Issuer, Idempotency = NhAiIdempotencySupport.Required",
        "NHAI008")]
    [InlineData(
        "NhAiToolEffect.Destructive",
        "Approval = NhAiApprovalRequirement.Issuer, VerifierId = \"verifier\"",
        "NHAI008")]
    [InlineData(
        "NhAiToolEffect.ReadOnly",
        "Approval = NhAiApprovalRequirement.Issuer",
        "NHAI008")]
    [InlineData(
        "NhAiToolEffect.Mutation",
        "Approval = NhAiApprovalRequirement.ConsumerAuthoritative, Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative, IdempotentHint = NhAiToolHint.True",
        "NHAI013")]
    [InlineData(
        "NhAiToolEffect.Mutation",
        "Approval = NhAiApprovalRequirement.Required, Idempotency = NhAiIdempotencySupport.Required, ReadOnlyHint = NhAiToolHint.True",
        "NHAI013")]
    [InlineData(
        "NhAiToolEffect.ExternalSideEffect",
        "Approval = NhAiApprovalRequirement.Required, Idempotency = NhAiIdempotencySupport.Required, OpenWorldHint = NhAiToolHint.False",
        "NHAI013")]
    [InlineData(
        "NhAiToolEffect.Destructive",
        "Approval = NhAiApprovalRequirement.Required, Idempotency = NhAiIdempotencySupport.Required, VerifierId = \"verifier\", DestructiveHint = NhAiToolHint.False",
        "NHAI013")]
    [InlineData(
        "NhAiToolEffect.ReadOnly",
        "AllowAgentGrant = true",
        "NHAI014")]
    [InlineData(
        "NhAiToolEffect.ReadOnly",
        "ApprovalPolicy = \"projects-approve\"",
        "NHAI014")]
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

    [Fact]
    public void Generator_accepts_consumer_authoritative_safeguards_with_a_flat_export_schema()
    {
        const string source = """
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            public sealed record ReceiptInput(Guid OrderId, string ApprovalGrant, string IdempotencyKey);

            public sealed record Receipt(string Execution, string Code);

            [NhAiToolSet("orders")]
            public sealed class ConsumerAuthoritativeTool
            {
                [NhAiTool(
                    "apply-status-receipt",
                    1,
                    NhAiToolEffect.Mutation,
                    NhAiToolExposure.Local,
                    Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
                    Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
                    ExportSchema = NhAiToolExportSchema.Flat)]
                [Description("Apply an approved status change and return the domain receipt.")]
                public Task<TaskResult<Receipt>> ApplyAsync(
                    ReceiptInput input,
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
            "GeneratorConsumerAuthoritative",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NhAiToolGenerator());

        driver = driver.RunGenerators(compilation);

        var runResult = driver.GetRunResult();
        Assert.DoesNotContain(
            runResult.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(runResult.GeneratedTrees).ToString();
        Assert.Contains(
            "Approval = (global::NewHeap.Platform.AI.NhAiApprovalRequirement)3",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "Idempotency = (global::NewHeap.Platform.AI.NhAiIdempotencySupport)3",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExportSchema = (global::NewHeap.Platform.AI.NhAiToolExportSchema)1",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "SerializerOptions = global::NewHeap.Platform.AI.NhAiToolJsonSerializerOptions.FlatExport",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_emits_agent_grant_and_approval_policy_into_the_contract()
    {
        var plain = GenerateAgentTool(string.Empty);
        var granted = GenerateAgentTool(", AllowAgentGrant = true, ApprovalPolicy = \"projects.approve\"");

        Assert.DoesNotContain("AllowAgentGrant", plain, StringComparison.Ordinal);
        Assert.Contains("AllowAgentGrant = true,", granted, StringComparison.Ordinal);
        Assert.Contains("ApprovalPolicy = \"projects.approve\",", granted, StringComparison.Ordinal);
        Assert.NotEqual(ReadContractHash(plain), ReadContractHash(granted));
    }

    [Fact]
    public void Generator_reads_positional_inherited_and_overridden_authorization_policies()
    {
        const string source = """
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Authorization;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            [Authorize(Policy = "vault.base")]
            public abstract class VaultToolsBase
            {
                [Authorize("vault.override")]
                public virtual Task<TaskResult<string>> OverriddenAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            public abstract class VaultToolsMiddle : VaultToolsBase
            {
            }

            [NhAiToolSet("vault")]
            public sealed class VaultTools : VaultToolsMiddle
            {
                [NhAiTool("positional", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp)]
                [Authorize("vault.admin")]
                [Description("Read with a positional policy.")]
                public Task<TaskResult<string>> PositionalAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();

                [NhAiTool("named-wins", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
                [Authorize("vault.replaced", Policy = "vault.named")]
                [Description("Read with a named policy that replaces the positional one.")]
                public Task<TaskResult<string>> NamedWinsAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();

                [NhAiTool("inherited", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Agent)]
                [Description("Read with only the inherited policy.")]
                public Task<TaskResult<string>> InheritedAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();

                [NhAiTool("overridden", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
                [Description("Read through an override of an authorized method.")]
                public override Task<TaskResult<string>> OverriddenAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;

        var generated = GenerateAuthorizationTools(source);

        Assert.Equal(["vault.admin", "vault.base"], ReadPolicies(generated, "vault.positional"));
        Assert.Equal(["vault.base", "vault.named"], ReadPolicies(generated, "vault.named-wins"));
        Assert.Equal(["vault.base"], ReadPolicies(generated, "vault.inherited"));
        Assert.Equal(["vault.base", "vault.override"], ReadPolicies(generated, "vault.overridden"));
    }

    [Theory]
    [InlineData("[Authorize(Roles = \"Admin\")]", "")]
    [InlineData("[Authorize(Policy = \"vault.admin\", Roles = \"Admin\")]", "")]
    [InlineData("[Authorize(AuthenticationSchemes = \"Bearer\")]", "")]
    [InlineData("[VaultAdmin]", "")]
    [InlineData("[VaultAdmin(Policy = \"vault.admin\")]", "")]
    [InlineData("[CustomAuthorizeData]", "")]
    [InlineData("[Authorize]", "[Authorize(Roles = \"Admin\")]")]
    [InlineData("[Authorize]", "[VaultAdmin]")]
    public void Generator_refuses_authorization_the_tool_gate_cannot_enforce(
        string methodAuthorization,
        string baseAuthorization)
    {
        var source = $$"""
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Authorization;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            public sealed class VaultAdminAttribute : AuthorizeAttribute
            {
                public VaultAdminAttribute() : base("vault.admin")
                {
                    Roles = "Admin";
                }
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
            public sealed class CustomAuthorizeDataAttribute : Attribute, IAuthorizeData
            {
                public string? Policy { get; set; } = "vault.admin";

                public string? Roles { get; set; }

                public string? AuthenticationSchemes { get; set; }
            }

            {{baseAuthorization}}
            public abstract class VaultToolsBase
            {
            }

            [NhAiToolSet("vault")]
            public sealed class VaultTools : VaultToolsBase
            {
                [NhAiTool("read", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local | NhAiToolExposure.Agent)]
                {{methodAuthorization}}
                [Description("Read vault data.")]
                public Task<TaskResult<string>> ReadAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;

        AssertGeneratorDiagnostic(source, "NHAI018");
    }

    [Fact]
    public void Generator_hashes_positional_and_named_policies_as_the_same_contract()
    {
        // The contract covers the policy set, not the syntax that declares it, so the named form keeps
        // its published hash and the positional form now produces the same one.
        var named = GenerateAgentTool(string.Empty);
        var positional = GenerateAgentTool(string.Empty, "[Authorize(\"projects.manage\")]");

        Assert.Equal(ReadContractHash(named), ReadContractHash(positional));
        Assert.Equal(["projects.manage"], ReadPolicies(positional, "projects.change-status"));
    }

    private static string GenerateAuthorizationTools(string source)
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var references = trustedAssemblies
            .Append(MetadataReference.CreateFromFile(typeof(NhAiToolAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TaskResult<>).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorAuthorizationForms",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NhAiToolGenerator());

        driver = driver.RunGenerators(compilation);

        var runResult = driver.GetRunResult();
        Assert.DoesNotContain(runResult.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return Assert.Single(runResult.GeneratedTrees).ToString();
    }

    private static string[] ReadPolicies(string generated, string logicalId)
    {
        var descriptor = generated.IndexOf("\"" + logicalId + "\",", StringComparison.Ordinal);
        Assert.True(descriptor >= 0, $"The generated catalog has no descriptor for '{logicalId}'.");
        const string marker = "new global::System.String[]";
        var start = generated.IndexOf(marker, descriptor, StringComparison.Ordinal) + marker.Length;
        var end = generated.IndexOf("})", start, StringComparison.Ordinal);
        return generated[start..end]
            .Split(['\n', '\r', ' ', ',', '{'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim('"'))
            .ToArray();
    }

    private static string GenerateAgentTool(string namedArguments)
    {
        return GenerateAgentTool(namedArguments, string.Empty);
    }

    private static string GenerateAgentTool(string namedArguments, string methodAuthorization)
    {
        var toolSetAuthorization = methodAuthorization.Length == 0
            ? "[Authorize(Policy = \"projects.manage\")]"
            : "[Authorize]";
        var source = $$"""
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Authorization;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            [NhAiToolSet("projects")]
            {{toolSetAuthorization}}
            public sealed class ProjectTools
            {
                [NhAiTool(
                    "change-status",
                    1,
                    NhAiToolEffect.Mutation,
                    NhAiToolExposure.Agent,
                    Approval = NhAiApprovalRequirement.Required,
                    Idempotency = NhAiIdempotencySupport.Required{{namedArguments}})]
                {{methodAuthorization}}
                [Description("Change a project status.")]
                public Task<TaskResult<string>> ChangeAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var references = trustedAssemblies
            .Append(MetadataReference.CreateFromFile(typeof(NhAiToolAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TaskResult<>).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorAgentGrant",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NhAiToolGenerator());

        driver = driver.RunGenerators(compilation);

        var runResult = driver.GetRunResult();
        Assert.DoesNotContain(runResult.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return Assert.Single(runResult.GeneratedTrees).ToString();
    }

    [Fact]
    public void Generator_emits_the_declared_subject_and_includes_it_in_the_contract_hash_only_when_present()
    {
        var plain = GenerateSubjectTool("public sealed record CloseInput(Guid ProjectId, string Reason);");
        var positional = GenerateSubjectTool("public sealed record CloseInput([NhAiSubject(\"project\")] Guid ProjectId, string Reason);");
        var property = GenerateSubjectTool("""
            public sealed class CloseInput
            {
                [NhAiSubject("project")]
                public Guid ProjectId { get; init; }

                public string Reason { get; init; } = "";
            }
            """);
        var number = GenerateSubjectTool("public sealed record CloseInput([NhAiSubject(\"project-task\")] long? TaskNumber, string Reason);");

        Assert.DoesNotContain("Subject =", plain, StringComparison.Ordinal);
        Assert.Contains("Subject = new global::NewHeap.Platform.AI.NhAiToolSubject(\"project\", \"projectId\"),", positional, StringComparison.Ordinal);
        Assert.Contains("Subject = new global::NewHeap.Platform.AI.NhAiToolSubject(\"project\", \"projectId\"),", property, StringComparison.Ordinal);
        Assert.Contains("Subject = new global::NewHeap.Platform.AI.NhAiToolSubject(\"project-task\", \"taskNumber\"),", number, StringComparison.Ordinal);
        // The declaration enters the contract only when present; the same declaration hashes the same.
        Assert.NotEqual(ReadContractHash(plain), ReadContractHash(positional));
        Assert.Equal(ReadContractHash(positional), ReadContractHash(property));
        Assert.NotEqual(ReadContractHash(positional), ReadContractHash(number));
        Assert.Equal(ReadContractHash(plain), ReadContractHash(GenerateSubjectTool("public sealed record CloseInput(Guid ProjectId, string Reason);")));
    }

    [Theory]
    [InlineData("public sealed record CloseInput([NhAiSubject(\"Project\")] Guid ProjectId);", "NHAI015")]
    [InlineData("public sealed record CloseInput([NhAiSubject(\"nh-system\")] string Id);", "NHAI015")]
    [InlineData("public sealed record CloseInput([NhAiSubject(\"1-project\")] Guid ProjectId);", "NHAI015")]
    [InlineData("public sealed record CloseInput([NhAiSubject(\"project\")] DateTime ProjectId);", "NHAI016")]
    [InlineData("public sealed class CloseInput { [NhAiSubject(\"project\")] [System.Text.Json.Serialization.JsonIgnore] public Guid ProjectId { get; init; } }", "NHAI016")]
    [InlineData("public sealed record CloseInput([NhAiSubject(\"project\")] Guid ProjectId, [NhAiSubject(\"project\")] Guid OtherId);", "NHAI017")]
    public void Generator_rejects_invalid_subject_declarations(string inputDeclaration, string expectedDiagnostic)
    {
        AssertGeneratorDiagnostic(SubjectToolSource(inputDeclaration, "CloseInput input"), expectedDiagnostic);
    }

    [Fact]
    public void Generator_rejects_a_subject_on_the_whole_input()
    {
        AssertGeneratorDiagnostic(
            SubjectToolSource("public sealed record CloseInput(Guid ProjectId);", "[NhAiSubject(\"project\")] Guid input"),
            "NHAI016");
    }

    private static string GenerateSubjectTool(string inputDeclaration)
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var references = trustedAssemblies
            .Append(MetadataReference.CreateFromFile(typeof(NhAiToolAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TaskResult<>).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorSubject",
            [CSharpSyntaxTree.ParseText(SubjectToolSource(inputDeclaration, "CloseInput input"))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NhAiToolGenerator());

        driver = driver.RunGenerators(compilation);

        var runResult = driver.GetRunResult();
        Assert.DoesNotContain(runResult.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return Assert.Single(runResult.GeneratedTrees).ToString();
    }

    private static string SubjectToolSource(string inputDeclaration, string inputParameter)
    {
        return $$"""
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Authorization;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            {{inputDeclaration}}

            [NhAiToolSet("projects")]
            [Authorize(Policy = "projects.manage")]
            public sealed class ProjectTools
            {
                [NhAiTool(
                    "close",
                    1,
                    NhAiToolEffect.Mutation,
                    NhAiToolExposure.Agent,
                    Approval = NhAiApprovalRequirement.Required,
                    Idempotency = NhAiIdempotencySupport.Required)]
                [Description("Close a project.")]
                public Task<TaskResult<string>> CloseAsync(
                    {{inputParameter}},
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;
    }

    private static string ReadContractHash(string generated)
    {
        const string marker = "ContractHash = \"";
        var start = generated.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return generated[start..generated.IndexOf('"', start)];
    }

    [Theory]
    [InlineData("Bad.Name", "NHAI010")]
    [InlineData("orders.search-v2", "NHAI010")]
    public void Generator_rejects_invalid_or_version_encoded_export_names(
        string exportName,
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
                [NhAiTool("read", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
                [NhAiToolExportName("{{exportName}}")]
                [Description("Read generated data.")]
                public Task<TaskResult<string>> ReadAsync(
                    string input,
                    NhAiInvocationContext context,
                    CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;

        AssertGeneratorDiagnostic(source, expectedDiagnostic);
    }

    [Fact]
    public void Generator_rejects_duplicate_export_names_across_catalogs()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using System.Threading;
            using System.Threading.Tasks;
            using NewHeap.Platform.AI;
            using NewHeap.Platform.Common.Models;

            [NhAiToolSet("first")]
            public sealed class FirstTool
            {
                [NhAiTool("read", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
                [NhAiToolExportName("orders.search")]
                [Description("Read first data.")]
                public Task<TaskResult<string>> ReadAsync(string input, NhAiInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
            }

            [NhAiToolSet("second")]
            public sealed class SecondTool
            {
                [NhAiTool("read", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
                [NhAiToolExportName("orders.search")]
                [Description("Read second data.")]
                public Task<TaskResult<string>> ReadAsync(string input, NhAiInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;

        AssertGeneratorDiagnostic(source, "NHAI011");
    }

    private static void AssertGeneratorDiagnostic(string source, string expectedDiagnostic)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var references = trustedAssemblies
            .Append(MetadataReference.CreateFromFile(typeof(NhAiToolAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TaskResult<>).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "GeneratorExportDiagnostics",
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
    [NhAiToolExportName("generated.read")]
    [Description("Read generated data.")]
    public Task<TaskResult<string>> ReadAsync(
        ReadInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(TaskResult<string>.Succeeded(input.Value + ":" + context.ActorId));
    }
}
