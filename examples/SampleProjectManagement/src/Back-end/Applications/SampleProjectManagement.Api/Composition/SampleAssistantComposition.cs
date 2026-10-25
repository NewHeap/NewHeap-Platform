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
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
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
/// which of those tools the agent is offered. The agents run on the deterministic
/// <see cref="SampleAssistantChatClient"/> unless the sample's secrets configure a real chat model
/// through <see cref="SampleAssistantModel"/>.
/// </summary>
public static class SampleAssistantComposition
{
    public const string AgentId = "sample-project-assistant";
    public const string ProfileName = "project-assistant-chat";

    /// <summary>
    /// SPM-305: the fallback of <see cref="ProfileName"/> for model calls that carry confidential data,
    /// such as the files people upload.
    /// </summary>
    public const string ConfidentialProfileName = "project-assistant-confidential";
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
        // A deterministic local model drives the agents unless the sample's secrets configure a real
        // one; hosts and tests register their own keyed client before this call to replace either.
        var model = SampleAssistantModel.FromConfiguration(configuration, environment);
        if (model is not null)
        {
            services.TryAddKeyedSingleton<IChatClient>(ModelKey, (_, _) => model.CreateChatClient());
            services.AddSingleton(model);
        }
        services.TryAddKeyedSingleton<IChatClient, SampleAssistantChatClient>(ModelKey);
        services.TryAddSingleton<SampleAssistantAuditLog>();
        // SPM-304: counts runs, runs by rules alone and shadow runs per agent and skill from the
        // content-free AgentRunEnded events.
        services.TryAddSingleton<SampleAgentRunFacts>();
        // SPM-312: the portfolio analysis tells the conversation that started it when it finished.
        services.TryAddScoped<SampleProjectManagement.Api.Services.ProjectAnalysisAssistantNotices>();
        services.AddHttpClient(SampleTokenExchangeCredentialProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
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
        // SPM-305: the files the model reads itself. The deterministic model names what it received;
        // a real model reads the types its MediaTypes setting lists. Word and Excel documents reach
        // either as text through the sample's reading tool (SampleAssistantFilesComposition).
        var mediaTypes = (model?.MediaTypes ?? SampleAssistantModel.DeterministicMediaTypes).ToArray();
        var regions = ModelExecutionRegions(services);
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(ProfileName, profile => ConfigureChatProfile(profile, model, mediaTypes, regions)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                // A model call that carries confidential data, such as an uploaded file, goes to the
                // fallback that permits it.
                .WithFallbackProfiles(ConfidentialProfileName))
            // The sample sends confidential data to the same model. A real application points this
            // profile at a model that may receive it, such as one it runs in its own region.
            .AddChatProfile(ConfidentialProfileName, profile => ConfigureChatProfile(profile, model, mediaTypes, regions)
                .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)));

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
            // SPM-247: the application's budget manager also reserves the calls of assistant turns
            // and reads whose work they are: a person's chat, an agent's run, a consultation or a
            // proposal that executes, with the agent, skill and run.
            .UseApplicationBudgetInTurns()
            // SPM-251: an administrator may name a secret instead of storing it: config:<name> reads
            // NewHeap:AI:Assistant:Mcp:Secrets:<name>, and secret-file:<name> a mounted secret file.
            .AddMcpSecretResolver<SampleMountedSecretResolver>(SampleMountedSecretResolver.Prefix)
            // SPM-251: an authentication mode of the sample's own: the person's token is exchanged
            // at the identity provider for a token of the MCP server (D1). Administrators choose it in
            // the server editor like a built-in mode.
            .AddMcpCredentialProvider<SampleTokenExchangeCredentialProvider>(
                SampleTokenExchangeCredentialProvider.Mode,
                NhAssistantMcpCredentialOwnership.Person)
            .ConfigureMcp(mcp =>
            {
                // Administrators may connect any https host that is not blocked; plain http is only
                // accepted for loopback in Development. Only hosts in
                // NewHeap:AI:Assistant:Mcp:ForwardUserTokenHosts receive a person's token, none by default.
                // SPM-251: background runs use a server only after an administrator sets its
                // headlessUse to organization, or to personal for a forwarded token, which then needs
                // the consent of the person a run acts for; every server starts at never.
                mcp.RequireHttps = true;
                mcp.ToolListCacheDuration = TimeSpan.FromMinutes(5);
                mcp.ConnectTimeout = TimeSpan.FromSeconds(10);
                // SPM-299: OAuth servers need the public address of the assistant's callback,
                // <prefix>/mcp/oauth/callback, and the origins of the pages that start an
                // authorization. The sample reads them from NewHeap:AI:Assistant:Mcp:OAuth
                // (appsettings.Development.json); a host without them cannot add an `oauth` server.
                // Client credentials need neither: the host asks the authorization server for a
                // token with its own client id and secret.
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
                    // SPM-289: the gateway's four reads, which the application map points at.
                    SampleAiBridgeComposition.GatewayToolSetId + ".*",
                    NhAssistantConsultationTools.ConsultToolId(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type),
                    NhAssistantConsultationTools.RequestToolId(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type),
                    // SPM-278: the project closer, whose proposals run with its own rights.
                    NhAssistantConsultationTools.ConsultToolId(SampleProjectCloserComposition.AgentId, ProjectSubjects.Type),
                    // SPM-311: the project starter, an agent created in the administration. The
                    // selector selects nothing until an administrator defines its consultation as
                    // data; then the tool appears without a deploy, and disappears when it is removed.
                    NhAssistantConsultationTools.ConsultToolId(Services.SampleRuntimeAgentSeeder.AgentId, ProjectSubjects.Type)
                ],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: AccessPolicy))
            // SPM-289: every turn starts with the map of the data the person may read, generated from
            // the gateway's resources, so the model queries tasks of a project without searching first.
            // SPM-291: every turn also carries the project descriptions of the active division that
            // match the question, read once through the project-documents source with its
            // authorization and division scope.
            .ConfigureAgentContext(AgentId, context => context
                .UseApplicationMap()
                .UseRetrievedContext(retrieval => retrieval
                    .Sources(ProjectAiContextSource.SourceId)
                    .MaxItems(5)
                    .MaxCharacters(4_000)))
            .AddBusinessAuditSink<SampleAssistantAuditSink>()
            .AddBusinessAuditSink<SampleAgentRunFactsSink>()
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
                if (model is not null && model.Timeout * 2 > limits.TurnTimeout)
                {
                    // A turn holds several model calls; a slow real model gets two of its timeouts.
                    limits.TurnTimeout = model.Timeout * 2;
                }
                limits.DailyToolCallBudgetPerActor = 100;
                // Agents that list many exact settings actions need more selectors than the
                // default 128. Prefer prefix selectors; this does not raise MaxToolsPerAgent.
                limits.MaxToolSelectorsPerAgent = 256;
                limits.MaxParticipantsPerConversation = 10;
                // SPM-278: one answer of the project assistant consults the intake reviewer at most
                // twice; a third consultation is refused with assistant-consult-turn-limit before the
                // reviewer runs. Unlimited by default; an agent with runs can set its own limit.
                limits.MaxConsultationsPerTurn = 2;
            }));

        if (model is not null)
        {
            // Every agent model call, triage included, names the region the real model runs in.
            // SPM-323: startup checks that every profile in use (the chat profile, the agents', the
            // skills' and the triage's) permits it, itself or through a fallback; both sample profiles
            // permit local and the model's region (ModelExecutionRegions). The administration refuses
            // a profile that does not with region-not-permitted.
            services.AddNewHeapAssistant(assistant => assistant.UseExecutionRegion(model.Region));
        }

        return services;
    }

    private static NhAiModelProfileBuilder ConfigureChatProfile(
        NhAiModelProfileBuilder profile,
        SampleAssistantModel? model,
        string[] mediaTypes,
        string[] regions)
    {
        return profile
            .UseKeyedClient(ModelKey)
            .RequireCapabilities(NhAiModelCapability.FunctionCalling)
            .WithStreaming(NhAiStreamingPolicy.Allowed)
            .PermitExecutionRegions(regions)
            .AcceptMediaTypes(mediaTypes)
            // A real model reads the agent's instructions and the tool schemas with every call.
            .WithBudget(
                maxInputTokens: model?.MaxInputTokens ?? 8_192,
                maxOutputTokens: model?.MaxOutputTokens ?? 1_024,
                maxCalls: 16)
            .WithTimeout(model?.Timeout ?? TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The execution regions a chat profile on <see cref="ModelKey"/> permits: <c>local</c>, where the
    /// deterministic model runs, and the region of the real model when the sample's secrets configure
    /// one. Call it after <see cref="AddSampleAssistant"/>.
    /// </summary>
    public static string[] ModelExecutionRegions(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var model = services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(SampleAssistantModel))?
            .ImplementationInstance as SampleAssistantModel;
        return model is null ? ["local"] : ["local", model.Region];
    }

    public static IEndpointRouteBuilder MapSampleAssistant(this IEndpointRouteBuilder endpoints)
    {
        // SPM-263: the assistant's routes take the prefix; its live-update hub maps on the host's root
        // at NhAssistantOptions.HubPath (/hub/assistant by default, NewHeap:AI:Assistant:HubPath),
        // outside the prefix. NewHeap's bearer setup reads the WebSocket's query token for that path,
        // also when the setting moves the hub out of /hub.
        endpoints.MapNewHeapAssistant("/api/assistant");
        return endpoints;
    }
}

