// The MCP SDK marks the extension points of its tasks extension experimental (MCPEXP002): the
// alternate-result call filters it adds the task filter to, and the request handlers of tasks/*. This
// file builds on exactly those, as the SDK's own WithTasks does.
#pragma warning disable MCPEXP002

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AspNet.Common;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Configures the MCP tasks of an exposure (<see cref="NhAiMcpExposureBuilder.UseTasks"/>).
/// </summary>
public sealed class NhAiMcpTaskOptions
{
    /// <summary>The smallest <see cref="MaxResultBytes"/>.</summary>
    public const int MinResultBytes = 1_024;

    /// <summary>
    /// How long a task stays after the latest moment its tool can answer, its timeout: the client polls
    /// its result within this time, after which the task does not exist any more. The task's
    /// <c>ttl</c> is this time plus the tool's timeout. Defaults to one hour.
    /// </summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often a client should poll a working task (<c>pollIntervalMs</c>). Defaults to two seconds.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The largest result a task keeps, in UTF-8 bytes of the tool result. A larger result is replaced
    /// by the failure <see cref="NhAiMcpTaskFailureCodes.ResultTooLarge"/>. The tool's own
    /// <see cref="NhAiToolAttribute.MaxResultBytes"/> bounds its data first. Defaults to 256 KiB.
    /// </summary>
    public int MaxResultBytes { get; set; } = 262_144;

    /// <summary>
    /// How long after the tool's timeout a working task counts as abandoned, because the process that ran
    /// the tool ended before it answered. Defaults to one minute.
    /// </summary>
    public TimeSpan AbandonAfter { get; set; } = TimeSpan.FromMinutes(1);

    internal bool InMemory { get; private set; }

    /// <summary>
    /// Keeps the tasks in this process instead of the assistant's store: for tests and development only,
    /// because another instance of the host never finds them and a restart loses them.
    /// </summary>
    public NhAiMcpTaskOptions UseInMemoryStore()
    {
        InMemory = true;
        return this;
    }

    internal void Validate()
    {
        if (TimeToLive <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The time to live of MCP tasks must be positive.");
        }
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The poll interval of MCP tasks must be positive.");
        }
        if (AbandonAfter <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The abandonment margin of MCP tasks must be positive.");
        }
        if (MaxResultBytes < MinResultBytes)
        {
            throw new InvalidOperationException(
                $"The largest result of an MCP task must be at least {MinResultBytes} bytes.");
        }
    }
}

/// <summary>
/// The stable codes of an MCP task that ended without the tool's own result.
/// </summary>
public static class NhAiMcpTaskFailureCodes
{
    /// <summary>
    /// The process that ran the tool ended before the tool answered, and the tool's timeout passed. Call
    /// the tool again; a tool with a request id finds its work again.
    /// </summary>
    public const string Abandoned = "ai-mcp-task-abandoned";

    /// <summary>The tool's result was larger than <see cref="NhAiMcpTaskOptions.MaxResultBytes"/>.</summary>
    public const string ResultTooLarge = "ai-mcp-task-result-too-large";
}

