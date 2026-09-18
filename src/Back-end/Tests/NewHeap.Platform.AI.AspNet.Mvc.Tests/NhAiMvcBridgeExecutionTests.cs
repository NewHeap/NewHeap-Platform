using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

public sealed class NhAiMvcBridgeExecutionTests
{
    [Fact]
    public async Task Authorized_read_executes_through_the_api_as_the_calling_user()
    {
        using var factory = new BridgeApiFactory();

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, "test-api_order_get-by-id_v1", new { id = 42 });

        Assert.True(result.Success, result.Errors);
        Assert.Equal(200, result.Data.GetProperty("status").GetInt32());
        Assert.False(result.Data.GetProperty("truncated").GetBoolean());
        Assert.Equal(42, result.Data.GetProperty("body").GetProperty("id").GetInt32());
        Assert.Equal("customer-42", result.Data.GetProperty("body").GetProperty("customer").GetString());
    }

    [Fact]
    public async Task Collection_read_maps_paging_and_search_to_the_query_contract()
    {
        using var factory = new BridgeApiFactory();

        var result = await factory.InvokeAsync(
            TestTokenHandler.Viewer,
            "test-api_order_get_v1",
            new { page = 3, itemsPerPage = 7, search = "acme", status = "Shipped" });

        Assert.True(result.Success, result.Errors);
        var order = result.Data.GetProperty("body")[0];
        Assert.Equal("page-3-7-acme", order.GetProperty("customer").GetString());
        Assert.Equal(2, order.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("forbidden", NhAiBridgeFailureCodes.Forbidden, "forbidden-body-text")]
    [InlineData("conflict", NhAiBridgeFailureCodes.Conflict, "conflict-body-text")]
    [InlineData("broken", NhAiBridgeFailureCodes.Upstream, "broken-body-text")]
    public async Task Http_failures_become_structured_codes_without_response_body_text(
        string action,
        string expectedCode,
        string bodyText)
    {
        using var factory = new BridgeApiFactory();

        var result = await ExecuteAsync(factory, TestTokenHandler.Viewer, "test-api.probe." + action, new { });

        Assert.False(result.Success);
        Assert.Equal(expectedCode, Code(result));
        Assert.Null(result.Data);
        Assert.DoesNotContain(bodyText, string.Join(" ", result.AllErrorMessages), StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(bodyText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_found_and_unauthenticated_calls_map_to_their_codes()
    {
        using var factory = new BridgeApiFactory();

        var notFound = await ExecuteAsync(factory, TestTokenHandler.Viewer, "test-api.order.get-by-id", new { id = 404 });
        var unauthenticated = await ExecuteAsync(factory, null, "test-api.order.get-by-id", new { id = 1 });

        Assert.Equal(NhAiBridgeFailureCodes.NotFound, Code(notFound));
        Assert.Equal(NhAiBridgeFailureCodes.Unauthenticated, Code(unauthenticated));
    }

    [Fact]
    public async Task Validation_failure_returns_the_model_state_as_data()
    {
        using var factory = new BridgeApiFactory();

        var result = await ExecuteAsync(
            factory,
            TestTokenHandler.Manager,
            "test-api.probe.validate",
            new { body = new { customer = "acme" } },
            "validate-key");

        Assert.False(result.Success);
        Assert.Equal(NhAiBridgeFailureCodes.Validation, Code(result));
        Assert.Equal(400, result.Data!.Status);
        Assert.Equal(
            "Customer is not allowed.",
            result.Data.Body!.Value.GetProperty("errors").GetProperty("Customer")[0].GetString());
        Assert.DoesNotContain("not allowed", string.Join(" ", result.AllErrorMessages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_results_are_truncated_below_the_result_limit_with_a_paging_hint()
    {
        using var factory = new BridgeApiFactory();

        var result = await factory.InvokeAsync(TestTokenHandler.Viewer, "test-api_probe_large_v1", new { items = 3000 });

        Assert.True(result.Success, result.Errors);
        var data = result.Data;
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.TryGetProperty("body", out _));
        Assert.StartsWith("[{\"index\":0", data.GetProperty("bodyText").GetString(), StringComparison.Ordinal);
        Assert.Equal(NhAiMvcBridgeDefaults.TruncationHint, data.GetProperty("hint").GetString());
        Assert.True(data.GetProperty("bodyBytes").GetInt64() > 65_536);
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
            data.Deserialize<NhAiBridgeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        Assert.InRange(envelopeBytes, 60_000, 65_536);
    }

    [Fact]
    public void Truncation_respects_small_limits_and_escaped_text()
    {
        var text = string.Concat(Enumerable.Repeat("\"<é>\"", 400));
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        var envelope = NhAiMvcBridgeExecutor.CreateEnvelope(200, "application/json", bytes[..1000], bytes.Length, 1000);

        Assert.True(envelope.Truncated);
        Assert.InRange(
            JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length,
            1,
            1000);
        Assert.Equal(bytes.Length, envelope.BodyBytes);
        Assert.False(string.IsNullOrEmpty(envelope.BodyText));
    }

    [Fact]
    public async Task Slow_api_calls_fail_with_the_timeout_code()
    {
        using var factory = new BridgeApiFactory();

        var result = await factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var tool = (await client.ListToolsAsync()).Single(item => item.Name == "test-api_probe_slow_v1");
            return await tool.CallAsync(new Dictionary<string, object?> { ["input"] = new { } });
        });

        Assert.True(result.IsError);
        Assert.Equal(NhAiBridgeFailureCodes.Timeout, McpCode(result));
    }

    [Fact]
    public async Task Outgoing_request_carries_only_the_contracted_headers()
    {
        using var factory = new BridgeApiFactory();

        var result = await ExecuteAsync(
            factory,
            TestTokenHandler.Manager,
            "test-api.probe.echo-write",
            new { body = new { customer = "acme" } },
            "lease-key-1");

        Assert.True(result.Success);
        var echo = result.Data!.Body!.Value;
        Assert.Equal("Bearer " + TestTokenHandler.Manager, echo.GetProperty("authorization").GetString());
        Assert.Equal("lease-key-1", echo.GetProperty("idempotencyKey").GetString());
        Assert.True(Guid.TryParse(echo.GetProperty("invocation").GetString(), out _));
        Assert.Equal("nl-NL", echo.GetProperty("acceptLanguage").GetString());
        Assert.Equal(string.Empty, echo.GetProperty("cookie").GetString());
        Assert.Equal("acme", echo.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Governed_mutation_forwards_the_idempotency_lease_key_and_keeps_the_token_out_of_context_audit_and_logs()
    {
        var effectPolicy = new CapturingEffectPolicy();
        using var factory = new BridgeApiFactory(configureServices: services =>
            services.AddScoped<INhAiEffectPolicy>(_ => effectPolicy));

        var result = await factory.AsUserAsync(
            TestTokenHandler.Manager,
            async services =>
            {
                var catalog = services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
                var function = catalog.CreateFunctions(services).Single(item => item.Name == "test-api_probe_echo-write_v1");
                var output = await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
                {
                    ["input"] = JsonSerializer.SerializeToElement(new { body = new { customer = "acme" } })
                });
                return new BridgeToolResult((JsonElement)output!);
            },
            new Dictionary<string, string> { ["Idempotency-Key"] = "proposal-7.retry-1" });

        Assert.True(result.Success, result.Errors);
        var echo = result.Data.GetProperty("body");
        Assert.Equal("proposal-7.retry-1", echo.GetProperty("idempotencyKey").GetString());
        var idempotencyManager = Assert.IsType<TestIdempotencyManager>(
            factory.Services.GetRequiredService<INhAiIdempotencyManager>());
        var idempotency = Assert.Single(idempotencyManager.Requests);
        Assert.Equal("proposal-7.retry-1", idempotency.IdempotencyKey);

        var context = Assert.Single(effectPolicy.Contexts);
        var contextText = context + " " + string.Join(",", context.Scope.Select(pair => pair.Key + "=" + pair.Value));
        Assert.DoesNotContain(TestTokenHandler.Manager, contextText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", contextText, StringComparison.Ordinal);
        var audit = factory.Services.GetRequiredService<NhAiCapturedAuditSink>().Records;
        Assert.Contains(audit, record => record.ToolId == "test-api.probe.echo-write" && record.Outcome == NhAiOutcomeKind.Succeeded);
        Assert.DoesNotContain(TestTokenHandler.Manager, JsonSerializer.Serialize(audit), StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(TestTokenHandler.Manager, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("acme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mutation_without_approval_evidence_is_stopped_before_the_http_call()
    {
        using var factory = new BridgeApiFactory();

        var result = await factory.WithMcpClientAsync(TestTokenHandler.Manager, async client =>
        {
            var tool = (await client.ListToolsAsync()).Single(item => item.Name == "test-api_order_create_v1");
            return await tool.CallAsync(new Dictionary<string, object?>
            {
                ["input"] = new { body = new { customer = "acme" } }
            });
        });

        Assert.True(result.IsError);
        Assert.Equal(NhAiToolFailureCodes.ApprovalRequired, McpCode(result));
        Assert.DoesNotContain(
            factory.Logs.Entries,
            entry => entry.Contains("test-api.order.create returned HTTP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalog_attestation_succeeds_and_the_agent_path_accepts_the_functions()
    {
        using var factory = new BridgeApiFactory();

        await factory.AsUserAsync(TestTokenHandler.Viewer, services =>
        {
            var catalog = services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
            NhAiToolCatalogAttestation.Validate(catalog, services);
            var functions = catalog.CreateFunctions(services);
            Assert.All(functions, function => Assert.IsAssignableFrom<INhAiGovernedAIFunction>(function));
            var getById = functions.Single(function => function.Name == "test-api_order_get-by-id_v1");
            Assert.Equal(
                "integer",
                getById.JsonSchema.GetProperty("properties").GetProperty("input")
                    .GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
            return Task.FromResult(true);
        });
        Assert.Contains(
            factory.Logs.Entries,
            entry => entry.Contains("AI API bridge 'test-api' published 16 tools.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mcp_export_lists_and_calls_bridge_tools()
    {
        using var factory = new BridgeApiFactory();

        var (names, result) = await factory.WithMcpClientAsync(TestTokenHandler.Viewer, async client =>
        {
            var tools = await client.ListToolsAsync();
            var tool = tools.Single(item => item.Name == "test-api_order_get-by-id_v1");
            var called = await tool.CallAsync(new Dictionary<string, object?> { ["input"] = new { id = 5 } });
            return (tools.Select(item => item.Name).ToArray(), called);
        });

        Assert.Contains("test-api_order_get_v1", names);
        Assert.DoesNotContain("test-api_order_create_v1", names);
        Assert.NotEqual(true, result.IsError);
        var data = result.StructuredContent!.Value.GetProperty("data");
        Assert.Equal(5, data.GetProperty("body").GetProperty("id").GetInt32());
    }

    private static Task<TaskResult<NhAiBridgeResponse>> ExecuteAsync(
        BridgeApiFactory factory,
        string? token,
        string toolId,
        object input,
        string? idempotencyKey = null)
    {
        return factory.AsUserAsync(token, services =>
        {
            var catalog = services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
            var descriptor = catalog.Descriptors.Single(item => item.Id == toolId);
            Assert.True(catalog.TryGetAction(descriptor, out var action));
            return services.GetRequiredService<INhAiMvcBridgeExecutor>().ExecuteAsync(
                action,
                descriptor,
                JsonSerializer.SerializeToElement(input),
                TestContexts.Create(idempotencyKey));
        });
    }

    private static string? McpCode(CallToolResult result)
    {
        return result.Meta?[NhAiMcpResultMetadata.CodeKey]?.GetValue<string>();
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    public sealed class CapturingEffectPolicy : INhAiEffectPolicy
    {
        public List<NhAiInvocationContext> Contexts { get; } = [];

        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return ValueTask.FromResult(new NhAiEffectDecision(NhAiEffectDecisionKind.Allow, "test-allow"));
        }
    }
}
