using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// Every generated governed tool returns the shared <see cref="NhAiToolResult"/> shape: the data on
/// success, otherwise the stable code and message, so a model can tell one failure from another.
/// </summary>
public sealed class NhAiToolResultTests
{
    private static readonly NhAiInvocationContext Context = new(
        "actor-1",
        "tool-invocation",
        new Dictionary<string, string>());

    [Fact]
    public async Task Success_returns_only_success_and_data()
    {
        await using var probe = ResultProbe.Create();

        var result = await probe.InvokeAsync("lookup", new ResultProbeInput("project-1", ResultProbeTools.Found));

        Assert.Equal(
            """{"success":true,"data":{"subject":"project-1"}}""",
            result.GetRawText());
    }

    [Fact]
    public async Task Expected_failures_reach_the_model_with_their_own_stable_code_and_message()
    {
        await using var probe = ResultProbe.Create();

        var unavailable = await probe.InvokeAsync("lookup", new ResultProbeInput("project-1", ResultProbeTools.Unavailable));
        var limited = await probe.InvokeAsync("lookup", new ResultProbeInput("project-1", ResultProbeTools.Limited));

        Assert.Equal(
            """{"success":false,"code":"result-probe-subject-unavailable","message":"The subject is not available."}""",
            unavailable.GetRawText());
        Assert.Equal(
            """{"success":false,"code":"result-probe-limit-reached","message":"The daily limit is reached."}""",
            limited.GetRawText());
    }

    [Fact]
    public async Task Pipeline_failure_reaches_the_model_with_the_pipeline_code()
    {
        await using var probe = ResultProbe.Create(NhAiTestInvocationGate.Denied("The caller may not use this tool."));

        var result = await probe.InvokeAsync("lookup", new ResultProbeInput("project-1", ResultProbeTools.Found));

        Assert.False(result.GetProperty(NhAiToolResult.SuccessPropertyName).GetBoolean());
        Assert.Equal(
            NhAiToolFailureCodes.AuthorizationDenied,
            result.GetProperty(NhAiToolResult.CodePropertyName).GetString());
        Assert.Equal(
            "The caller may not use this tool.",
            result.GetProperty(NhAiToolResult.MessagePropertyName).GetString());
        Assert.False(result.TryGetProperty(NhAiToolResult.DataPropertyName, out _));
        Assert.Equal(0, probe.Tool.Calls);
    }

    [Fact]
    public async Task Failure_without_a_code_keeps_every_message_with_its_field()
    {
        await using var probe = ResultProbe.Create();

        var result = await probe.InvokeAsync("lookup", new ResultProbeInput("project-1", ResultProbeTools.Invalid));

        Assert.Equal(
            """{"success":false,"code":"ai-tool-failed","message":"Title: The title is required. DueDate: The due date must be in the future."}""",
            result.GetRawText());
    }

    [Fact]
    public async Task Failure_with_a_typed_payload_keeps_the_payload_as_data()
    {
        await using var probe = ResultProbe.Create();

        var result = await probe.InvokeAsync("lookup", new ResultProbeInput("project-1", ResultProbeTools.Denied));

        Assert.Equal(
            """{"success":false,"code":"result-probe-denied","message":"The change was not executed.","data":{"subject":"project-1"}}""",
            result.GetRawText());
    }