/// <summary>
/// Localizes assistant tool names and explains a governed project status proposal with data read
/// inside the same authorized division. The technical descriptor remains model-facing metadata.
/// SPM-246: a presentation keeps within <see cref="NhAssistantPresentationLimits"/> (at most eight
/// fields, short texts); one that breaks a cap is dropped with a warning naming this presenter, the
/// tool and the rule, and the approver reads the generic summary. Approvals are presented in every
/// culture of <c>NewHeap:PlatformAspNetCommon:Settings:SupportedCultures</c> (nl-NL and en-US here),
/// so each approver reads their own language.
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
/// working memory; a message that names a project id and its documents reads them through the
/// document tool (SPM-306); any other message searches projects; a tool result is summarized, and
/// files that reach it are named with their type and size or the start of their text (SPM-305). It
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
        if (last is not null && DescribeFiles(last) is { } received)
        {
            // SPM-305: files a person attached, or that a tool returned, follow the text as external
            // data; the deterministic model says what it received instead of reading them.
            return new ChatMessage(ChatRole.Assistant, received);
        }

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
            // SPM-282: a guard found the work already done; the reviewer says why instead of proposing.
            if (result.Result is JsonElement { ValueKind: JsonValueKind.Object } notNeeded
                && notNeeded.TryGetProperty("code", out var notNeededCode)
                && notNeededCode.GetString() == "assistant-proposal-not-needed")
            {
                var explanation = notNeeded.TryGetProperty("explanation", out var why) && why.ValueKind == JsonValueKind.String
                    ? why.GetString()
                    : null;
                return new ChatMessage(
                    ChatRole.Assistant,
                    "Nothing to propose: " + (explanation ?? "the work is already done."));
            }

            // SPM-328: an approval in the person's own chat went to the order desk, which reports here.
            if (ResultCodeOf(result.Result) == "assistant-proposal-handed-to-application")
            {
                return new ChatMessage(
                    ChatRole.Assistant,
                    "The order desk has the approved order; its report follows in this conversation.");
            }

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
        if (text.Contains("why", StringComparison.OrdinalIgnoreCase) && RuleReasons(messages) is { } reasons)
        {
            // SPM-283: asked about a proposal, the project closer answers from the reasons its
            // rules wrote into the thread.
            return new ChatMessage(ChatRole.Assistant, "My rules proposed it because: " + reasons);
        }

        var projectId = FindProjectId(text);
        if (projectId is not null && text.Contains("intake reviewer", StringComparison.OrdinalIgnoreCase))
        {
            // SPM-278: ask the intake reviewer through its generated consultation tools.
            var subjectId = Guid.Parse(projectId).ToString("D");
            return text.Contains("again", StringComparison.OrdinalIgnoreCase)
                ? Call("request_sample-project-intake-reviewer_project_v1", new { input = new { subjectId, message = text } })
                : Call("consult_sample-project-intake-reviewer_project_v1", new { input = new { subjectId, question = text } });
        }

        if (projectId is not null && text.Contains("documents", StringComparison.OrdinalIgnoreCase))
        {
            // SPM-306: the document reader reads the project's documents through its tool.
            return Call("project_documents_read_v1", new
            {
                input = new { projectId = Guid.Parse(projectId) }
            });
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

        var equipment = EquipmentPattern().Match(text);
        if (projectId is not null && equipment.Success)
        {
            // SPM-314: the intake reviewer proposes an equipment order, which the order desk executes.
            return Call("projects_order_equipment_v1", new
            {
                input = new
                {
                    projectId = Guid.Parse(projectId),
                    item = equipment.Groups["item"].Value,
                    quantity = int.Parse(equipment.Groups["quantity"].Value, System.Globalization.CultureInfo.InvariantCulture)
                }
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

    /// <summary>
    /// What the model received in files: the assistant puts an <c>&lt;external-file&gt;</c> marker
    /// before each file, followed by the file itself or the text a reading tool made of it.
    /// <see langword="null"/> for a message without files.
    /// </summary>
    private static string? DescribeFiles(ChatMessage message)
    {
        var received = new List<string>();
        for (var index = 0; index < message.Contents.Count; index++)
        {
            if (message.Contents[index] is not TextContent marker
                || !marker.Text.StartsWith("<external-file>", StringComparison.Ordinal))
            {
                continue;
            }

            var name = FileNamePattern().Match(marker.Text).Groups["name"].Value;
            var body = index + 1 < message.Contents.Count ? message.Contents[index + 1] : null;
            received.Add(body switch
            {
                DataContent file => $"\"{name}\" reached me as a {file.MediaType} file of {file.Data.Length} bytes.",
                TextContent text => $"A reading tool turned \"{name}\" into text, which starts with: {Excerpt(text.Text)}",
                _ => $"\"{name}\" reached me without content."
            });
        }

        if (received.Count == 0)
        {
            return null;
        }

        return "I received " + (received.Count == 1 ? "one file" : received.Count + " files") + ". "
            + string.Join(" ", received)
            + " The sample's deterministic model does not read files; connect a real model to ask about their content.";
    }

    private static string Excerpt(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 200 ? line[..200] + "…" : line;
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

    /// <summary>
    /// The reasons of the latest message an agent's rules wrote: the lines that start with "- ",
    /// outside the platform's summary of the message's tool calls.
    /// </summary>
    private static string? RuleReasons(IReadOnlyList<ChatMessage> messages)
    {
        var reasons = messages
            .Where(message => message.Role == ChatRole.Assistant)
            .Select(message => ToolCallSummaryPattern().Replace(message.Text, string.Empty)
                .Split('\n')
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line[2..].Trim())
                .ToArray())
            .LastOrDefault(lines => lines.Length > 0);
        return reasons is null ? null : string.Join(" ", reasons);
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

    /// <summary>
    /// The <c>code</c> of a tool result the assistant gave the model, as JSON or as the dictionary it
    /// builds itself; <see langword="null"/> without one.
    /// </summary>
    private static string? ResultCodeOf(object? result)
    {
        return result switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } json
                when json.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String => code.GetString(),
            IReadOnlyDictionary<string, object?> dictionary when dictionary.TryGetValue("code", out var value) => value as string,
            _ => null
        };
    }

    [GeneratedRegex(@"project\s+(?:[^\s(]+\s+\()?(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReferencePattern();

    [GeneratedRegex(@"complete task (?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex TaskPattern();

    /// <summary>
    /// SPM-314: an equipment order such as "order 3 laptops for project …".
    /// </summary>
    [GeneratedRegex(@"\border (?<quantity>[0-9]{1,3}) (?<item>[a-z][a-z ]{0,98}?) for project\b", RegexOptions.IgnoreCase)]
    private static partial Regex EquipmentPattern();

    [GeneratedRegex("\\b(draft|active|onhold|completed|archived)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatusPattern();

    [GeneratedRegex("<working-memory[^>]*>(?<memory>.*?)</working-memory>", RegexOptions.Singleline)]
    private static partial Regex WorkingMemoryPattern();

    [GeneratedRegex("<tool-call-summary>.*?</tool-call-summary>", RegexOptions.Singleline)]
    private static partial Regex ToolCallSummaryPattern();

    [GeneratedRegex("^<external-file>[^\"]*\"(?<name>[^\"]*)\"")]
    private static partial Regex FileNamePattern();
}
