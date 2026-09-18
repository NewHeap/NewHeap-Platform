using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// A real assistant registration over a real database, driven without HTTP. The signed-in user is
/// provided through <see cref="IHttpContextAccessor"/> exactly as an endpoint would.
/// </summary>
internal sealed class AssistantTestHost : IAsyncDisposable
{
    public const string UserId = "user-1";
    public const string AccessPolicy = "app.assistant.access";

    private readonly ServiceProvider _provider;

    private AssistantTestHost(
        ServiceProvider provider,
        NhAiCapturedAuditSink audit,
        NhAiCapturedUsageSink usage,
        CapturedBusinessAuditSink business,
        CapturingLoggerProvider logs)
    {
        _provider = provider;
        Audit = audit;
        Usage = usage;
        Business = business;
        Logs = logs;
    }

    public IServiceProvider Services => _provider;

    public NhAiCapturedAuditSink Audit { get; }

    public NhAiCapturedUsageSink Usage { get; }

    public CapturedBusinessAuditSink Business { get; }

    public CapturingLoggerProvider Logs { get; }

    public TestProjectToolRecorder Tools => _provider.GetRequiredService<TestProjectToolRecorder>();

    public static async Task<AssistantTestHost> CreateAsync(
        AssistantDatabaseFixture database,
        AssistantTestProvider provider,
        IChatClient model,
        Action<NhAssistantLimits>? limits = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        var audit = new NhAiCapturedAuditSink();
        var usage = new NhAiCapturedUsageSink();
        var business = new CapturedBusinessAuditSink();
        var logs = new CapturingLoggerProvider();
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NewHeap:AI:Assistant:Enabled"] = "true"
            })
            .Build());
        services.AddAuthorization(options =>
        {
            options.AddPolicy(TestProjectToolCatalog.ReadPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(TestProjectToolCatalog.ManagePolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("app.project.manage", policy => policy.RequireClaim("permission", "app.project.manage"));
        });
        services.AddSingleton<TestProjectToolRecorder>();
        services.AddKeyedSingleton("project-chat-model", model);
        services.AddSingleton<INhAiAuditSink>(audit);
        services.AddSingleton<INhAiUsageSink>(usage);
        services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local")
                .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
            ai.AddGeneratedToolCatalog<TestProjectToolCatalog>();
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });
        services.AddScoped<INhAiToolDiscoveryPolicy>(_ => NhAiTestDiscoveryPolicy.Allowed());
        services.AddNewHeapPlatformAIAspNet(ai => ai.UseToolInvocationPurpose("project-assistance"));
        var schema = AssistantDatabaseFixture.UniqueSchema();
        var connectionString = database.ConnectionString(provider);
        services.AddNewHeapAssistant(assistant =>
        {
            if (provider == AssistantTestProvider.SqlServer)
            {
                assistant.UseSqlServer(connectionString, options => options.Schema = schema);
            }
            else
            {
                assistant.UsePostgreSql(connectionString, options => options.Schema = schema);
            }
            assistant
                .UseAccessPolicy(AccessPolicy)
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" })
                .AddBusinessAuditSink<CapturedBusinessAuditSinkAdapter>()
                .WithLimits(configured => limits?.Invoke(configured));
        });
        services.AddSingleton(business);
        configure?.Invoke(services);

        var built = services.BuildServiceProvider();
        await using (var context = built.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.Database.MigrateAsync();
        }
        return new AssistantTestHost(built, audit, usage, business, logs);
    }

    public async Task<AssistantConversation> CreateConversationAsync(string owner = UserId)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = owner,
            AgentId = AssistantTestData.Agent().Id,
            AgentVersion = 1,
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await _provider.GetRequiredService<NhAssistantDbContextFactory>()
            .CreateDbContext()
            .UsingAsync(async context =>
            {
                context.Conversations.Add(conversation);
                await context.SaveChangesAsync();
            });
        return conversation;
    }

    public Task<TurnResult> SendAsync(Guid conversationId, string text, string? userId = null)
    {
        return RunAsync(userId, (runner, context) => runner.StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(conversationId, context, text, Guid.NewGuid().ToString("N"), CancellationToken.None),
            CancellationToken.None));
    }

    public Task<TurnResult> DecideAsync(
        Guid conversationId,
        NhAssistantApprovalView approval,
        bool approve,
        string? expectedHash = null,
        string? userId = null)
    {
        return RunAsync(userId, (runner, context) => runner.StartDecisionTurnAsync(
            new NhAssistantDecisionTurnRequest(
                conversationId,
                context,
                approval.ApprovalId,
                approve,
                expectedHash ?? approval.ProposalHash,
                approve ? null : "Not now.",
                CancellationToken.None),
            CancellationToken.None));
    }

    public async Task<TurnResult> StartWithoutWaitingAsync(
        Guid conversationId,
        string text,
        Func<INhAssistantTurnRunner, Task> whileRunning)
    {
        await using var scope = _provider.CreateAsyncScope();
        var httpContext = EnterUser(scope.ServiceProvider, UserId);
        var context = await ResolveAsync(scope.ServiceProvider, httpContext);
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        var started = await runner.StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(conversationId, context, text, null, CancellationToken.None),
            CancellationToken.None);
        if (!started.Success)
        {
            return new TurnResult(started, []);
        }
        await whileRunning(scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>());
        var events = await ReadAllAsync(started.Data);
        return new TurnResult(started, events);
    }

    public async Task<AssistantConversation> ReloadAsync(Guid conversationId, string owner = UserId)
    {
        var store = _provider.GetRequiredService<INhAssistantStore>();
        return (await store.FindConversationAsync(conversationId, owner, CancellationToken.None))!;
    }

    public async Task<NhAssistantConversationView> ReadViewAsync(Guid conversationId, string owner = UserId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<NhAssistantConversationReader>();
        return (await reader.GetAsync(conversationId, owner, CancellationToken.None))!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    private async Task<TurnResult> RunAsync(
        string? userId,
        Func<INhAssistantTurnRunner, NhAiInvocationContext, Task<TaskResult<NhAssistantTurnHandle>>> start)
    {
        await using var scope = _provider.CreateAsyncScope();
        var httpContext = EnterUser(scope.ServiceProvider, userId ?? UserId);
        var context = await ResolveAsync(scope.ServiceProvider, httpContext);
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        var started = await start(runner, context);
        if (!started.Success)
        {
            return new TurnResult(started, []);
        }
        var events = await ReadAllAsync(started.Data);
        return new TurnResult(started, events);
    }

    private static async Task<IReadOnlyList<NhAssistantTurnEvent>> ReadAllAsync(NhAssistantTurnHandle handle)
    {
        var events = new List<NhAssistantTurnEvent>();
        await foreach (var evt in handle.Events.ReadAllAsync())
        {
            events.Add(evt);
        }
        await handle.Completion;
        return events;
    }

    /// <summary>
    /// Sets the request user synchronously: the accessor is AsyncLocal-based, so it must be set in the
    /// calling flow rather than inside an awaited helper.
    /// </summary>
    private static HttpContext EnterUser(IServiceProvider services, string userId)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "test"))
        };
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        return httpContext;
    }

    private static async Task<NhAiInvocationContext> ResolveAsync(IServiceProvider services, HttpContext httpContext)
    {
        var resolved = await services
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext);
        if (!resolved.Success)
        {
            throw new InvalidOperationException("The test user could not be resolved.");
        }
        return resolved.Data;
    }
}

internal sealed record TurnResult(
    TaskResult<NhAssistantTurnHandle> Start,
    IReadOnlyList<NhAssistantTurnEvent> Events)
{
    public string? StartCode => Start.Success
        ? null
        : Start.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));

    public T Single<T>()
        where T : NhAssistantTurnEvent
    {
        return Events.OfType<T>().Single();
    }

    public string Text => string.Concat(Events.OfType<NhAssistantMessageDeltaEvent>().Select(delta => delta.Text));
}

internal static class DbContextUsingExtensions
{
    public static async Task UsingAsync(this NhAssistantDbContext context, Func<NhAssistantDbContext, Task> action)
    {
        await using (context)
        {
            await action(context);
        }
    }
}

public sealed class CapturedBusinessAuditSink
{
    public ConcurrentQueue<NhAssistantAuditEvent> Events { get; } = new();
}

public sealed class CapturedBusinessAuditSinkAdapter(CapturedBusinessAuditSink sink) : INhAssistantBusinessAuditSink
{
    public ValueTask RecordAsync(NhAssistantAuditEvent evt, CancellationToken ct)
    {
        sink.Events.Enqueue(evt);
        return ValueTask.CompletedTask;
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(this);
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
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
            provider.Messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }
}