    [Fact]
    public async Task Return_schema_is_the_shared_shape_around_the_descriptor_output_schema()
    {
        await using var probe = ResultProbe.Create();
        var function = probe.CreateFunction("lookup");
        var descriptor = Assert.IsAssignableFrom<INhAiGovernedAIFunction>(function).Descriptor;

        var schema = Assert.IsType<JsonElement>(function.ReturnJsonSchema);

        var properties = schema.GetProperty("properties");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(
            [NhAiToolResult.SuccessPropertyName, NhAiToolResult.CodePropertyName, NhAiToolResult.MessagePropertyName, NhAiToolResult.DataPropertyName],
            properties.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("boolean", properties.GetProperty(NhAiToolResult.SuccessPropertyName).GetProperty("type").GetString());
        Assert.Equal(
            JsonSerializer.Serialize(JsonDocument.Parse(descriptor.OutputSchemaJson).RootElement),
            JsonSerializer.Serialize(properties.GetProperty(NhAiToolResult.DataPropertyName)));
        Assert.Equal(NhAiToolResult.SuccessPropertyName, Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Declared_context_needs_only_the_tool_input_and_output_and_keeps_its_naming_inside_data()
    {
        await using var provider = CreateProvider(new ResultContextProbeTools());
        var function = Assert.Single(new ResultContextProbeToolsNhAiCatalog().CreateFunctions(provider));

        var success = Assert.IsType<JsonElement>(await function.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(
                new ResultContextProbeInput("project-1", false),
                ResultContextProbeJsonContext.Default.ResultContextProbeInput)
        }));
        var failure = Assert.IsType<JsonElement>(await function.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(
                new ResultContextProbeInput("project-1", true),
                ResultContextProbeJsonContext.Default.ResultContextProbeInput)
        }));