/// <summary>
/// Registers the MCP tasks of an exposure on the MCP SDK's tasks extension
/// (<see cref="McpTasksBuilderExtensions.WithTasks(IMcpServerBuilder, IMcpTaskStore, Action{McpTasksOptions})"/>):
/// a call filter before the SDK's that captures the authorized caller of a tool that declares task
/// support, the SDK's selection of the execution mode from that capture, the NewHeap task store, and a
/// guard of <c>tasks/get</c>, <c>tasks/update</c> and <c>tasks/cancel</c> that authorizes the caller again.
/// </summary>
internal static class NhAiMcpTasks
{
    public static void Register(IMcpServerBuilder builder, NhAiMcpTaskOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var services = builder.Services;
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NhAiMcpTaskOptions)))
        {
            throw new InvalidOperationException("The MCP exposure has tasks already; call UseTasks once.");
        }

        services.AddSingleton(options);
        if (options.InMemory)
        {
            // An explicit choice: it wins over the assistant's store whatever the registration order.
            services.AddSingleton<INhAiMcpTaskRecords, NhAiInMemoryMcpTaskRecords>();
        }

        services.TryAddSingleton<NhAiMcpTaskStore>();
        var forwarder = new NhAiMcpTaskStoreForwarder();
        // Before the SDK's own configuration, so the capture filter runs before the SDK's task filter.
        services.AddSingleton<IConfigureOptions<McpServerOptions>>(provider => new NhAiMcpTaskServerOptions(
            forwarder,
            provider.GetRequiredService<NhAiMcpTaskStore>()));
        builder.WithTasks(forwarder, tasks => tasks.ExecutionModeSelector = NhAiMcpTaskCalls.SelectExecutionMode);
        services.AddSingleton<IPostConfigureOptions<McpServerOptions>, NhAiMcpTaskRequestGuard>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAiMcpTaskStartupCheck>());
    }

    /// <summary>
    /// Whether the request negotiated the tasks extension: a protocol revision with tasks and the
    /// extension among the client's capabilities, as the SDK decides it.
    /// </summary>
    public static bool IsNegotiated(JsonRpcRequest? request)
    {
        if (request is null || !SupportsTasks(request.Context?.ProtocolVersion))
        {
            return false;
        }

        if (request.Context?.ClientCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) is true)
        {
            return true;
        }

        return request.Params?["_meta"]?[MetaKeys.ClientCapabilities]?["extensions"] is JsonObject extensions
            && extensions.ContainsKey(TasksProtocol.ExtensionId);
    }

    /// <summary>
    /// The refusal of a tool that requires tasks for a client without them, as the SDK words it.
    /// </summary>
    public static MissingRequiredClientCapabilityException MissingTasksCapability()
    {
        return new MissingRequiredClientCapabilityException(
            new ClientCapabilities
            {
                Extensions = new Dictionary<string, object>
                {
                    [TasksProtocol.ExtensionId] = new JsonObject()
                }
            },
            $"The request requires the '{TasksProtocol.ExtensionId}' client extension capability.");
    }

    private static bool SupportsTasks(string? protocolVersion)
    {
        // Revisions are dates; tasks exist from the July 2026 revision on.
        return protocolVersion is { Length: 10 }
            && string.CompareOrdinal(protocolVersion, "2026-07-28") >= 0;
    }
}

/// <summary>
/// Who a task belongs to: the actor, tenant and active division of the call that created it. A task of
/// another owner does not exist for the caller.
/// </summary>
internal sealed record NhAiMcpTaskOwner(string ActorId, string? TenantId, string? DivisionId)
{
    public static NhAiMcpTaskOwner Of(HttpContext httpContext, NhAiInvocationContext context)
    {
        return new NhAiMcpTaskOwner(
            context.ActorId,
            string.IsNullOrWhiteSpace(context.TenantId) ? null : context.TenantId,
            httpContext.GetActiveDivisionId()?.ToString("D"));
    }

    public bool Owns(NhAiMcpTaskRecord record)
    {
        return string.Equals(record.ActorId, ActorId, StringComparison.Ordinal)
            && string.Equals(record.TenantId, TenantId, StringComparison.Ordinal)
            && string.Equals(record.DivisionId, DivisionId, StringComparison.Ordinal);
    }
}

/// <summary>
/// A call of a tool that declares task support, captured in the caller's request before the SDK decides
/// whether it becomes a task: the tool, the caller as the request authorized them, and the call's request
/// metadata. The store names the task in it; the tool then runs as that task with the captured caller,
/// also after the request ended.
/// </summary>
internal sealed class NhAiMcpTaskCall(
    NhAiToolDescriptor descriptor,
    string toolName,
    NhAiPersonInvocation person,
    NhAiMcpTaskOwner owner,
    JsonObject? meta)
{
    public NhAiToolDescriptor Descriptor { get; } = descriptor;

    public string ToolName { get; } = toolName;

    public NhAiPersonInvocation Person { get; } = person;

    public NhAiMcpTaskOwner Owner { get; } = owner;

    public JsonObject? Meta { get; } = meta;

    /// <summary>The task the call became, or <see langword="null"/> while it runs as a plain call.</summary>
    public string? TaskId { get; set; }
}

