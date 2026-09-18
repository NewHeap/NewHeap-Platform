using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AspNet.Common.DAL;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Composes the assistant sample: a streaming chat profile, one agent over the curated
/// <c>projects.*</c> tools and the <c>sample-api.*</c> bridge tools, PostgreSQL storage in the
/// library-owned <c>nhai</c> schema and a business audit sink that keeps content-free events in
/// <see cref="SampleAssistantAuditLog"/>. Call it after <c>AddSampleProjectManagementAi</c>,
/// <c>AddNewHeapPlatformAIAspNet</c> and <c>AddSampleAiBridge</c>. Discovery decides per user
/// which of those tools the agent is offered.
/// </summary>
public static class SampleAssistantComposition
{
    public const string AgentId = "sample-project-assistant";
    public const string ProfileName = "project-assistant-chat";
    public const string ModelKey = "project-assistant-chat-model";
    public const string AccessPolicy = "app.active-division.project.view";

    /// <summary>
    /// Administrators (manage permission or the administrator role) manage agents, MCP servers and
    /// the application context.
    /// </summary>
    public const string AdminPolicy = "app.project.edit-or-admin";

    /// <summary>
    /// Seeded once as version 1 of the application context; administrators edit it afterwards.
    /// </summary>
    public static NhAiTextAsset DefaultApplicationContext { get; } = NhAiTextAssetFactory.Create(
        "sample-application-context",
        1,
        """
        Sample Project Management tracks projects per division. A project has a key, a name, a
        description and a status: Draft, Active, OnHold, Completed or Archived. Users only see and
        change projects of their active division. Status changes are governed actions that the user
        approves in the chat.
        """,
        "embedded:SampleProjectManagement.Api/SampleAssistantComposition",
        NhAiAssetRole.SystemInstructions,
        NhAiContextTrust.TrustedApplication,
        NhAiModelCapability.Chat,
        [],
        "default",
        NhAiDataClassification.Internal,
        NhAiRetentionCategory.Operational,
        "sample-application-context-v1");

    public static IServiceCollection AddSampleAssistant(this IServiceCollection services)
    {
        // The sample has no model provider. A deterministic local model drives the agent;
        // hosts and tests register their own keyed client before this call to replace it.
        services.TryAddKeyedSingleton<IChatClient, SampleAssistantChatClient>(ModelKey);
        services.TryAddSingleton<SampleAssistantAuditLog>();
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(ProfileName, profile => profile
                .UseKeyedClient(ModelKey)
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local")
                .WithBudget(
                    maxInputTokens: 8_192,
                    maxOutputTokens: 1_024,
                    maxCalls: 16)
                .WithTimeout(TimeSpan.FromSeconds(30))));

        services.AddNewHeapAssistant(assistant => assistant
            .UsePostgreSql(
                provider => provider.GetRequiredService<IConfiguration>().GetDatabaseConnectionString(),
                options =>
                {
                    options.Schema = "nhai";
                    options.RunMigrations = true;
                })
            .UseAccessPolicy(AccessPolicy)
            .UseAdminPolicy(AdminPolicy)
            .UseDefaultApplicationContext(DefaultApplicationContext)
            .ConfigureMcp(mcp =>
            {
                // Administrators may connect any https host that is not blocked; plain http is only
                // accepted for loopback in Development. No host receives forwarded user tokens.
                mcp.RequireHttps = true;
                mcp.ToolListCacheDuration = TimeSpan.FromMinutes(5);
                mcp.ConnectTimeout = TimeSpan.FromSeconds(10);
            })
            .UseChatProfile(ProfileName)
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-assistant.name",
                DescriptionKey: "nh-assistant.agents.sample-project-assistant.description",
                ProfileName: ProfileName,
                Instructions: ProjectAiAssets.ProjectAgentInstructions,
                ToolSelectors: ["projects.*", SampleAiBridgeComposition.ToolSetId + ".*"],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: AccessPolicy))
            .AddBusinessAuditSink<SampleAssistantAuditSink>()
            .WithLimits(limits =>
            {
                limits.MaxToolCallsPerTurn = 4;
                limits.MaxMessageChars = 4_000;
                limits.TurnTimeout = TimeSpan.FromSeconds(60);
                limits.DailyToolCallBudgetPerActor = 100;
            }));
        return services;
    }

    public static IEndpointRouteBuilder MapSampleAssistant(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapNewHeapAssistant("/api/assistant");
        return endpoints;
    }
}

/// <summary>
/// Bounded in-memory log of content-free assistant audit events for the sample.
/// </summary>
public sealed class SampleAssistantAuditLog
{
    private readonly ConcurrentQueue<NhAssistantAuditEvent> _events = new();

    public IReadOnlyCollection<NhAssistantAuditEvent> Events => _events.ToArray();

    public void Add(NhAssistantAuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _events.Enqueue(evt);
        while (_events.Count > 200)
        {
            _events.TryDequeue(out _);
        }
    }
}

/// <summary>
/// Business audit sink of the sample. It receives identifiers, codes and timestamps only.
/// </summary>
public sealed class SampleAssistantAuditSink(SampleAssistantAuditLog log) : INhAssistantBusinessAuditSink
{
    public ValueTask RecordAsync(NhAssistantAuditEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log.Add(evt);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Deterministic local model for the runnable sample. A message that names a project id and a
/// status proposes <c>projects.change-status</c>; any other message searches projects; a tool
/// result is summarized. It demonstrates the governed flow, not language understanding.
/// </summary>
public sealed partial class SampleAssistantChatClient : IChatClient
{
    private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ChatResponse(Respond(messages.ToArray()))
        {
            ModelId = "sample-assistant-model"
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var message = Respond(messages.ToArray());
        foreach (var content in message.Contents)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
            {
                ModelId = "sample-assistant-model"
            };
        }

        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [new UsageContent(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 })])
        {
            FinishReason = message.Contents.OfType<FunctionCallContent>().Any()
                ? ChatFinishReason.ToolCalls
                : ChatFinishReason.Stop
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private static ChatMessage Respond(IReadOnlyList<ChatMessage> messages)
    {
        var last = messages.LastOrDefault();
        var result = last?.Contents.OfType<FunctionResultContent>().LastOrDefault();
        if (result is not null)
        {
            var succeeded = result.Result is JsonElement { ValueKind: JsonValueKind.Object } json
                && json.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True;
            return new ChatMessage(
                ChatRole.Assistant,
                succeeded
                    ? "The governed project request completed."
                    : "The project request was not completed.");
        }

        var text = last?.Text ?? string.Empty;
        var projectId = ProjectIdPattern().Match(text);
        var status = StatusPattern().Match(text);
        if (projectId.Success && status.Success)
        {
            return Call("projects_change_status_v1", new
            {
                input = new { projectId = Guid.Parse(projectId.Value), status = StatusValue(status.Value) }
            });
        }

        return Call("projects_search_v1", new
        {
            input = new { query = text.Length > 100 ? text[..100] : text, limit = 5 }
        });
    }

    private static ChatMessage Call(string name, object arguments)
    {
        var element = JsonSerializer.SerializeToElement(arguments, ArgumentOptions);
        var dictionary = element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone());
        return new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("sample-call-" + Guid.NewGuid().ToString("N"), name, dictionary)]);
    }

    private static int StatusValue(string value)
    {
        return Enum.TryParse<SampleProjectManagement.DAL.Entities.ProjectStatus>(value, ignoreCase: true, out var status)
            ? (int)status
            : 0;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex ProjectIdPattern();

    [GeneratedRegex("\\b(draft|active|onhold|completed|archived)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatusPattern();
}
