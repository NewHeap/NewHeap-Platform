using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The agent builder does what the administrator can do in the administration's screens, except secrets
/// (FB94), and knows the application (FB95): it changes whose rights an agent uses, removes and assigns
/// existing MCP servers only after the administrator's approval on a card that says so, through the
/// administration's own services and checks; missing input keeps what the agent has; it never takes or
/// returns an MCP server's secret; it reads the gateway's resources with the administrator's rights and names
/// what blocks an agent; and the application's guidance reaches the builder alone. On both relational
/// providers.
/// </summary>
public sealed class AssistantAgentBuilderAdministrationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Builder = NhAssistantAgentBuilderIds.AgentId;
    private const string Weekly = "weekly-count";
    private const string Admin = "admin-1";
    private const string Secret = "SECRET-PLANNING-TOKEN-4711";
    private const string GuidanceText = "Order status changes are the order-status-change resource; count them by changedAt.";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_changes_whose_rights_an_agent_uses_only_after_an_approval_whose_card_says_so(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_read_gateway", new { input = new { query = "orders" } })
            .RespondWithFunctionCall("builder_search_accounts", new { input = new { query = "Operations" } })
            .RespondWithFunctionCall("builder_save_run_settings", new
            {
                input = new
                {
                    agentId = Weekly,
                    accountability = "service-account",
                    serviceAccountActorId = KnownAccountPrincipalFactory.First,
                    allowGatewayReads = true,
                    reason = "The count covers the whole application."
                }
            })
            .RespondWithText("The agent answers to the operations account now.")
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Weekly, expectedVersion = 1, maxConcurrentRuns = 2 } })
            .RespondWithText("Only the concurrency changed.");
        await using var app = await StartAsync(model, provider);
        await CreateWeeklyAsync(app, runSettings: false);
        using var administrator = app.CreateClient(Admin, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        // Without a gateway the builder hears there is none; the account search runs without approval.
        var asked = await SendAsync(administrator, conversation, "Let the weekly count read as the operations account through the gateway.");
        Assert.False(FunctionResult(model, 0).GetProperty("data").GetProperty("gateway").GetBoolean());
        var account = Assert.Single(FunctionResult(model, 1).GetProperty("data").GetProperty("accounts").EnumerateArray());
        Assert.Equal(KnownAccountPrincipalFactory.First, account.GetProperty("actorId").GetString());

        // The change of whose rights the agent uses waits for the approval, on a card that says so.
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        var presentation = approval.GetProperty("presentation");
        Assert.Matches("whose rights agent weekly-count works with|met wiens rechten agent weekly-count werkt", presentation.GetProperty("notice").GetString());
        var rights = Assert.Single(presentation.GetProperty("fields").EnumerateArray(), field => field.GetProperty("label").GetString() is "Rights" or "Rechten");
        Assert.Contains(KnownAccountPrincipalFactory.First, rights.GetProperty("value").GetString(), StringComparison.Ordinal);
        Assert.Null((await ConfigurationAsync(app)).RunSettings);

        await DecideAsync(administrator, conversation, approval, "approve");
        var saved = (await ConfigurationAsync(app)).RunSettings!;
        Assert.Equal(
            (NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First, true),
            (saved.Accountability, saved.ServiceAccountActorId, saved.AllowGatewayReads));
        Assert.Contains(app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events, evt => evt.ActorId == Admin);

        // A change that names only the concurrency keeps whose rights the agent uses, and says nothing of them.
        var second = await SendAsync(administrator, conversation, "Let two runs work at the same time.");
        var budget = Assert.Single(second, evt => evt.Name == "approval.required").Data;
        Assert.DoesNotMatch("whose rights|wiens rechten", budget.GetProperty("presentation").GetProperty("notice").GetString());
        await DecideAsync(administrator, conversation, budget, "approve");
        var kept = (await ConfigurationAsync(app)).RunSettings!;
        Assert.Equal(
            (NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First, true, 2, 2),
            (kept.Accountability, kept.ServiceAccountActorId, kept.AllowGatewayReads, kept.MaxConcurrentRuns, kept.Version));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_removes_only_after_an_approval_whose_card_says_what_goes(AssistantTestProvider provider)
    {
        var removeSkill = new { input = new { agentId = Weekly, part = "skill", skillId = "notes", reason = "Not needed any more." } };
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_remove", removeSkill)
            .RespondWithText("The skill stays.")
            .RespondWithFunctionCall("builder_remove", removeSkill)
            .RespondWithText("The skill is gone.")
            .RespondWithFunctionCall("builder_remove", new { input = new { agentId = "project-assistant", part = "agent" } })
            .RespondWithText("An agent in code stays.")
            .RespondWithFunctionCall("builder_remove", new { input = new { agentId = Weekly, part = "agent" } })
            .RespondWithText("The agent is deleted.");
        await using var app = await StartAsync(model, provider);
        await CreateWeeklyAsync(app, runSettings: true);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var skill = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SaveSkillAsync(
                Weekly,
                "notes",
                new NhAssistantDataSkillRequest("Writes notes."),
                Admin);
            Assert.True(skill.Success, string.Join(", ", skill.AllErrorMessages));
        }
        using var administrator = app.CreateClient(Admin, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        // The card names what goes; a rejection removes nothing.
        var asked = await SendAsync(administrator, conversation, "Remove the notes skill.");
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        Assert.Matches("this removes skill notes|dit verwijdert vaardigheid notes", approval.GetProperty("presentation").GetProperty("notice").GetString());
        await DecideAsync(administrator, conversation, approval, "reject");
        Assert.Contains((await ConfigurationAsync(app)).DataSkills, skill => skill.SkillId == "notes");

        // Approved, it goes.
        var again = await SendAsync(administrator, conversation, "Remove it after all.");
        await DecideAsync(administrator, conversation, Assert.Single(again, evt => evt.Name == "approval.required").Data, "approve");
        Assert.DoesNotContain((await ConfigurationAsync(app)).DataSkills, skill => skill.SkillId == "notes");

        // The administration's own checks hold: an agent in code is not deleted.
        var code = await SendAsync(administrator, conversation, "Delete the project assistant.");
        var refused = await DecideAsync(administrator, conversation, Assert.Single(code, evt => evt.Name == "approval.required").Data, "approve");
        Assert.Equal("assistant-code-agent-not-deletable", Assert.Single(refused, evt => evt.Name == "tool.completed").Data.GetProperty("resultCode").GetString());

        // An agent created in the administration is deleted, audited for the administrator.
        var delete = await SendAsync(administrator, conversation, "Delete the weekly count.");
        var deleteCard = Assert.Single(delete, evt => evt.Name == "approval.required").Data;
        Assert.Matches("the agent itself|de agent zelf", deleteCard.GetProperty("presentation").GetProperty("notice").GetString());
        await DecideAsync(administrator, conversation, deleteCard, "approve");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>().GetAsync(Weekly));
        }
        var deleted = Assert.Single(app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentDeleted);
        Assert.Equal(Admin, deleted.ActorId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_assigns_an_existing_mcp_server_and_never_takes_or_returns_its_secret(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { query = "nothing-matches" } })
            .RespondWithFunctionCall("builder_set_mcp_server", new { input = new { agentId = Weekly, serverId = "planning", assigned = true, secret = "guessed" } })
            .RespondWithFunctionCall("builder_set_mcp_server", new { input = new { agentId = Weekly, serverId = "planning", assigned = true } })
            .RespondWithText("Assigned after your approval.")
            .RespondWithFunctionCall("builder_set_mcp_headless_use", new { input = new { serverId = "planning", headlessUse = "organization" } })
            .RespondWithText("Runs without a person may use it now.");
        await using var app = await StartAsync(model, provider);
        await CreateWeeklyAsync(app, runSettings: false);
        using var administrator = app.CreateClient(Admin, admin: true);
        using (var created = await administrator.PostAsync("/api/assistant/admin/mcp-servers", Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = "http://localhost/planning/mcp",
            authMode = "bearer",
            secret = Secret,
            isEnabled = true
        })))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var conversation = await CreateConversationAsync(administrator, Builder);

        // The catalog lists the server without its address, connection or secret; a call that sends a
        // secret is refused before anything runs.
        var asked = await SendAsync(administrator, conversation, "Give the weekly count the planning tools.");
        var catalog = FunctionResult(model, 0).GetProperty("data");
        var server = Assert.Single(catalog.GetProperty("mcpServers").EnumerateArray());
        Assert.Equal(("planning", "never"), (server.GetProperty("id").GetString(), server.GetProperty("headlessUse").GetString()));
        Assert.Equal(
            ["assignedAgentIds", "authMode", "displayName", "headlessUse", "id", "isEnabled"],
            server.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Secret, catalog.GetRawText(), StringComparison.Ordinal);
        var refusedSecret = asked.Where(evt => evt.Name == "tool.completed").Select(evt => evt.Data).ToArray()[1];
        Assert.Equal(("failed", NhAiToolFailureCodes.InputInvalid), (refusedSecret.GetProperty("status").GetString(), refusedSecret.GetProperty("resultCode").GetString()));
        Assert.DoesNotContain("guessed", refusedSecret.GetRawText(), StringComparison.Ordinal);

        // The assignment waits for the approval on a card that says it changes whose rights the agent uses.
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        Assert.Matches("whose rights|wiens rechten", approval.GetProperty("presentation").GetProperty("notice").GetString());
        var decided = await DecideAsync(administrator, conversation, approval, "approve");
        var completed = Assert.Single(decided, evt => evt.Name == "tool.completed").Data;
        Assert.Equal("succeeded", completed.GetProperty("status").GetString());
        Assert.DoesNotContain(Secret, completed.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(["planning"], (await AgentAsync(app)).McpServerIds);

        // Whether runs without a person use the server changes after the approval; its secret is kept.
        var headless = await SendAsync(administrator, conversation, "Let its runs use the planning server on their own.");
        var headlessCard = Assert.Single(headless, evt => evt.Name == "approval.required").Data;
        Assert.Matches("every agent the server is assigned to|elke agent waaraan de server is toegewezen", headlessCard.GetProperty("presentation").GetProperty("notice").GetString());
        await DecideAsync(administrator, conversation, headlessCard, "approve");
        using var servers = await administrator.GetAsync("/api/assistant/admin/mcp-servers");
        var stored = Assert.Single((await ReadJsonAsync(servers)).EnumerateArray());
        Assert.Equal(("organization", true), (stored.GetProperty("headlessUse").GetString(), stored.GetProperty("hasSecret").GetBoolean()));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_reads_the_gateway_names_what_blocks_the_agent_and_reads_the_applications_guidance(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_read_gateway", new { input = new { query = "status changes" } })
            .RespondWithFunctionCall("builder_read_gateway", new { input = new { resource = "order-status-change" } })
            .RespondWithFunctionCall("builder_read_agent", new { input = new { agentId = Weekly } })
            .RespondWithText("Gateway reads are off and the alert kind is not listed.")
            .RespondWithText("I answer as another agent.");
        await using var app = await StartAsync(model, provider, gateway: true);
        await CreateWeeklyAsync(app, runSettings: true);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var script = await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().SaveAsync(
                Weekly,
                null,
                new NhAssistantRuleScriptSaveRequest("alerts.raise({ kind: 'weekly-count', key: 'week' }); done('Counted.');", [], 0),
                new NhAssistantRuleScriptAuthor(Admin));
            Assert.True(script.Success, string.Join(", ", script.AllErrorMessages));
        }
        using var administrator = app.CreateClient(Admin, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        // Reads run at once: the gateway's resources with the administrator's own rights, and the agent.
        var events = await SendAsync(administrator, conversation, "Why does the weekly count not work?");
        Assert.All(events.Where(evt => evt.Name == "tool.completed"), evt => Assert.Equal("succeeded", evt.Data.GetProperty("status").GetString()));
        Assert.DoesNotContain(events, evt => evt.Name == "approval.required");
        var found = FunctionResult(model, 0).GetProperty("data");
        Assert.Equal("order-status-change", Assert.Single(found.GetProperty("resources").EnumerateArray()).GetProperty("resource").GetString());
        var described = FunctionResult(model, 1).GetProperty("data").GetProperty("resource");
        Assert.Equal("changedAt", described.GetProperty("query").GetProperty("filterFields")[0].GetProperty("key").GetString());
        Assert.Equal([Admin, Admin], app.Services.GetRequiredService<FakeGatewayResources>().Readers);

        // The blockers name the settings and the tools that change them.
        var blockers = FunctionResult(model, 2).GetProperty("data").GetProperty("blockers").EnumerateArray()
            .ToDictionary(blocker => blocker.GetProperty("code").GetString()!, blocker => blocker);
        Assert.Equal("allowGatewayReads", blockers["gateway-reads-off"].GetProperty("setting").GetString());
        Assert.Equal(NhAssistantAgentBuilderTools.SaveRunSettings, blockers["gateway-reads-off"].GetProperty("fix").GetString());
        Assert.Equal("accountability", blockers["gateway-reads-need-accountability"].GetProperty("setting").GetString());
        Assert.Contains("weekly-count", blockers["alert-kind-not-listed"].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(NhAssistantAgentBuilderTools.SaveTrigger, blockers["no-trigger"].GetProperty("fix").GetString());

        // The application's guidance reaches the builder after the rules and before the application context,
        // and no other agent.
        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.True(
            instructions.IndexOf("# Assistant rules", StringComparison.Ordinal) < instructions.IndexOf(GuidanceText, StringComparison.Ordinal)
            && instructions.IndexOf(GuidanceText, StringComparison.Ordinal) < instructions.IndexOf("# Agent instructions", StringComparison.Ordinal),
            instructions);
        var other = await CreateConversationAsync(administrator, "project-assistant");
        await SendAsync(administrator, other, "Hello.");
        Assert.DoesNotContain(GuidanceText, model.Requests[^1].Options!.Instructions!, StringComparison.Ordinal);
    }

    private Task<AssistantWebApplication> StartAsync(NhAiScriptedChatClient model, AssistantTestProvider provider, bool gateway = false)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddNewHeapPlatformAIScripting();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                services.AddNewHeapActorSearch<OperationsAccounts>().AddNewHeapActorDirectory<OperationsAccounts>();
                // Gateway reads need the host to issue a person's delegated read credential.
                services.AddSingleton<INhAiDelegatedReadSurface, TestReadSurface>();
                services.AddScoped<INhAiDelegatedReadCredentialIssuer, TestNoCredentialIssuer>();
                if (gateway)
                {
                    services.AddSingleton<FakeGatewayResources>();
                    services.AddScoped<INhAiGatewayResources>(provider => provider.GetRequiredService<FakeGatewayResources>().For(provider));
                }
            },
            assistantBuilder: assistant => assistant
                .AddBusinessAuditSink<CapturedBusinessAuditSinkAdapter>()
                .AddAgentBuilder(builder => builder.UseGuidance(NhAiTextAssetFactory.Create(
                    "builder-guidance",
                    1,
                    GuidanceText,
                    "test:guidance",
                    NhAiAssetRole.SystemInstructions,
                    NhAiContextTrust.TrustedApplication,
                    NhAiModelCapability.Chat,
                    [],
                    "default",
                    NhAiDataClassification.Internal,
                    NhAiRetentionCategory.Operational,
                    "builder-guidance-v1")))
                .ConfigureMcp(mcp => mcp.AllowLoopback = true)
                .UseRuleScriptsAsData(scripts => scripts.ForAgentsCreatedInAdministration()),
            provider: provider);
    }

    /// <summary>
    /// The agent created in the administration the tests work on: its tools read through the gateway, and
    /// with <paramref name="runSettings"/> it answers to itself.
    /// </summary>
    private static async Task CreateWeeklyAsync(AssistantWebApplication app, bool runSettings)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
        var created = await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(Weekly, "Weekly count", "Counts last week's changes.", "Count the changes.", ["shop-gateway.*", "projects.*"], NhAiAutonomyLevel.Propose, WorksOnApplication: true),
            Admin);
        Assert.True(created.Success, string.Join(", ", created.AllErrorMessages));
        if (runSettings)
        {
            var saved = await configuration.SaveRunSettingsAsync(Weekly, new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent), Admin);
            Assert.True(saved.Success, string.Join(", ", saved.AllErrorMessages));
        }
    }

    private static async Task<NhAssistantAgentConfigurationDto> ConfigurationAsync(AssistantWebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetAsync(Weekly))!;
    }

    private static async Task<NhAssistantAgentInEffectDto> AgentAsync(AssistantWebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>().GetAsync(Weekly))!;
    }

    private static JsonElement FunctionResult(NhAiScriptedChatClient model, int index)
    {
        var result = model.FunctionResults[index].Result;
        return result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client, string agentId)
    {
        using var response = await client.PostAsync("/api/assistant/conversations", Json(new { agentId }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<IReadOnlyList<ServerSentEvent>> SendAsync(HttpClient client, Guid conversationId, string text)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/messages",
            Json(new { text, clientMessageId = Guid.NewGuid().ToString("N") }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAllAsync(response)).Events;
    }

    private static async Task<IReadOnlyList<ServerSentEvent>> DecideAsync(HttpClient client, Guid conversationId, JsonElement approval, string decision)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/approvals/{approval.GetProperty("approvalId").GetString()}/decide",
            Json(new { decision, expectedProposalHash = approval.GetProperty("proposalHash").GetString() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAllAsync(response)).Events;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>The application's account search knows one technical account.</summary>
    private sealed class OperationsAccounts : INhActorSearch, INhActorDirectory
    {
        private static readonly NhActorInfo Operations = new(new NhActorRef(NhActorKinds.User, KnownAccountPrincipalFactory.First), "Operations account")
        {
            Email = "operations@example.test"
        };

        public Task<IReadOnlyList<NhActorInfo>> SearchAsync(NhActorSearchRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NhActorInfo>>(
                Operations.DisplayName.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ? [Operations] : []);
        }

        public Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(IReadOnlyCollection<NhActorRef> actors, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<NhActorRef, NhActorInfo> known = actors
                .Where(actor => actor == Operations.Actor)
                .ToDictionary(actor => actor, _ => Operations);
            return Task.FromResult(known);
        }
    }

    /// <summary>
    /// Stands in for the API bridge gateway's resources: one resource of order status changes that can be
    /// filtered by date and counted, read by the person of the current request, whom it records.
    /// </summary>
    internal sealed class FakeGatewayResources
    {
        private readonly List<string> _readers = [];

        public IReadOnlyList<string> Readers
        {
            get
            {
                lock (_readers)
                {
                    return _readers.ToArray();
                }
            }
        }

        public INhAiGatewayResources For(IServiceProvider services)
        {
            return new Reader(this, services);
        }

        private void Record(IServiceProvider services)
        {
            var person = services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext?.User
                .FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "nobody";
            lock (_readers)
            {
                _readers.Add(person);
            }
        }

        private sealed class Reader(FakeGatewayResources owner, IServiceProvider services) : INhAiGatewayResources
        {
            public IReadOnlyCollection<string> ToolIds { get; } =
                ["shop-gateway.search-resources", "shop-gateway.describe-resource", "shop-gateway.query", "shop-gateway.get"];

            public Task<TaskResult<JsonElement>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
            {
                owner.Record(services);
                return Task.FromResult(TaskResult<JsonElement>.Succeeded(JsonSerializer.SerializeToElement(new[]
                {
                    new { resource = "order-status-change", title = "Order status changes", summary = "When an order's status changed.", operations = new[] { "query" } }
                })));
            }

            public Task<TaskResult<JsonElement>> DescribeAsync(string resource, CancellationToken cancellationToken)
            {
                owner.Record(services);
                return Task.FromResult(resource == "order-status-change"
                    ? TaskResult<JsonElement>.Succeeded(JsonSerializer.SerializeToElement(new
                    {
                        resource,
                        title = "Order status changes",
                        description = "When an order's status changed.",
                        query = new
                        {
                            filterFields = new[] { new { key = "changedAt", type = "date", operators = new[] { "gte", "lt" } } },
                            searchable = false,
                            orderFields = new[] { "changedAt" }
                        },
                        resultFields = new[] { new { key = "orderId" }, new { key = "changedAt" } }
                    }))
                    : TaskResult<JsonElement>.Failed("api-bridge-not-found", "The resource does not exist or is not available to you."));
            }
        }
    }
}