/// <summary>
/// The ambient <see cref="NhAiMcpTaskCall"/> of the current asynchronous flow. The SDK runs a task in a
/// flow that starts inside the capture, so the task's tool call finds it as well.
/// </summary>
internal static class NhAiMcpTaskCalls
{
    private static readonly AsyncLocal<NhAiMcpTaskCall?> CurrentCall = new();

    public static NhAiMcpTaskCall? Current => CurrentCall.Value;

    public static IDisposable Enter(NhAiMcpTaskCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var previous = CurrentCall.Value;
        CurrentCall.Value = call;
        return new Restore(previous);
    }

    /// <summary>
    /// The SDK's execution mode of a call: the declared task support of a captured tool, otherwise a plain
    /// call. A tool that requires tasks refuses a plain call itself.
    /// </summary>
    public static McpTaskExecutionMode SelectExecutionMode(RequestContext<CallToolRequestParams> request)
    {
        if (Current is not { } call
            || call.TaskId is not null
            || !string.Equals(call.ToolName, request.Params?.Name, StringComparison.Ordinal))
        {
            return McpTaskExecutionMode.Synchronous;
        }

        return call.Descriptor.TaskSupport switch
        {
            NhAiToolTaskSupport.Optional => McpTaskExecutionMode.Optional,
            NhAiToolTaskSupport.Required => McpTaskExecutionMode.Required,
            _ => McpTaskExecutionMode.Synchronous
        };
    }

    /// <summary>
    /// Runs the tool of a call that became a task, in the SDK's background flow: with the caller as the
    /// request authorized them, who is checked again for the tool before it runs, and with the task's id
    /// in the invocation context.
    /// </summary>
    public static async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        NhAiMcpTaskCall call,
        CancellationToken cancellationToken)
    {
        var services = request.Services
            ?? throw new InvalidOperationException("The MCP task's service provider is unavailable.");
        var context = call.Person.Context with { McpTaskId = call.TaskId };
        var person = call.Person with { Context = context };
        var tools = await services
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(services, context, cancellationToken);
        var tool = tools.SingleOrDefault(item => string.Equals(
            item.ProtocolTool.Name,
            call.ToolName,
            StringComparison.Ordinal));
        if (tool is null
            || await new NhAiMcpCompositionRequest(services, context).AvailabilityOfAsync(tool, cancellationToken)
                != NhAiMcpCompositionAvailability.Available)
        {
            return NhAiMcpRequestHandlers.Error("ai-tool-not-authorized", "The requested AI tool is not available.");
        }

        // The request gate authorizes the captured person, never a request that ended or another one.
        using (NhAiPersonInvocationScopes.Begin(person))
        using (NhAiMcpInboundCall.Enter(call.Meta))
        {
            return await tool.InvokeAsync(request, cancellationToken);
        }
    }

    private sealed class Restore(NhAiMcpTaskCall? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentCall.Value = previous;
        }
    }
}

/// <summary>
/// The task ids the current request authorized: the store reads or cancels a task only inside the
/// guard of its <c>tasks/*</c> request.
/// </summary>
internal static class NhAiMcpTaskAccess
{
    private static readonly AsyncLocal<string?> AllowedTask = new();

    public static bool Allows(string taskId)
    {
        return AllowedTask.Value is { } allowed && string.Equals(allowed, taskId, StringComparison.Ordinal);
    }

    public static IDisposable Allow(string taskId)
    {
        var previous = AllowedTask.Value;
        AllowedTask.Value = taskId;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AllowedTask.Value = previous;
        }
    }
}