        // The context declares no TaskResult<T>; the envelope names stay fixed under its Pascal naming.
        Assert.Equal("""{"success":true,"data":{"Subject":"project-1","Note":null}}""", success.GetRawText());
        Assert.Equal(
            """{"success":false,"code":"result-probe-subject-unavailable","message":"The subject is not available."}""",
            failure.GetRawText());
    }

    [Fact]
    public async Task Mcp_envelope_export_publishes_the_shared_shape_and_its_schema()
    {
        await using var probe = ResultProbe.Create();
        var tools = await probe.Scope.ServiceProvider
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(probe.Scope.ServiceProvider, Context);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ScopeRequests = false, ToolCollection = [.. tools] },
            serviceProvider: probe.Scope.ServiceProvider);
        _ = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        var tool = Assert.Single(await client.ListToolsAsync());

        var denied = await tool.CallAsync(new Dictionary<string, object?>
        {
            ["input"] = new { subject = "project-1", mode = ResultProbeTools.Denied }
        });
        var invalid = await tool.CallAsync(new Dictionary<string, object?>
        {
            ["input"] = new { subject = "project-1", mode = ResultProbeTools.Invalid }
        });

        var properties = tool.ReturnJsonSchema!.Value.GetProperty("properties");
        Assert.True(properties.TryGetProperty(NhAiToolResult.CodePropertyName, out _));
        Assert.False(properties.TryGetProperty("allErrorMessages", out _));
        Assert.True(denied.IsError);
        Assert.Equal("result-probe-denied", denied.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Equal("project-1", denied.StructuredContent.Value.GetProperty("data").GetProperty("subject").GetString());
        Assert.True(invalid.IsError);
        Assert.Equal(
            "Title: The title is required. DueDate: The due date must be in the future.",
            invalid.Meta![NhAiMcpResultMetadata.MessageKey]!.GetValue<string>());
    }

    [Fact]
    public async Task Imported_mcp_tool_returns_the_remote_result_as_data_and_a_stable_code_for_a_remote_error()
    {
        McpServerTool[] remoteTools =
        [
            McpServerTool.Create(
                (string order) => new ResultProbeOrder(order, "planned"),
                new McpServerToolCreateOptions { Name = "lookup", Description = "Looks up an order.", UseStructuredContent = true }),
            McpServerTool.Create(
                (string order) => new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "Order " + order + " does not exist." }]
                },
                new McpServerToolCreateOptions { Name = "status", Description = "Reads the status of an order." })
        ];
        var services = new ServiceCollection();
        services.AddSingleton<NhAiCapturedAuditSink>();
        services.AddSingleton<INhAiAuditSink>(provider => provider.GetRequiredService<NhAiCapturedAuditSink>());
        services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(Context));
        services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAIMcp();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ScopeRequests = false, ToolCollection = [.. remoteTools] },
            serviceProvider: scope.ServiceProvider);
        _ = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        var catalog = scope.ServiceProvider.GetRequiredService<INhAiMcpClientToolImporter>().Import(
            await client.ListToolsAsync(),
            new NhAiMcpImportOptions(
                "orders",
                "orders",
                [ImportPolicy("lookup"), ImportPolicy("status")]));
        var functions = catalog.CreateFunctions(scope.ServiceProvider);
        var lookup = Assert.Single(functions, function => function.Name == "mcp_orders_lookup");
        var status = Assert.Single(functions, function => function.Name == "mcp_orders_status");

        var found = Assert.IsType<JsonElement>(await lookup.InvokeAsync(new AIFunctionArguments { ["order"] = "A-1" }));
        var failed = Assert.IsType<JsonElement>(await status.InvokeAsync(new AIFunctionArguments { ["order"] = "A-2" }));

        Assert.True(found.GetProperty(NhAiToolResult.SuccessPropertyName).GetBoolean());
        Assert.Equal(
            "planned",
            found.GetProperty(NhAiToolResult.DataPropertyName).GetProperty("structuredContent").GetProperty("status").GetString());
        Assert.Equal(
            [NhAiToolResult.SuccessPropertyName, NhAiToolResult.CodePropertyName, NhAiToolResult.MessagePropertyName, NhAiToolResult.DataPropertyName],
            failed.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(NhAiMcpImportedToolCodes.RemoteError, failed.GetProperty(NhAiToolResult.CodePropertyName).GetString());
        var remoteResult = failed.GetProperty(NhAiToolResult.DataPropertyName);
        Assert.True(remoteResult.GetProperty("isError").GetBoolean());
        Assert.Equal(
            "Order A-2 does not exist.",
            Assert.Single(remoteResult.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(
            NhAiMcpImportedToolCodes.RemoteError,
            Assert.Single(provider.GetRequiredService<NhAiCapturedAuditSink>().Records, record => record.ToolId == "mcp.orders.status").ResultCode);

        // The return schema is the shared shape around the remote result, typed by the remote output schema.
        var descriptor = ((INhAiGovernedAIFunction)lookup).Descriptor;
        var structured = lookup.ReturnJsonSchema!.Value
            .GetProperty("properties").GetProperty(NhAiToolResult.DataPropertyName)
            .GetProperty("properties").GetProperty("structuredContent");
        Assert.Equal(
            JsonSerializer.Serialize(JsonDocument.Parse(descriptor.OutputSchemaJson).RootElement),
            JsonSerializer.Serialize(structured));
        Assert.True(structured.GetProperty("properties").TryGetProperty("status", out _));
    }

    [Fact]
    public void Failed_requires_a_stable_code()
    {
        var refused = NhAiToolResult.Failed("result-probe-refused", null);

        Assert.Equal("""{"success":false,"code":"result-probe-refused"}""", refused.GetRawText());
        Assert.Throws<ArgumentException>(() => NhAiToolResult.Failed("Not a code", "The message."));
    }

    [Fact]
    public void Serializer_options_without_a_resolver_are_rejected_before_any_call()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Throws<ArgumentException>(() => NhAiToolResult.CreateMarshaller<ResultProbeOutput>(options));
        Assert.Throws<ArgumentException>(() => NhAiToolResult.Create(
            TaskResult<ResultProbeOutput>.Succeeded(new ResultProbeOutput("project-1")),
            options));
    }

    private static NhAiMcpImportedToolPolicy ImportPolicy(string remoteName)
    {
        return new NhAiMcpImportedToolPolicy(
            remoteName,
            remoteName,
            "Reads orders through the order service.",
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Agent,
            ["orders.read"])
        {
            Approval = NhAiApprovalRequirement.NotRequired
        };
    }

    private static ServiceProvider CreateProvider<TTools>(TTools tools)
        where TTools : class
    {
        var services = new ServiceCollection();
        services.AddSingleton(tools);
        services.AddSingleton<INhAiToolInvocationGate>(NhAiTestInvocationGate.Authorized(Context));
        services.AddSingleton<INhAiBudgetManager>(new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAI();
        return services.BuildServiceProvider();
    }

    private sealed class ResultProbe : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private ResultProbe(ServiceProvider provider)
        {
            _provider = provider;
            Scope = provider.CreateAsyncScope();
        }

        public AsyncServiceScope Scope { get; }

        public ResultProbeTools Tool => _provider.GetRequiredService<ResultProbeTools>();

        public static ResultProbe Create(INhAiToolInvocationGate? gate = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ResultProbeTools>();
            services.AddScoped(_ => gate ?? NhAiTestInvocationGate.Authorized(Context));
            services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
            services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
            services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ResultProbeToolsNhAiCatalog>());
            services.AddNewHeapPlatformAIMcp();
            return new ResultProbe(services.BuildServiceProvider());
        }

        public AIFunction CreateFunction(string toolId)
        {
            return Assert.Single(
                Scope.ServiceProvider.GetServices<INhAiToolCatalog>()
                    .Single(catalog => catalog is ResultProbeToolsNhAiCatalog)
                    .CreateFunctions(Scope.ServiceProvider),
                function => ((INhAiGovernedAIFunction)function).Descriptor.Id == "result-probe." + toolId);
        }

        public async Task<JsonElement> InvokeAsync(string toolId, ResultProbeInput input)
        {
            var output = await CreateFunction(toolId).InvokeAsync(new AIFunctionArguments
            {
                ["input"] = input
            });
            return Assert.IsType<JsonElement>(output);
        }

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}

