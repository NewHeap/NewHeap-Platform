using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiMcpPublicationTests
{
    [Fact]
    public async Task Flat_result_without_a_context_is_compact_nulls_present_and_byte_stable()
    {
        await using var host = await PublicationHost.StartAsync();
        var tool = host.Tool("publication.flat-receipt");

        var result = await tool.CallAsync(Arguments("valid", "a+b<c> é"));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(
            "{\"execution\":\"executed\",\"evidenceReference\":null,\"status\":\"Active\",\"note\":\"a+b<c> é\"}",
            Text(result));
        Assert.Equal(JsonValueKind.Null, result.StructuredContent!.Value.GetProperty("evidenceReference").ValueKind);
    }

    [Fact]
    public async Task Flat_result_with_a_declared_context_uses_that_context_byte_for_byte()
    {
        await using var host = await PublicationHost.StartAsync();
        var tool = host.Tool("publication-context.flat-receipt");

        var success = await tool.CallAsync(new Dictionary<string, object?>
        {
            ["grant"] = "valid",
            ["note"] = "a+b<c> é"
        });
        var denial = await tool.CallAsync(new Dictionary<string, object?>
        {
            ["grant"] = "deny",
            ["note"] = "n"
        });

        Assert.Equal(
            "{\"execution\":\"executed\",\"evidence_reference\":null,\"status\":\"Active\",\"note\":\"a\\u002Bb\\u003Cc\\u003E \\u00E9\"}",
            Text(success));
        Assert.True(denial.IsError);
        Assert.Equal(
            "{\"execution\":\"deny\",\"evidence_reference\":\"grant:deny\",\"status\":\"Rejected\",\"note\":\"n\"}",
            Text(denial));
        Assert.Equal("approval-invalid-expired-or-replayed", Meta(denial, NhAiMcpResultMetadata.CodeKey));
    }

    [Theory]
    [InlineData("publication.flat-receipt")]
    [InlineData("publication.enveloped-receipt")]
    public async Task Gate_denial_without_data_publishes_code_and_message_for_flat_and_enveloped(string toolName)
    {
        await using var host = await PublicationHost.StartAsync(denyGate: true);
        var tool = host.Tool(toolName);
        var arguments = toolName.Contains("flat", StringComparison.Ordinal)
            ? Arguments("valid", "n")
            : new Dictionary<string, object?> { ["input"] = new PublicationRequest("valid", "n") };

        var result = await tool.CallAsync(arguments);

        Assert.True(result.IsError);
        Assert.Equal("ai-tool-authorization-denied: AI tool authorization was denied.", Text(result));
        Assert.Equal(
            "{\"code\":\"ai-tool-authorization-denied\",\"message\":\"AI tool authorization was denied.\"}",
            result.StructuredContent!.Value.GetRawText());
        Assert.Equal("ai-tool-authorization-denied", Meta(result, NhAiMcpResultMetadata.CodeKey));
        Assert.DoesNotContain("{0}", Text(result), StringComparison.Ordinal);

        var exception = await Assert.ThrowsAnyAsync<NhAiMcpToolException>(async () =>
        {
            if (toolName.Contains("flat", StringComparison.Ordinal))
            {
                await host.Client.CallNewHeapFlatToolAsync<PublicationRequest, PublicationReceipt>(
                    toolName,
                    new PublicationRequest("valid", "n"));
            }
            else
            {
                await host.Client.CallNewHeapToolAsync<PublicationRequest, PublicationReceipt>(
                    toolName,
                    new PublicationRequest("valid", "n"));
            }
        });
        Assert.Equal("ai-tool-authorization-denied", exception.Code);
        Assert.Equal("AI tool authorization was denied.", exception.FailureMessage);
        Assert.Null(exception.PayloadJson);
        Assert.Equal(
            $"NewHeap tool '{toolName}' failed with code 'ai-tool-authorization-denied': AI tool authorization was denied.",
            exception.Message);
    }

    [Fact]
    public async Task Tool_failure_without_data_publishes_its_own_code_and_message()
    {
        await using var host = await PublicationHost.StartAsync();

        var result = await host.Tool("publication.flat-receipt").CallAsync(Arguments("empty", "n"));

        Assert.True(result.IsError);
        Assert.Equal("engine-unavailable: The engine returned no receipt.", Text(result));
        Assert.Equal("engine-unavailable", result.StructuredContent!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Typed_authoritative_denial_exposes_code_message_evidence_and_payload()
    {
        await using var host = await PublicationHost.StartAsync();

        var exception = await Assert.ThrowsAsync<NhAiMcpToolException<PublicationReceipt>>(async () =>
            await host.Client.CallNewHeapFlatToolAsync<PublicationRequest, PublicationReceipt>(
                "publication.flat-receipt",
                new PublicationRequest("burned", "n")));

        Assert.Equal("approval-invalid-expired-or-replayed", exception.Code);
        Assert.Equal("The approval grant is invalid, expired or replayed.", exception.FailureMessage);
        Assert.Equal("grant-store:burned", exception.EvidenceReference);
        Assert.NotNull(exception.Payload);
        Assert.Equal("deny", exception.Payload.Execution);
        Assert.Equal("grant-store:burned", exception.Payload.EvidenceReference);
        Assert.Equal(
            "NewHeap tool 'publication.flat-receipt' failed with code 'approval-invalid-expired-or-replayed': The approval grant is invalid, expired or replayed.",
            exception.Message);
        Assert.Equal("grant-store:burned", Meta(exception.Result, NhAiMcpResultMetadata.EvidenceReferenceKey));
    }

    [Fact]
    public async Task Hint_overrides_are_published_and_audited_without_changing_governance()
    {
        await using var host = await PublicationHost.StartAsync();

        var flat = host.Tool("publication.flat-receipt").ProtocolTool.Annotations!;
        var enveloped = host.Tool("publication.enveloped-receipt").ProtocolTool.Annotations!;
        await host.Tool("publication.flat-receipt").CallAsync(Arguments("valid", "n"));

        Assert.True(flat.DestructiveHint);
        Assert.False(flat.ReadOnlyHint);
        Assert.False(flat.IdempotentHint);
        Assert.False(enveloped.IdempotentHint);
        Assert.False(enveloped.DestructiveHint);
        var record = Assert.Single(host.Audit.Records);
        Assert.Equal("destructive-hint=true", record.AnnotationOverrides);
        Assert.Equal("consumer-authoritative", record.ApprovalCode);
    }

    [Fact]
    public async Task Issuer_and_consumer_bound_to_one_grant_are_distinguishable_in_descriptor_and_audit()
    {
        await using var host = await PublicationHost.StartAsync();
        var catalog = new PublicationToolsNhAiCatalog();

        var issued = await host.Client.CallNewHeapFlatToolAsync<PublicationRequest, PublicationReceipt>(
            "publication.issue-grant",
            new PublicationRequest("grant-7", "issue"));
        var consumed = await host.Client.CallNewHeapFlatToolAsync<PublicationRequest, PublicationReceipt>(
            "publication.flat-receipt",
            new PublicationRequest("valid", "consume grant-7"));

        Assert.Equal("issued", issued.Execution);
        Assert.Equal("grant:grant-7", issued.EvidenceReference);
        Assert.Equal("executed", consumed.Execution);
        Assert.Equal(
            NhAiApprovalRole.Issuer,
            Assert.Single(catalog.Descriptors, item => item.Id == "publication.issue-grant").ApprovalRole);
        Assert.Equal(
            NhAiApprovalRole.Consumer,
            Assert.Single(catalog.Descriptors, item => item.Id == "publication.flat-receipt").ApprovalRole);
        Assert.Collection(
            host.Audit.Records,
            record =>
            {
                Assert.Equal("publication.issue-grant", record.ToolId);
                Assert.Equal("issuer", record.ApprovalCode);
                Assert.Null(record.IdempotencyCode);
                Assert.Equal(NhAiOutcomeKind.Succeeded, record.Outcome);
            },
            record =>
            {
                Assert.Equal("publication.flat-receipt", record.ToolId);
                Assert.Equal("consumer-authoritative", record.ApprovalCode);
                Assert.Equal("consumer-authoritative", record.IdempotencyCode);
            });
    }

    [Fact]
    public void Widening_hint_override_on_a_hand_built_descriptor_fails_before_publication()
    {
        var descriptor = Assert.Single(
            new PublicationToolsNhAiCatalog().Descriptors,
            item => item.Id == "publication.flat-receipt") with
        {
            IdempotentHint = NhAiToolHint.True
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NhAiToolAnnotationHints.Resolve(descriptor));

        Assert.Contains("IdempotentHint = True", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pipeline_failures_carry_stable_named_codes()
    {
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(
                new NhAiInvocationContext("actor-1", "test", new Dictionary<string, string>())),
            new NhAiTestBudgetManager(allow: false));
        var descriptor = Assert.Single(
            new PublicationToolsNhAiCatalog().Descriptors,
            item => item.Id == "publication.flat-receipt");

        var result = await invoker.InvokeAsync(
            descriptor,
            new PublicationRequest("valid", "n"),
            (_, _) => Task.FromResult(TaskResult<PublicationReceipt>.Succeeded(
                new PublicationReceipt("executed", null, PublicationStatus.Active, "n"))));

        Assert.False(result.Success);
        Assert.Equal(
            NhAiToolFailureCodes.BudgetDenied,
            Assert.Single(result.GetResultItems()).Name);
    }

    private static Dictionary<string, object?> Arguments(string grant, string note)
    {
        return new Dictionary<string, object?>
        {
            ["grant"] = grant,
            ["note"] = note
        };
    }

    private static string Text(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static string? Meta(CallToolResult result, string key)
    {
        return result.Meta?[key]?.GetValue<string>();
    }

    private sealed class PublicationHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly McpServer _server;
        private readonly IReadOnlyList<McpServerTool> _tools;

        private PublicationHost(
            ServiceProvider provider,
            AsyncServiceScope scope,
            McpServer server,
            McpClient client,
            IReadOnlyList<McpServerTool> tools)
        {
            _provider = provider;
            _scope = scope;
            _server = server;
            Client = client;
            _tools = tools;
        }

        public McpClient Client { get; }

        public NhAiCapturedAuditSink Audit => _scope.ServiceProvider
            .GetServices<INhAiAuditSink>()
            .OfType<NhAiCapturedAuditSink>()
            .Single();

        public static async Task<PublicationHost> StartAsync(bool denyGate = false)
        {
            var context = new NhAiInvocationContext(
                "agent-1",
                "publication",
                new Dictionary<string, string>());
            var services = new ServiceCollection();
            services.AddScoped<PublicationTools>();
            services.AddScoped<PublicationContextTools>();
            services.AddScoped<INhAiToolInvocationGate>(_ => denyGate
                ? new NhAiTestInvocationGate((_, _) => ValueTask.FromResult(
                    TaskResult<NhAiInvocationContext>.Failed(
                        "ai-tool-authorization-denied",
                        "AI tool authorization was denied.")))
                : NhAiTestInvocationGate.Authorized(context));
            services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
            services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
            services.AddNewHeapPlatformAI(ai => ai
                .AddGeneratedToolCatalog<PublicationToolsNhAiCatalog>()
                .AddGeneratedToolCatalog<PublicationContextToolsNhAiCatalog>()
                .UseAuthoritativeExecutionEvidenceValidator<PublicationEvidenceValidator>()
                .AddAuditSink<NhAiCapturedAuditSink>());
            services.AddNewHeapPlatformAIMcp();
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            var tools = await scope.ServiceProvider
                .GetRequiredService<INhAiMcpToolAdapter>()
                .CreateToolsAsync(scope.ServiceProvider, context);
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var server = McpServer.Create(
                new StreamServerTransport(
                    clientToServer.Reader.AsStream(),
                    serverToClient.Writer.AsStream()),
                new McpServerOptions
                {
                    ScopeRequests = false,
                    ToolCollection = [.. tools]
                },
                serviceProvider: scope.ServiceProvider);
            _ = server.RunAsync();
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream()));
            var listed = await client.ListToolsAsync();
            return new PublicationHost(provider, scope, server, client, tools)
            {
                Listed = listed
            };
        }

        public IList<McpClientTool> Listed { get; private init; } = [];

        public McpClientTool Tool(string name)
        {
            return Listed.Single(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _server.DisposeAsync();
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}

public sealed record PublicationRequest(string Grant, string Note);

public enum PublicationStatus
{
    Active,
    Rejected
}

public sealed record PublicationReceipt(
    string Execution,
    string? EvidenceReference,
    PublicationStatus Status,
    string Note);

internal static class PublicationEngine
{
    public static Task<TaskResult<PublicationReceipt>> ApplyAsync(PublicationRequest input)
    {
        if (string.Equals(input.Grant, "empty", StringComparison.Ordinal))
        {
            return Task.FromResult(TaskResult<PublicationReceipt>.Failed(
                "engine-unavailable",
                "The engine returned no receipt."));
        }
        if (string.Equals(input.Grant, "deny", StringComparison.Ordinal))
        {
            return Task.FromResult(
                TaskResult<PublicationReceipt>
                    .Failed("approval-invalid-expired-or-replayed", "The approval grant is invalid.")
                    .WithData(new PublicationReceipt(
                        "deny",
                        "grant:deny",
                        PublicationStatus.Rejected,
                        input.Note)));
        }
        return Task.FromResult(TaskResult<PublicationReceipt>.Succeeded(
            new PublicationReceipt("executed", null, PublicationStatus.Active, input.Note)));
    }
}

[NhAiToolSet("publication")]
[Authorize(Policy = "publication")]
public sealed class PublicationTools
{
    [NhAiTool(
        "flat-receipt",
        1,
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        ExportSchema = NhAiToolExportSchema.Flat,
        DestructiveHint = NhAiToolHint.True)]
    [NhAiToolExportName("publication.flat-receipt")]
    [Description("Apply a consumer-authoritative change and return a flat receipt.")]
    public Task<TaskResult<PublicationReceipt>> FlatReceiptAsync(
        PublicationRequest input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PublicationEngine.ApplyAsync(input);
    }

    [NhAiTool(
        "enveloped-receipt",
        1,
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        IdempotentHint = NhAiToolHint.False)]
    [NhAiToolExportName("publication.enveloped-receipt")]
    [Description("Apply a consumer-authoritative change and return an enveloped receipt.")]
    public Task<TaskResult<PublicationReceipt>> EnvelopedReceiptAsync(
        PublicationRequest input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PublicationEngine.ApplyAsync(input);
    }

    [NhAiTool(
        "issue-grant",
        1,
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.Issuer,
        ExportSchema = NhAiToolExportSchema.Flat)]
    [NhAiToolExportName("publication.issue-grant")]
    [Description("Issue a single-use approval grant under the consumer's own write authorization.")]
    public Task<TaskResult<PublicationReceipt>> IssueGrantAsync(
        PublicationRequest input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TaskResult<PublicationReceipt>.Succeeded(
            new PublicationReceipt("issued", $"grant:{input.Grant}", PublicationStatus.Active, input.Note)));
    }
}

[NhAiToolSet("publication-context", JsonSerializerContextType = typeof(PublicationJsonContext))]
[Authorize(Policy = "publication")]
public sealed class PublicationContextTools
{
    [NhAiTool(
        "flat-receipt",
        1,
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        ExportSchema = NhAiToolExportSchema.Flat)]
    [NhAiToolExportName("publication-context.flat-receipt")]
    [Description("Apply a consumer-authoritative change with a snake_case wire contract.")]
    public Task<TaskResult<PublicationReceipt>> FlatReceiptAsync(
        PublicationRequest input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PublicationEngine.ApplyAsync(input);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PublicationRequest))]
[JsonSerializable(typeof(TaskResult<PublicationReceipt>))]
internal partial class PublicationJsonContext : JsonSerializerContext
{
}

public sealed class PublicationEvidenceValidator : INhAiAuthoritativeExecutionEvidenceValidator
{
    public ValueTask<TaskResult<NhAiAuthoritativeExecutionEvidence>> ValidateAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments is PublicationRequest { Grant: "burned" } request)
        {
            return ValueTask.FromResult(
                TaskResult<NhAiAuthoritativeExecutionEvidence>
                    .Failed(
                        "approval-invalid-expired-or-replayed",
                        "The approval grant is invalid, expired or replayed.")
                    .WithData(NhAiAuthoritativeExecutionEvidence.Denied(
                        new PublicationReceipt(
                            "deny",
                            "grant-store:burned",
                            PublicationStatus.Rejected,
                            request.Note),
                        "grant-store:burned")));
        }

        return ValueTask.FromResult(
            TaskResult<NhAiAuthoritativeExecutionEvidence>.Succeeded(
                NhAiAuthoritativeExecutionEvidence.None));
    }
}