/// <summary>
/// Adds the capture filter in front of the SDK's task filter and binds the store the SDK was given.
/// </summary>
internal sealed class NhAiMcpTaskServerOptions(
    NhAiMcpTaskStoreForwarder forwarder,
    NhAiMcpTaskStore store) : IConfigureOptions<McpServerOptions>
{
    public void Configure(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        forwarder.Bind(store);
        options.Filters.Request.CallToolWithAlternateFilters.Add(CaptureAsync);
    }

    /// <summary>
    /// Captures the authorized caller of a tool that declares task support when the request negotiated
    /// tasks, so the SDK's filter after it can turn the call into a task. Anything else passes unchanged;
    /// the request handler refuses what it refuses for every call.
    /// </summary>
    private static async ValueTask<ResultOrAlternate<CallToolResult>> CaptureAsync(
        RequestContext<CallToolRequestParams> request,
        McpRequestHandler<CallToolRequestParams, ResultOrAlternate<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        var call = NhAiMcpTasks.IsNegotiated(request.JsonRpcRequest)
            ? await TryCaptureAsync(request, cancellationToken)
            : null;
        if (call is null)
        {
            return await next(request, cancellationToken);
        }

        using (NhAiMcpTaskCalls.Enter(call))
        {
            return await next(request, cancellationToken);
        }
    }

    private static async ValueTask<NhAiMcpTaskCall?> TryCaptureAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = request.Services;
        var name = request.Params?.Name;
        var httpContext = services?.GetService<IHttpContextAccessor>()?.HttpContext;
        if (services is null || httpContext is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var resolved = await services
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, cancellationToken);
        if (!resolved.Success)
        {
            return null;
        }

        var tools = await services
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(services, resolved.Data, cancellationToken);
        var tool = tools.SingleOrDefault(item => string.Equals(item.ProtocolTool.Name, name, StringComparison.Ordinal));
        var descriptor = tool?.Metadata.OfType<NhAiToolDescriptor>().FirstOrDefault();
        if (tool is null || descriptor is null || descriptor.TaskSupport == NhAiToolTaskSupport.None)
        {
            return null;
        }

        var availability = await new NhAiMcpCompositionRequest(services, resolved.Data)
            .AvailabilityOfAsync(tool, cancellationToken);
        if (availability != NhAiMcpCompositionAvailability.Available)
        {
            return null;
        }

        return new NhAiMcpTaskCall(
            descriptor,
            name,
            NhAiPersonInvocations.FromRequest(httpContext, resolved.Data),
            NhAiMcpTaskOwner.Of(httpContext, resolved.Data),
            request.Params?.Meta);
    }
}

/// <summary>
/// Authorizes every <c>tasks/get</c>, <c>tasks/update</c> and <c>tasks/cancel</c> before the SDK handles
/// it: the caller must own the task and still be offered its tool. Otherwise the task does not exist for
/// them, with the SDK's own error for an unknown task, so a task of someone else is never confirmed.
/// </summary>
internal sealed class NhAiMcpTaskRequestGuard(
    NhAiMcpTaskStore store,
    ILogger<NhAiMcpTaskRequestGuard> logger) : IPostConfigureOptions<McpServerOptions>
{
    private static readonly HashSet<string> GuardedMethods = new(StringComparer.Ordinal)
    {
        TasksProtocol.MethodTasksGet,
        TasksProtocol.MethodTasksUpdate,
        TasksProtocol.MethodTasksCancel
    };

    public void PostConfigure(string? name, McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestHandlers is not { } handlers)
        {
            return;
        }

        for (var index = 0; index < handlers.Count; index++)
        {
            var handler = handlers[index];
            if (!GuardedMethods.Contains(handler.Method))
            {
                continue;
            }

            var inner = handler.Handler;
            handlers[index] = new McpServerRequestHandler
            {
                Method = handler.Method,
                RoutingNameParameter = handler.RoutingNameParameter,
                Handler = (request, cancellationToken) => GuardAsync(handler.Method, inner, request, cancellationToken)
            };
        }
    }

    private async ValueTask<JsonNode?> GuardAsync(
        string method,
        Func<JsonRpcRequest, CancellationToken, ValueTask<JsonNode?>> inner,
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        // Without tasks the SDK refuses the method itself, before it reads a task.
        if (!NhAiMcpTasks.IsNegotiated(request)
            || request.Params?["taskId"] is not JsonValue taskIdValue
            || !taskIdValue.TryGetValue<string>(out var taskId)
            || string.IsNullOrWhiteSpace(taskId))
        {
            return await inner(request, cancellationToken);
        }

        if (!await store.AuthorizeAsync(taskId, cancellationToken))
        {
            logger.LogInformation("MCP task {TaskId}: {Method} refused, the task does not exist for the caller.", taskId, method);
            throw new McpProtocolException($"Unknown task: '{taskId}'", McpErrorCode.InvalidParams);
        }

        using (NhAiMcpTaskAccess.Allow(taskId))
        {
            return await inner(request, cancellationToken);
        }
    }
}