public sealed record ResultProbeInput(string Subject, string Mode);

public sealed record ResultProbeOutput(string Subject);

[NhAiToolSet("result-probe")]
public sealed class ResultProbeTools
{
    public const string Found = "found";
    public const string Unavailable = "unavailable";
    public const string Limited = "limited";
    public const string Invalid = "invalid";
    public const string Denied = "denied";

    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    [NhAiTool("lookup", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local | NhAiToolExposure.Mcp)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "result-probe")]
    [Description("Look up one subject.")]
    public Task<TaskResult<ResultProbeOutput>> LookupAsync(
        ResultProbeInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        var result = input.Mode switch
        {
            Found => TaskResult<ResultProbeOutput>.Succeeded(new ResultProbeOutput(input.Subject)),
            Unavailable => TaskResult<ResultProbeOutput>.Failed(
                "result-probe-subject-unavailable",
                "The subject is not available."),
            Limited => TaskResult<ResultProbeOutput>.Failed(
                "result-probe-limit-reached",
                "The daily limit is reached."),
            Denied => TaskResult<ResultProbeOutput>
                .Failed("result-probe-denied", "The change was not executed.")
                .WithData(new ResultProbeOutput(input.Subject)),
            _ => new TaskResult<ResultProbeOutput>()
                .AddError("Title", "The title is required.")
                .AddError("DueDate", "The due date must be in the future.")
        };
        return Task.FromResult(result);
    }
}

public sealed record ResultContextProbeInput(string Subject, bool Unavailable);

public sealed record ResultContextProbeOutput(string Subject, string? Note);

[NhAiToolSet("result-context-probe", JsonSerializerContextType = typeof(ResultContextProbeJsonContext))]
public sealed class ResultContextProbeTools
{
    [NhAiTool("lookup", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Look up one subject with a declared serializer context.")]
    public Task<TaskResult<ResultContextProbeOutput>> LookupAsync(
        ResultContextProbeInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(input.Unavailable
            ? TaskResult<ResultContextProbeOutput>.Failed(
                "result-probe-subject-unavailable",
                "The subject is not available.")
            : TaskResult<ResultContextProbeOutput>.Succeeded(new ResultContextProbeOutput(input.Subject, null)));
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(ResultContextProbeInput))]
[JsonSerializable(typeof(ResultContextProbeOutput))]
internal partial class ResultContextProbeJsonContext : JsonSerializerContext
{
}

public sealed record ResultProbeOrder(string Order, string Status);
