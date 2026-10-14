using System.Collections.Concurrent;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Localization;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;

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

    public static IServiceCollection AddSampleAssistant(
        this IServiceCollection services,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        // The sample has no model provider. A deterministic local model drives the agent;
        // hosts and tests register their own keyed client before this call to replace it.
        services.TryAddKeyedSingleton<IChatClient, SampleAssistantChatClient>(ModelKey);
        services.TryAddSingleton<SampleAssistantAuditLog>();
        services.AddLocalization();
        // The NewHeap localizer searches only registered assemblies; the texts of the sample's
        // presenters live next to their composition in this assembly.
        services.Configure<NhLocalizationOptions>(options =>
            options.AssemblyNameLocalizationOptions.Add(new NhLocalizationOptions.Entry
            {
                AssemblyName = typeof(SampleAssistantToolPresenter).Assembly.GetName(),
                Options = new LocalizationOptions(),
                Order = 0
            }));
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
            // Every turn starts with the date and time in Amsterdam and who the user is, so the model
            // does not need a tool call to learn them.
            .UseTimeZone("Europe/Amsterdam")
            .UseTurnContextProvider<SampleAssistantTurnContextProvider>()
            .AddToolPresenter<SampleAssistantToolPresenter>()
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-assistant.name",
                DescriptionKey: "nh-assistant.agents.sample-project-assistant.description",
                ProfileName: ProfileName,
                Instructions: ProjectAiAssets.ProjectAgentInstructions,
                // SPM-278: the generated consultation tools of the intake reviewer, selected like any
                // other tool. The reviewer has to allow consultation as well.
                ToolSelectors:
                [
                    "projects.*",
                    SampleAiBridgeComposition.ToolSetId + ".*",
                    NhAssistantConsultationTools.ConsultToolId(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type),
                    NhAssistantConsultationTools.RequestToolId(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type)
                ],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: AccessPolicy))
            .AddBusinessAuditSink<SampleAssistantAuditSink>()
            // Owners share conversations through an invitation link or invite colleagues of
            // their division directly; colleagues continue with their own permissions.
            .UseParticipantDirectory<SampleAssistantParticipantDirectory>()
            // Desktop notifications when a longer turn ends or an approval waits while the user is away.
            .ConfigurePush(push => SampleAssistantPush.Configure(push, environment))
            // A reviewed research MCP tool receives who acts for whom and a bounded snapshot of the
            // conversation as request metadata; other MCP servers never do.
            .AddSampleResearchContext(configuration)
            .WithLimits(limits =>
            {
                limits.MaxToolCallsPerTurn = 4;
                limits.MaxMessageChars = 4_000;
                limits.TurnTimeout = TimeSpan.FromSeconds(60);
                limits.DailyToolCallBudgetPerActor = 100;
                // Agents that list many exact settings actions need more selectors than the
                // default 128. Prefer prefix selectors; this does not raise MaxToolsPerAgent.
                limits.MaxToolSelectorsPerAgent = 256;
                limits.MaxParticipantsPerConversation = 10;
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
/// Localizes assistant tool names and explains a governed project status proposal with data read
/// inside the same authorized division. The technical descriptor remains model-facing metadata.
/// </summary>
public sealed class SampleAssistantToolPresenter(
    IProjectAiReadService projects,
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantToolPresenter
{
    public ValueTask<string?> GetDisplayNameAsync(
        NhAssistantToolDisplayNameContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = context.Descriptor.Id switch
        {
            "projects.search" => localizer["ToolProjectsSearch"].Value,
            "projects.change-status" => localizer["ToolProjectStatusChange"].Value,
            ProjectTaskAiTools.CompleteToolId => localizer["ToolProjectTaskComplete"].Value,
            _ => null
        };
        return ValueTask.FromResult(name);
    }

    public async ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
        NhAssistantApprovalPresentationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InvocationContext.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return null;
        }
        if (context.Arguments is ProjectTaskCompleteInput taskInput
            && string.Equals(context.Descriptor.Id, ProjectTaskAiTools.CompleteToolId, StringComparison.Ordinal))
        {
            return await PresentTaskCompletionAsync(divisionId, taskInput, cancellationToken);
        }
        if (!string.Equals(context.Descriptor.Id, "projects.change-status", StringComparison.Ordinal)
            || context.Arguments is not ProjectAiStatusChangeInput input)
        {
            return null;
        }

        var project = await projects.GetForAiApprovalAsync(
            divisionId,
            input.ProjectId,
            cancellationToken);
        if (project is null)
        {
            return null;
        }

        var currentStatus = localizer[$"ProjectStatus{project.Status}"].Value;
        var newStatus = localizer[$"ProjectStatus{input.Status}"].Value;
        return new NhAssistantApprovalPresentation(
            localizer["ToolProjectStatusChange"].Value,
            localizer["ApprovalProjectStatusSummary", project.Key, project.Name, newStatus].Value,
            [
                new NhAssistantPresentationField(
                    localizer["ApprovalFieldProject"].Value,
                    localizer["ApprovalProjectValue", project.Key, project.Name].Value),
                new NhAssistantPresentationField(
                    localizer["ApprovalFieldCurrentStatus"].Value,
                    currentStatus),
                new NhAssistantPresentationField(
                    localizer["ApprovalFieldNewStatus"].Value,
                    newStatus)
            ]);
    }

    /// <summary>
    /// SPM-282: names the task and its project, so approvers recognize a task they already ticked off.
    /// </summary>
    private async Task<NhAssistantApprovalPresentation?> PresentTaskCompletionAsync(
        Guid divisionId,
        ProjectTaskCompleteInput input,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(item => item.Id == input.TaskId && item.Project.DivisionId == divisionId)
            .Select(item => new { item.Title, item.Project.Key, item.Project.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (task is null)
        {
            return null;
        }

        return new NhAssistantApprovalPresentation(
            localizer["ToolProjectTaskComplete"].Value,
            localizer["ApprovalProjectTaskSummary", task.Title, task.Key].Value,
            [
                new NhAssistantPresentationField(
                    localizer["ApprovalFieldProject"].Value,
                    localizer["ApprovalProjectValue", task.Key, task.Name].Value),
                new NhAssistantPresentationField(localizer["ApprovalFieldTask"].Value, task.Title)
            ]);
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
/// <summary>
/// Situational facts for the sample assistant: the signed-in user's display name and roles, read
/// from the authenticated principal only. The assistant marks them as data, not instructions.
/// </summary>
public sealed class SampleAssistantTurnContextProvider(IHttpContextAccessor httpContextAccessor) : INhAssistantTurnContextProvider
{
    public ValueTask<IReadOnlyList<NhAssistantContextFact>> GetFactsAsync(
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = httpContextAccessor.HttpContext?.User;
        var facts = new List<NhAssistantContextFact>
        {
            new("User", user?.FindFirst(ClaimTypes.Name)?.Value ?? context.ActorId)
        };
        var roles = user?.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (roles.Length > 0)
        {
            facts.Add(new NhAssistantContextFact("Roles", string.Join(", ", roles)));
        }
        return ValueTask.FromResult<IReadOnlyList<NhAssistantContextFact>>(facts);
    }
}

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
/// status proposes <c>projects.change-status</c>, which is also how the intake reviewer answers
/// its event message; a message that names a project id and "the intake reviewer" consults it, or
/// with "again" requests a new review (SPM-278), and the consulted reviewer answers from its
/// working memory; any other message searches projects; a tool result is summarized. It
/// demonstrates the governed flow, not language understanding.
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
        if (result is null && IsConsultation(messages))
        {
            // SPM-278: the consulted intake reviewer answers from its working memory.
            return new ChatMessage(ChatRole.Assistant, AnswerFromMemory(messages));
        }

        if (result?.Result is JsonElement { ValueKind: JsonValueKind.Object } consulted
            && consulted.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String)
            {
                return new ChatMessage(ChatRole.Assistant, "The intake reviewer says: " + answer.GetString());
            }

            if (data.TryGetProperty("runId", out _))
            {
                return new ChatMessage(
                    ChatRole.Assistant,
                    "The intake reviewer will look at the project again and answer in the project's thread.");
            }
        }

        if (result is not null)
        {
            // A deferred proposal returns a "recorded" result: nothing happened yet.
            if (result.Result is JsonElement { ValueKind: JsonValueKind.Object } recorded
                && recorded.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == "assistant-proposal-recorded")
            {
                return new ChatMessage(
                    ChatRole.Assistant,
                    "I proposed the change; it waits for a project approver.");
            }

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
        var projectId = FindProjectId(text);
        if (projectId is not null && text.Contains("intake reviewer", StringComparison.OrdinalIgnoreCase))
        {
            // SPM-278: ask the intake reviewer through its generated consultation tools.
            var subjectId = Guid.Parse(projectId).ToString("D");
            return text.Contains("again", StringComparison.OrdinalIgnoreCase)
                ? Call("request_sample-project-intake-reviewer_project_v1", new { input = new { subjectId, message = text } })
                : Call("consult_sample-project-intake-reviewer_project_v1", new { input = new { subjectId, question = text } });
        }

        var task = TaskPattern().Match(text);
        if (task.Success)
        {
            // SPM-282: the intake reviewer proposes to tick off a task of the project.
            return Call("project_tasks_complete_v1", new
            {
                input = new { taskId = Guid.Parse(task.Groups["id"].Value) }
            });
        }

        var status = StatusPattern().Match(text);
        if (projectId is not null && status.Success)
        {
            return Call("projects_change_status_v1", new
            {
                input = new { projectId = Guid.Parse(projectId), status = StatusValue(status.Value) }
            });
        }

        if (projectId is not null && text.Contains("request a review", StringComparison.OrdinalIgnoreCase))
        {
            return Call("projects_request_review_v1", new
            {
                input = new { projectId = Guid.Parse(projectId) }
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

    /// <summary>
    /// A consulted agent receives the platform's consultation instruction as a system message.
    /// </summary>
    private static bool IsConsultation(IReadOnlyList<ChatMessage> messages)
    {
        return messages.Any(message => message.Role == ChatRole.System
            && message.Text.Contains("You are being consulted", StringComparison.Ordinal));
    }

    private static string AnswerFromMemory(IReadOnlyList<ChatMessage> messages)
    {
        var memory = messages
            .Where(message => message.Role == ChatRole.System)
            .Select(message => WorkingMemoryPattern().Match(message.Text))
            .FirstOrDefault(match => match.Success)?
            .Groups["memory"].Value;
        return string.IsNullOrWhiteSpace(memory) || memory == "{}"
            ? "I have not reviewed this project yet."
            : "My last intake review of this project: " + memory;
    }

    private static int StatusValue(string value)
    {
        return Enum.TryParse<SampleProjectManagement.DAL.Entities.ProjectStatus>(value, ignoreCase: true, out var status)
            ? (int)status
            : 0;
    }

    /// <summary>
    /// In a shared thread a message starts with its writer's label, which can hold an id, so the
    /// project is the id after the word "project" (as in "project KEY (id)"); otherwise the first id.
    /// </summary>
    private static string? FindProjectId(string text)
    {
        var reference = ProjectReferencePattern().Match(text);
        if (reference.Success)
        {
            return reference.Groups["id"].Value;
        }

        var any = ProjectIdPattern().Match(text);
        return any.Success ? any.Value : null;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex ProjectIdPattern();

    [GeneratedRegex(@"project\s+(?:[^\s(]+\s+\()?(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReferencePattern();

    [GeneratedRegex(@"complete task (?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex TaskPattern();

    [GeneratedRegex("\\b(draft|active|onhold|completed|archived)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatusPattern();

    [GeneratedRegex("<working-memory[^>]*>(?<memory>.*?)</working-memory>", RegexOptions.Singleline)]
    private static partial Regex WorkingMemoryPattern();
}