/// <summary>
/// The store the SDK is given at registration, which forwards to the <see cref="NhAiMcpTaskStore"/> of
/// the host's services once the MCP server is configured.
/// </summary>
internal sealed class NhAiMcpTaskStoreForwarder : IMcpTaskStore
{
    private NhAiMcpTaskStore? _store;

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived
    {
        add => Store.InputResponseReceived += value;
        remove => Store.InputResponseReceived -= value;
    }

    private NhAiMcpTaskStore Store => _store
        ?? throw new InvalidOperationException("The MCP task store is used before the MCP server was configured.");

    public void Bind(NhAiMcpTaskStore store)
    {
        Interlocked.CompareExchange(ref _store, store, null);
    }

    public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        return Store.CreateTaskAsync(cancellationToken);
    }

    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return Store.GetTaskAsync(taskId, cancellationToken);
    }

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    {
        return Store.SetCompletedAsync(taskId, result, cancellationToken);
    }

    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
    {
        return Store.SetFailedAsync(taskId, error, cancellationToken);
    }

    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return Store.SetCancelledAsync(taskId, cancellationToken);
    }

    public Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        return Store.ResolveInputRequestsAsync(taskId, inputResponses, cancellationToken);
    }

    public Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default)
    {
        return Store.SetInputRequestsAsync(taskId, inputRequests, cancellationToken);
    }
}

/// <summary>
/// The NewHeap MCP task store on <see cref="INhAiMcpTaskRecords"/>: a task belongs to the caller that
/// created it, expires on the application's clock, keeps a bounded result and logs its id, tool and status
/// only. Governed tools never ask the client for input while they run, so a task never waits for input.
/// </summary>
internal sealed class NhAiMcpTaskStore : IMcpTaskStore
{
    private const int MaxErrorBytes = 8_192;
    private const int SweepBatch = 200;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly INhAiMcpTaskRecords _records;
    private readonly NhAiMcpTaskOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<NhAiMcpTaskStore> _logger;
    private readonly TimeProvider _clock;
    private long _nextSweepTicks;

