using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// Governed functions accept arguments without the <c>input</c> envelope, reject an envelope
/// mixed with other properties as <c>ai-tool-input-invalid</c> without executing, and never put
/// argument values into messages, audit records or logs.
/// </summary>
public sealed class NhAiToolArgumentEnvelopeTests
{
    private const string ExportName = "envelope-probe.search";

    [Fact]
    public async Task Generated_tool_accepts_flat_arguments_once_with_the_same_result_as_the_envelope()
    {
        await using var probe = EnvelopeProbe.Create();

        var enveloped = await probe.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(new { query = "roadmap", page = 2 })
        });
        var flat = await probe.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = JsonSerializer.SerializeToElement("roadmap"),
            ["page"] = JsonSerializer.SerializeToElement(2)
        });

        Assert.Equal(enveloped.GetRawText(), flat.GetRawText());
        Assert.True(flat.GetProperty("success").GetBoolean());
        Assert.Equal("roadmap:2", flat.GetProperty("data").GetString());
        Assert.Equal(2, probe.Tool.Calls);
        Assert.Equal(2, probe.Audit.Records.Count(record => record.Outcome == NhAiOutcomeKind.Succeeded));
    }

    [Fact]
    public async Task Envelope_mixed_with_other_properties_is_input_invalid_and_does_not_execute()
    {
        await using var probe = EnvelopeProbe.Create();

        var result = await probe.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(new { query = "secret-value", page = 1 }),
            ["page"] = JsonSerializer.SerializeToElement(9),
            ["itemsPerPage"] = JsonSerializer.SerializeToElement(1)
        });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, result.GetProperty("code").GetString());
        var message = result.GetProperty("message").GetString()!;
        Assert.Contains("{\"input\": {...}}", message, StringComparison.Ordinal);
        Assert.Contains("itemsPerPage, page", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("9", message, StringComparison.Ordinal);
        Assert.Equal(0, probe.Tool.Calls);
        var audit = Assert.Single(probe.Audit.Records);
        Assert.Equal(NhAiOutcomeKind.TerminalFailure, audit.Outcome);
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, audit.ResultCode);
    }

    [Fact]
    public async Task Flat_input_that_does_not_match_the_tool_input_is_still_rejected()
    {
        await using var probe = EnvelopeProbe.Create();

        var result = await probe.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = JsonSerializer.SerializeToElement("roadmap"),
            ["page"] = JsonSerializer.SerializeToElement("not-a-number")
        });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, result.GetProperty("code").GetString());
        Assert.DoesNotContain("not-a-number", result.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, probe.Tool.Calls);
    }

    [Fact]
    public async Task Mcp_export_accepts_flat_arguments_and_publishes_input_invalid_as_a_structured_error()
    {
        await using var probe = EnvelopeProbe.Create();
        var tools = await probe.Scope.ServiceProvider
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(probe.Scope.ServiceProvider, EnvelopeProbe.Context);
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

        var flat = await tool.CallAsync(new Dictionary<string, object?> { ["query"] = "roadmap", ["page"] = 3 });
        var mixed = await tool.CallAsync(new Dictionary<string, object?>
        {
            ["input"] = new { query = "roadmap", page = 3 },
            ["page"] = 4
        });

        Assert.NotEqual(true, flat.IsError);
        Assert.Equal("roadmap:3", flat.StructuredContent!.Value.GetProperty("data").GetString());
        Assert.True(mixed.IsError);
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, mixed.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
        Assert.Equal(1, probe.Tool.Calls);
    }

    [Fact]
    public async Task Imported_mcp_tool_takes_flat_arguments_natively_and_executes_once()
    {
        var calls = 0;
        var remote = McpServerTool.Create(
            (string query) =>
            {
                Interlocked.Increment(ref calls);
                return "remote:" + query;
            },
            new McpServerToolCreateOptions { Name = "lookup", Description = "Looks up projects." });
        var services = new ServiceCollection();
        services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(EnvelopeProbe.Context));
        services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAIMcp();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ScopeRequests = false, ToolCollection = [remote] },
            serviceProvider: scope.ServiceProvider);
        _ = server.RunAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        var catalog = scope.ServiceProvider.GetRequiredService<INhAiMcpClientToolImporter>().Import(
            await client.ListToolsAsync(),
            new NhAiMcpImportOptions(
                "provider",
                "projects",
                [
                    new NhAiMcpImportedToolPolicy(
                        "lookup",
                        "lookup",
                        "Looks up projects through the provider.",
                        NhAiToolEffect.ReadOnly,
                        NhAiToolExposure.Agent,
                        ["projects.lookup"])
                    {
                        Approval = NhAiApprovalRequirement.NotRequired
                    }
                ]));
        var function = Assert.Single(catalog.CreateFunctions(scope.ServiceProvider));

        var result = Assert.IsType<TaskResult<CallToolResult>>(
            await function.InvokeAsync(new AIFunctionArguments { ["query"] = "roadmap" }));

        // Imported tools publish the remote schema, which has no input envelope: flat is their native shape.
        Assert.False(function.JsonSchema.GetProperty("properties").TryGetProperty("input", out _));
        Assert.True(result.Success);
        Assert.Contains("remote:roadmap", Assert.IsType<TextContentBlock>(Assert.Single(result.Data!.Content)).Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unexpected_exception_inside_the_invoker_is_logged_without_content()
    {
        await using var probe = EnvelopeProbe.Create();
        probe.Tool.Exception = new InvalidOperationException("secret-exception-message");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await probe.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(new { query = "secret-argument", page = 1 })
        }));

        var entry = Assert.Single(probe.Logs.Entries, entry => entry.Contains("failed unexpectedly", StringComparison.Ordinal));
        Assert.Contains("envelope-probe.search", entry, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", entry, StringComparison.Ordinal);
        Assert.DoesNotContain(probe.Logs.Entries, item => item.Contains("secret-", StringComparison.Ordinal));
    }

    private sealed class EnvelopeProbe : IAsyncDisposable
    {
        public static readonly NhAiInvocationContext Context = new(
            "actor-1",
            "tool-invocation",
            new Dictionary<string, string>());

        private readonly ServiceProvider _provider;

        private EnvelopeProbe(ServiceProvider provider)
        {
            _provider = provider;
            Scope = provider.CreateAsyncScope();
        }

        public AsyncServiceScope Scope { get; }

        public EnvelopeProbeTools Tool => _provider.GetRequiredService<EnvelopeProbeTools>();

        public NhAiCapturedAuditSink Audit => _provider.GetRequiredService<NhAiCapturedAuditSink>();

        public ListLoggerProvider Logs => _provider.GetRequiredService<ListLoggerProvider>();

        public static EnvelopeProbe Create()
        {
            var logs = new ListLoggerProvider();
            var services = new ServiceCollection();
            services.AddSingleton(logs);
            services.AddLogging(logging => logging.AddProvider(logs));
            services.AddSingleton<EnvelopeProbeTools>();
            services.AddSingleton<NhAiCapturedAuditSink>();
            services.AddSingleton<INhAiAuditSink>(provider => provider.GetRequiredService<NhAiCapturedAuditSink>());
            services.AddScoped<INhAiToolInvocationGate>(_ => NhAiTestInvocationGate.Authorized(Context));
            services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
            services.AddScoped<INhAiBudgetManager>(_ => new NhAiTestBudgetManager());
            services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<EnvelopeProbeToolsNhAiCatalog>());
            services.AddNewHeapPlatformAIMcp();
            return new EnvelopeProbe(services.BuildServiceProvider());
        }

        public async Task<JsonElement> InvokeAsync(AIFunctionArguments arguments)
        {
            var function = Assert.Single(
                Scope.ServiceProvider.GetServices<INhAiToolCatalog>()
                    .Single(catalog => catalog is EnvelopeProbeToolsNhAiCatalog)
                    .CreateFunctions(Scope.ServiceProvider));
            Assert.Equal(ExportName, function.Name);
            var output = await function.InvokeAsync(arguments);
            return output is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(output, function.JsonSerializerOptions);
        }

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    public sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new ListLogger(this);
        }

        public void Dispose()
        {
        }

        private sealed class ListLogger(ListLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (provider._entries)
                {
                    provider._entries.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
                }
            }
        }
    }
}

public sealed record EnvelopeProbeInput(string Query, int Page);

[NhAiToolSet("envelope-probe")]
public sealed class EnvelopeProbeTools
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Exception? Exception { get; set; }

    [NhAiTool("search", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local | NhAiToolExposure.Mcp)]
    [NhAiToolExportName("envelope-probe.search")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "envelope-probe")]
    [Description("Search with a paged query.")]
    public Task<TaskResult<string>> SearchAsync(
        EnvelopeProbeInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (Exception is not null)
        {
            throw Exception;
        }
        return Task.FromResult(TaskResult<string>.Succeeded(input.Query + ":" + input.Page));
    }
}