    public NhAiMcpTaskStore(
        INhAiMcpTaskRecords records,
        NhAiMcpTaskOptions options,
        IServiceProvider services,
        ILogger<NhAiMcpTaskStore> logger)
    {
        _records = records;
        _options = options;
        _services = services;
        _logger = logger;
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived
    {
        // No task ever waits for input, so no response is ever received.
        add
        {
        }
        remove
        {
        }
    }

    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        var call = NhAiMcpTaskCalls.Current
            ?? throw new InvalidOperationException("An MCP task is only created for a captured call of a tool that declares task support.");
        if (call.TaskId is not null)
        {
            throw new InvalidOperationException("The MCP tool call is a task already.");
        }

        var now = _clock.GetUtcNow();
        var abandonAt = now + call.Descriptor.Timeout + _options.AbandonAfter;
        var record = new NhAiMcpTaskRecord(
            NewTaskId(),
            call.ToolName,
            call.Descriptor.Id,
            call.Descriptor.Version,
            call.Owner.ActorId,
            call.Owner.TenantId,
            call.Owner.DivisionId,
            McpTaskStatus.Working,
            now,
            now,
            abandonAt + _options.TimeToLive,
            abandonAt,
            (long)_options.PollInterval.TotalMilliseconds,
            ResultJson: null,
            ErrorJson: null,
            StatusMessage: null);
        await _records.CreateAsync(record, cancellationToken);
        call.TaskId = record.TaskId;
        _logger.LogInformation(
            "MCP task {TaskId} created for tool {ToolId} version {ToolVersion}.",
            record.TaskId,
            record.ToolId,
            record.ToolVersion);
        await SweepAsync(now, cancellationToken);
        return ToInfo(record);
    }

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!NhAiMcpTaskAccess.Allows(taskId))
        {
            return null;
        }

        var record = await FindLiveAsync(taskId, cancellationToken);
        return record is null ? null : ToInfo(record);
    }

    public async Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    {
        var json = result.GetRawText();
        var size = Encoding.UTF8.GetByteCount(json);
        if (size > _options.MaxResultBytes)
        {
            _logger.LogWarning(
                "MCP task {TaskId}: the result of {ResultBytes} bytes exceeds the limit of {MaxResultBytes} bytes and is replaced by {Code}.",
                taskId,
                size,
                _options.MaxResultBytes,
                NhAiMcpTaskFailureCodes.ResultTooLarge);
            var replacement = NhAiMcpResultMetadata.CreateDatalessFailure(
                NhAiMcpTaskFailureCodes.ResultTooLarge,
                "The tool's result is too large to keep for its task.",
                evidenceReference: null,
                messageSource: null);
            json = JsonSerializer.Serialize(replacement, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>());
        }

        await FinishAsync(taskId, McpTaskStatus.Completed, json, errorJson: null, statusMessage: null, cancellationToken);
    }

    public async Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
    {
        var json = error.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxErrorBytes)
        {
            json = ErrorJson((int)McpErrorCode.InternalError, "The task failed.", reason: null);
        }

        await FinishAsync(taskId, McpTaskStatus.Failed, resultJson: null, json, statusMessage: null, cancellationToken);
    }

    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        // The SDK cancels after the guard authorized the caller, or when the task's own run was cancelled.
        return FinishAsync(taskId, McpTaskStatus.Cancelled, resultJson: null, errorJson: null, statusMessage: null, cancellationToken);
    }

    public Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default)
    {
        // A response that answers no outstanding request is ignored (SEP-2663); there never is one.
        return Task.CompletedTask;
    }

    public Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "A NewHeap MCP task never asks the client for input: a tool cannot elicit or sample while it runs as a task.");
    }

    /// <summary>
    /// Whether the caller of the current request owns the task and is still offered its tool.
    /// </summary>
    public async Task<bool> AuthorizeAsync(string taskId, CancellationToken cancellationToken)
    {
        var httpContext = _services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (httpContext is null)
        {
            return false;
        }

        var services = httpContext.RequestServices;
        var resolved = await services
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, cancellationToken);
        if (!resolved.Success)
        {
            return false;
        }

        var record = await FindLiveAsync(taskId, cancellationToken);
        if (record is null || !NhAiMcpTaskOwner.Of(httpContext, resolved.Data).Owns(record))
        {
            return false;
        }

        // The same authorization as a call of the tool: it is still offered to the caller, as the same tool.
        var tools = await services
            .GetRequiredService<INhAiMcpToolAdapter>()
            .CreateToolsAsync(services, resolved.Data, cancellationToken);
        var tool = tools.SingleOrDefault(item => string.Equals(item.ProtocolTool.Name, record.ToolName, StringComparison.Ordinal));
        var descriptor = tool?.Metadata.OfType<NhAiToolDescriptor>().FirstOrDefault();
        if (tool is null
            || descriptor is null
            || !string.Equals(descriptor.Id, record.ToolId, StringComparison.Ordinal))
        {
            return false;
        }

        return await new NhAiMcpCompositionRequest(services, resolved.Data).AvailabilityOfAsync(tool, cancellationToken)
            == NhAiMcpCompositionAvailability.Available;
    }

    /// <summary>
    /// The task unless it expired; a working task whose tool can no longer answer fails as abandoned.
    /// </summary>
    private async Task<NhAiMcpTaskRecord?> FindLiveAsync(string taskId, CancellationToken cancellationToken)
    {
        var record = await _records.FindAsync(taskId, cancellationToken);
        var now = _clock.GetUtcNow();
        if (record is null || now >= record.ExpiresAt)
        {
            return null;
        }

        if (record.Status == McpTaskStatus.Working && now >= record.AbandonAt)
        {
            var error = ErrorJson(
                (int)McpErrorCode.InternalError,
                "The process that ran the tool ended before the tool answered. Call the tool again.",
                NhAiMcpTaskFailureCodes.Abandoned);
            await FinishAsync(taskId, McpTaskStatus.Failed, resultJson: null, error, NhAiMcpTaskFailureCodes.Abandoned, cancellationToken);
            record = await _records.FindAsync(taskId, cancellationToken);
        }

        return record;
    }

    private async Task<bool> FinishAsync(
        string taskId,
        McpTaskStatus status,
        string? resultJson,
        string? errorJson,
        string? statusMessage,
        CancellationToken cancellationToken)
    {
        // A late cancellation never overwrites a result: only a working task finishes.
        var finished = await _records.TryFinishAsync(
            taskId,
            status,
            resultJson,
            errorJson,
            statusMessage,
            _clock.GetUtcNow(),
            cancellationToken);
        if (finished)
        {
            _logger.LogInformation("MCP task {TaskId} ended {Status}.", taskId, status);
        }

        return finished;
    }

    private async Task SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < next
            || Interlocked.CompareExchange(ref _nextSweepTicks, (now + SweepInterval).UtcTicks, next) != next)
        {
            return;
        }

        try
        {
            var deleted = await _records.DeleteExpiredAsync(now, SweepBatch, cancellationToken);
            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} expired MCP tasks.", deleted);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Expired tasks are invisible already; the next sweep tries again.
            _logger.LogWarning("Deleting expired MCP tasks failed with {ExceptionType}.", exception.GetType().Name);
        }
    }

    private static McpTaskInfo ToInfo(NhAiMcpTaskRecord record)
    {
        return new McpTaskInfo(
            record.TaskId,
            record.Status,
            record.CreatedAt,
            record.LastUpdatedAt,
            record.ExpiresAt - record.CreatedAt,
            record.PollIntervalMs,
            record.StatusMessage,
            Parse(record.ResultJson),
            Parse(record.ErrorJson));
    }

    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ErrorJson(int code, string message, string? reason)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = reason is null ? message : $"{reason}: {message}"
        };
        if (reason is not null)
        {
            error["data"] = new JsonObject { ["code"] = reason };
        }

        return error.ToJsonString();
    }

    private static string NewTaskId()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }
}

/// <summary>
/// Fails the host's start when tasks are enabled without a store for them.
/// </summary>
internal sealed class NhAiMcpTaskStartupCheck(IServiceProvider services) : NhAiStartupCheck(services)
{
    private readonly IServiceProvider _services = services;

    protected override Task ValidateAsync(CancellationToken cancellationToken)
    {
        if (_services.GetService<INhAiMcpTaskRecords>() is null)
        {
            throw new InvalidOperationException(
                "UseTasks needs a store for MCP tasks: register the assistant, whose store every instance of the host shares, or call UseInMemoryStore() for tests and development.");
        }

        return Task.CompletedTask;
    }
}
