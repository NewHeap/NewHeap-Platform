using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The agent builder (RB-7 to RB-9, lane BA): only a person with the access and administration policies
/// sees it, chats with it and gets its tools, which no other agent is offered; every save and every step
/// live waits for that person's approval in the chat and changes nothing without it; it acts with the
/// person's own rights through the administration's services, so the code's ceilings and four eyes hold for
/// it as for the person; and it has no tool for grants, technical accounts, MCP servers, the ceilings or a
/// deletion, never works on itself, and its definition never changes in the administration. On both
/// relational providers where the store is touched.
/// </summary>
public sealed class AssistantAgentBuilderTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Builder = NhAssistantAgentBuilderIds.AgentId;
    private const string AgentId = "project-assistant";
    private const string Administrator = "admin-1";
    private const string OtherAdministrator = "admin-2";
    private const string Script = "done('Nothing to warn about.');";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_an_administrator_sees_and_chats_with_the_agent_builder_and_only_it_gets_its_tools(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I help you build agents.")
            .RespondWithText("I answer with the project tools.");
        await using var app = await StartAsync(model, provider);
        using var person = app.CreateClient("user-1");
        using var administrator = app.CreateClient(Administrator, admin: true);

        // A person without the administration policy neither sees it nor chats with it.
        Assert.DoesNotContain(await AgentIdsAsync(person), id => id == Builder);
        using var refused = await person.PostAsync("/api/assistant/conversations", Json(new { agentId = Builder }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("assistant-agent-forbidden", (await ReadJsonAsync(refused)).GetProperty("code").GetString());

        // An administrator does, and its model is offered the builder's tools and nothing else.
        Assert.Contains(await AgentIdsAsync(administrator), id => id == Builder);
        var conversation = await CreateConversationAsync(administrator, Builder);
        await SendAsync(administrator, conversation, "What can you do?");
        var offered = model.Requests[0].Options!.Tools!.Select(tool => tool.Name).ToArray();
        Assert.Equal(NhAssistantAgentBuilderTools.All.Select(tool => tool.ExportName).Order(StringComparer.Ordinal), offered.Order(StringComparer.Ordinal));

        // Another agent whose selectors name every assistant tool is never offered them.
        var other = await CreateConversationAsync(administrator, "assistant-everything");
        await SendAsync(administrator, other, "What can you do?");
        Assert.DoesNotContain(model.Requests[1].Options?.Tools ?? [], tool => tool.Name.StartsWith("builder_", StringComparison.Ordinal));

        // The same person without the administration policy any more cannot continue the chat.
        using var withoutAdmin = app.CreateClient(Administrator);
        using var continued = await withoutAdmin.PostAsync(
            $"/api/assistant/conversations/{conversation}/messages",
            Json(new { text = "Change the agent.", clientMessageId = "c-later" }));
        Assert.Equal(HttpStatusCode.Forbidden, continued.StatusCode);
        Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_script_is_saved_only_after_the_approval_and_four_eyes_hold_for_what_the_builder_saved(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            // The administrator asks; the builder reads what a script may call and proposes to save it.
            .RespondWithFunctionCall("builder_read_rule_script", new { input = new { agentId = AgentId } })
            .RespondWithFunctionCall("builder_save_rule_script", new { input = new { agentId = AgentId, script = Script, expectedRevision = 0, reason = "Warn about nothing yet." } })
            .RespondWithText("Saved as a candidate.")
            // A second save the administrator rejects.
            .RespondWithFunctionCall("builder_save_rule_script", new { input = new { agentId = AgentId, script = "done('Other.');", expectedRevision = 1 } })
            .RespondWithText("Nothing was saved.")
            // Setting the builder's version live by the same administrator under four eyes.
            .RespondWithFunctionCall("builder_set_live", new { input = new { agentId = AgentId, part = "rule-script", version = 1, expectedRevision = 1 } })
            .RespondWithText("Another administrator sets it live.")
            // Another administrator sets it live.
            .RespondWithFunctionCall("builder_set_live", new { input = new { agentId = AgentId, part = "rule-script", version = 1, expectedRevision = 1, reason = "Reviewed." } })
            .RespondWithText("It is live.");
        await using var app = await StartAsync(model, provider, fourEyes: true);
        using var administrator = app.CreateClient(Administrator, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        // The save waits for the approval, whose card says what changes; nothing is saved yet.
        var asked = await SendAsync(administrator, conversation, "Add a rule script that warns about nothing yet.");
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        Assert.Equal(NhAssistantAgentBuilderTools.SaveRuleScript, approval.GetProperty("toolId").GetString());
        var presentation = approval.GetProperty("presentation");
        // The card is in the host's language, English or Dutch.
        Assert.Contains(presentation.GetProperty("toolDisplayName").GetString(), new[] { "Save a rule script", "Regelscript opslaan" });
        Assert.Contains(AgentId, presentation.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Matches("candidate|kandidaat", presentation.GetProperty("summary").GetString());
        Assert.Equal("waiting-for-approval", asked[^1].Data.GetProperty("status").GetString());
        Assert.Empty((await RuleScriptAsync(app)).Versions);

        // Approved, it is saved with the administrator as its author and the builder acting for them.
        var approved = await DecideAsync(administrator, conversation, approval, "approve");
        Assert.Contains(approved, evt => evt.Name == "tool.completed" && evt.Data.GetProperty("status").GetString() == "succeeded");
        var saved = Assert.Single((await RuleScriptAsync(app)).Versions);
        Assert.Equal((NhAssistantRuleScriptStatuses.Candidate, Administrator, Builder), (saved.Status, saved.AuthorActorId, saved.AuthorAgentId));
        var audited = Assert.Single(app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminRuleScriptSaved);
        Assert.Equal((Administrator, Builder), (audited.ActorId, audited.AskingAgentId));

        // Rejected, nothing is saved.
        var second = await SendAsync(administrator, conversation, "Change it.");
        var rejected = await DecideAsync(administrator, conversation, Assert.Single(second, evt => evt.Name == "approval.required").Data, "reject");
        Assert.DoesNotContain(rejected, evt => evt.Name == "tool.completed" && evt.Data.GetProperty("status").GetString() == "succeeded");
        Assert.Single((await RuleScriptAsync(app)).Versions);

        // Four eyes: the administrator who saved it through the builder does not set it live.
        var third = await SendAsync(administrator, conversation, "Set it live.");
        var ownLive = Assert.Single(third, evt => evt.Name == "approval.required").Data;
        Assert.Matches("four eyes|vier ogen", ownLive.GetProperty("presentation").GetProperty("notice").GetString());
        var refused = await DecideAsync(administrator, conversation, ownLive, "approve");
        var completed = Assert.Single(refused, evt => evt.Name == "tool.completed").Data;
        Assert.Equal(NhAssistantRuleScriptCodes.FourEyes, completed.GetProperty("resultCode").GetString());
        Assert.Null((await RuleScriptAsync(app)).LiveVersion);

        // Another administrator, through their own chat with the builder, does.
        using var reviewer = app.CreateClient(OtherAdministrator, admin: true);
        var review = await CreateConversationAsync(reviewer, Builder);
        var reviewed = await SendAsync(reviewer, review, "Set version 1 live.");
        await DecideAsync(reviewer, review, Assert.Single(reviewed, evt => evt.Name == "approval.required").Data, "approve");
        var live = await RuleScriptAsync(app);
        Assert.Equal(1, live.LiveVersion);
        Assert.Equal(OtherAdministrator, live.Versions.Single().LiveBy);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_reads_and_dry_runs_without_approval_and_its_writes_stay_within_the_code_ceiling(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_read_catalog", new { input = new { } })
            .RespondWithFunctionCall("builder_read_agent", new { input = new { agentId = AgentId } })
            .RespondWithFunctionCall("builder_dry_run_rule_script", new { input = new { agentId = AgentId, version = 1, subjects = new[] { new { type = "nh-system", id = "global" } } } })
            .RespondWithFunctionCall("builder_save_rule_script", new { input = new { agentId = AgentId, script = Script, writes = new[] { "projects.archive" }, expectedRevision = 1 } })
            .RespondWithText("That write is outside what the code allows.")
            .RespondWithFunctionCall("builder_read_agent", new { input = new { agentId = Builder } })
            .RespondWithText("I do not work on myself.");
        await using var app = await StartAsync(model, provider);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().SaveAsync(
                AgentId,
                null,
                new NhAssistantRuleScriptSaveRequest(Script, [], 0),
                new NhAssistantRuleScriptAuthor(Administrator));
            Assert.True(saved.Success, string.Join(",", saved.GetResultItems().Select(item => item.Name)));
        }

        using var administrator = app.CreateClient(Administrator, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        // Reads and the dry run run at once; the save of a write outside the ceiling waits for its approval.
        var events = await SendAsync(administrator, conversation, "Try the script and let it archive projects.");
        var completed = events.Where(evt => evt.Name == "tool.completed").Select(evt => evt.Data.GetProperty("status").GetString()).ToArray();
        Assert.Equal(["succeeded", "succeeded", "succeeded"], completed);
        var catalog = FunctionResult(model, 0);
        Assert.Contains(catalog.GetProperty("data").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("id").GetString() == "projects.change-status");
        Assert.DoesNotContain(catalog.GetProperty("data").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("id").GetString()!.StartsWith(NhAssistantAgentBuilderIds.ToolIdPrefix, StringComparison.Ordinal));
        Assert.Equal(AgentId, catalog.GetProperty("data").GetProperty("ruleScripts").GetProperty("agents")[0].GetProperty("agentId").GetString());
        var dryRun = FunctionResult(model, 2).GetProperty("data");
        Assert.Equal(("dry-run", 1), (dryRun.GetProperty("kind").GetString(), dryRun.GetProperty("subjectCount").GetInt32()));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().GetDryRunAsync(AgentId, null, 1));
        }

        // Approved, the write outside the code's ceiling is refused with the rule scripts' code.
        var approval = Assert.Single(events, evt => evt.Name == "approval.required").Data;
        var decided = await DecideAsync(administrator, conversation, approval, "approve");
        Assert.Equal(
            NhAssistantRuleScriptCodes.WriteNotAllowed,
            Assert.Single(decided, evt => evt.Name == "tool.completed").Data.GetProperty("resultCode").GetString());
        Assert.Single((await RuleScriptAsync(app)).Versions);

        // The builder never reads or changes itself.
        var itself = await SendAsync(administrator, conversation, "Show me your own settings.");
        Assert.Equal(
            NhAssistantAdminErrorCodes.Forbidden,
            Assert.Single(itself, evt => evt.Name == "tool.completed").Data.GetProperty("resultCode").GetString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_creates_an_agent_after_the_approval_and_its_own_definition_never_changes_in_the_administration(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_create_agent", new
            {
                input = new
                {
                    agentId = "deadline-watch",
                    displayName = "Deadline watch",
                    description = "Warns about projects that are due soon.",
                    instructions = "Check the project's deadline.",
                    toolSelectors = new[] { "projects.*" },
                    autonomy = "propose"
                }
            })
            .RespondWithText("The agent exists now.");
        await using var app = await StartAsync(model, provider);
        using var administrator = app.CreateClient(Administrator, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        var asked = await SendAsync(administrator, conversation, "Make an agent that watches deadlines.");
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        Assert.Contains("Deadline watch", approval.GetProperty("presentation").GetProperty("summary").GetString(), StringComparison.Ordinal);
        using (var before = await administrator.GetAsync("/api/assistant/admin/agents"))
        {
            Assert.DoesNotContain((await ReadJsonAsync(before)).EnumerateArray(), agent => agent.GetProperty("id").GetString() == "deadline-watch");
        }

        var decided = await DecideAsync(administrator, conversation, approval, "approve");
        var completed = Assert.Single(decided, evt => evt.Name == "tool.completed").Data;
        Assert.Equal("succeeded", completed.GetProperty("status").GetString());
        // The result names the new agent first, so the administration can follow it to its page.
        Assert.StartsWith("{\"agentId\":\"deadline-watch\"", completed.GetProperty("resultPreview").GetString(), StringComparison.Ordinal);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<AspNet.AgentRuns.INhAssistantAgentDirectory>().GetAsync("deadline-watch");
            Assert.Equal(("admin", NhAiAutonomyLevel.Propose), (created!.Source, created.Autonomy));
        }
        var audited = Assert.Single(app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated);
        Assert.Equal(Administrator, audited.ActorId);

        // The builder's id is reserved and its definition is the platform's: the administration refuses both.
        using var takeId = await administrator.PostAsync("/api/assistant/admin/agents", Json(new
        {
            id = Builder,
            displayName = "Mine",
            description = "Mine.",
            instructions = "Mine.",
            toolSelectors = new[] { "projects.*" },
            mcpServerIds = Array.Empty<string>(),
            autonomy = "execute"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, takeId.StatusCode);
        Assert.Equal("not-allowed", (await ReadJsonAsync(takeId)).GetProperty("errors").GetProperty("id")[0].GetString());
        using var change = await administrator.PutAsync($"/api/assistant/admin/agents/{Builder}", Json(new
        {
            displayName = "Mine",
            description = "Mine.",
            instructions = "Mine.",
            toolSelectors = new[] { "assistant.agent-builder.*" },
            mcpServerIds = Array.Empty<string>(),
            autonomy = "execute",
            expectedVersion = 1
        }));
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    }

    [Fact]
    public void Every_change_of_the_builder_needs_an_approval_and_no_tool_takes_a_secret()
    {
        var catalog = new NhAssistantAgentBuilderToolCatalog();

        Assert.Equal(27, catalog.Descriptors.Count);
        Assert.All(catalog.Descriptors, descriptor =>
        {
            Assert.StartsWith(NhAssistantAgentBuilderIds.ToolIdPrefix, descriptor.Id, StringComparison.Ordinal);
            Assert.Equal(NhAiToolExposure.Agent, descriptor.Exposure);
            Assert.Equal([NhAssistantAgentBuilderIds.Policy], descriptor.AuthorizationPolicies);
            Assert.NotEqual(NhAiToolEffect.Destructive, descriptor.Effect);
            Assert.False(descriptor.AllowWithoutApproval);
        });
        // Every tool that changes something waits for the approval; reads and the dry run do not.
        Assert.All(
            catalog.Descriptors.Where(descriptor => descriptor.Effect == NhAiToolEffect.Mutation),
            descriptor => Assert.Equal(NhAiApprovalRequirement.Required, descriptor.Approval));
        Assert.Equal(
            [
                NhAssistantAgentBuilderTools.DryRunRuleScript,
                NhAssistantAgentBuilderTools.ListAgents,
                NhAssistantAgentBuilderTools.ReadAgent,
                NhAssistantAgentBuilderTools.ReadCatalog,
                NhAssistantAgentBuilderTools.ReadGateway,
                NhAssistantAgentBuilderTools.ReadRuleScript,
                NhAssistantAgentBuilderTools.SearchAccounts
            ],
            catalog.Descriptors.Where(descriptor => descriptor.Approval != NhAiApprovalRequirement.Required).Select(descriptor => descriptor.Id));
        // No tool takes a secret: no credential, key, password, token or certificate is an input.
        Assert.All(NhAssistantAgentBuilderTools.All, tool => Assert.DoesNotContain(
            tool.Input.Properties,
            name => Regex.IsMatch(name, "secret|password|credential|apikey|certificate|oauth|bearer|^(url|key|token|accessToken|header)$", RegexOptions.IgnoreCase)));
        // Whose rights an agent uses is an input of the run settings now (FB94).
        var runSettings = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.SaveRunSettings);
        Assert.Superset(
            new HashSet<string> { "accountability", "serviceAccountActorId", "delegateFromInitiator", "allowGatewayReads", "elevatedReads" },
            runSettings.Input.Properties.ToHashSet());
        NhAiToolCatalogAttestation.Validate(catalog, new ServiceCollection().BuildServiceProvider());
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_call_outside_a_persons_chat_with_the_builder_is_refused_before_anything_runs(string toolId)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new NhAssistantAgentBuilderToolHandler(
            services,
            new NhAssistantRegistrationState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NhAssistantAgentBuilderToolHandler>.Instance);
        var tool = NhAssistantAgentBuilderTools.All.Single(item => item.Id == toolId);

        var result = await handler.HandleAsync(
            tool,
            JsonSerializer.SerializeToElement(new { agentId = AgentId, script = Script, expectedRevision = 0 }),
            new NhAiInvocationContext(Administrator, "assistant", new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal(NhAssistantAdminErrorCodes.Forbidden, result.GetResultItems().First().Name);
    }

    public static TheoryData<string> Writes()
    {
        return new TheoryData<string>(NhAssistantAgentBuilderTools.All.Where(tool => tool.Approval == NhAiApprovalRequirement.Required).Select(tool => tool.Id));
    }

    [Fact]
    public async Task A_change_of_whose_rights_an_agent_uses_and_every_removal_say_so_on_the_card_in_english_and_dutch()
    {
        var presenter = new NhAssistantAgentBuilderPresenter(new NhAssistantRegistrationState());

        var rights = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, accountability = "service-account", serviceAccountActorId = "service-1", allowGatewayReads = true }, "en-US");
        Assert.Contains("whose rights agent project-assistant works with", rights.Notice, StringComparison.Ordinal);
        Assert.Contains(rights.Fields, field => field.Label == "Rights" && field.Value.Contains("a technical account", StringComparison.Ordinal) && field.Value.Contains("service-1", StringComparison.Ordinal) && field.Value.Contains("Gateway reads: on", StringComparison.Ordinal));
        var dutch = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, accountability = "service-account", serviceAccountActorId = "service-1" }, "nl-NL");
        Assert.Contains("met wiens rechten agent project-assistant werkt", dutch.Notice, StringComparison.Ordinal);
        Assert.Contains(dutch.Fields, field => field.Label == "Rechten");

        // A change of the budget alone is no change of rights.
        var budget = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, maxConcurrentRuns = 2 }, "en-US");
        Assert.Equal("The agent builder works with your own rights. Nothing changes until you approve this.", budget.Notice);

        // The required policy, MCP servers, a technical account and the routes of consultations are rights.
        foreach (var (toolId, input) in new (string, object)[]
        {
            (NhAssistantAgentBuilderTools.UpdateAgent, new { agentId = AgentId, expectedVersion = 1, requiredPolicy = (string?)null }),
            (NhAssistantAgentBuilderTools.UpdateAgent, new { agentId = AgentId, expectedVersion = 1, mcpServerIds = new[] { "planning" } }),
            (NhAssistantAgentBuilderTools.SetTechnicalAccount, new { agentId = AgentId, actorId = "service-2" }),
            (NhAssistantAgentBuilderTools.SetConsultationRoutes, new { agentId = AgentId, subjectAgentRights = "on" }),
            (NhAssistantAgentBuilderTools.SetMcpServer, new { agentId = AgentId, serverId = "planning", assigned = true })
        })
        {
            Assert.Contains("whose rights", (await PresentAsync(presenter, toolId, input, "en-US")).Notice, StringComparison.Ordinal);
        }

        // Every removal names what goes.
        var removal = await PresentAsync(presenter, NhAssistantAgentBuilderTools.Remove, new { agentId = AgentId, part = "trigger", triggerId = "weekly" }, "en-US");
        Assert.Equal("Removes trigger weekly of agent project-assistant.", removal.Summary);
        Assert.StartsWith("Note: this removes trigger weekly", removal.Notice, StringComparison.Ordinal);
        var verwijderd = await PresentAsync(presenter, NhAssistantAgentBuilderTools.Remove, new { agentId = AgentId, part = "agent" }, "nl-NL");
        Assert.StartsWith("Let op: dit verwijdert de agent zelf", verwijderd.Notice, StringComparison.Ordinal);
        Assert.StartsWith("Note: this removes the assignment of MCP server planning", (await PresentAsync(presenter, NhAssistantAgentBuilderTools.SetMcpServer, new { agentId = AgentId, serverId = "planning", assigned = false }, "en-US")).Notice, StringComparison.Ordinal);
        Assert.StartsWith("Note: this removes the shadow variant itself", (await PresentAsync(presenter, NhAssistantAgentBuilderTools.PromoteShadowVariant, new { agentId = "variant", expectedVersion = 3 }, "en-US")).Notice, StringComparison.Ordinal);

        // Every card of every write keeps within the presentation's limits, with the longest inputs.
        foreach (var tool in NhAssistantAgentBuilderTools.All.Where(tool => tool.Approval == NhAiApprovalRequirement.Required))
        {
            var longest = tool.Input.Properties.ToDictionary(name => name, name => (object)new string('x', 600));
            foreach (var culture in new[] { "en-US", "nl-NL" })
            {
                var card = await PresentAsync(presenter, tool.Id, longest, culture);
                Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(card));
            }
        }
    }

    [Fact]
    public void Guidance_of_the_application_is_checked_and_reaches_only_the_builder_after_the_rules_and_before_the_application_context()
    {
        var options = new NhAssistantAgentBuilderOptions();
        Assert.Throws<ArgumentException>(() => options.UseGuidance(Guidance("x") with { Manifest = Guidance("x").Manifest with { Trust = NhAiContextTrust.UntrustedRetrieved } }));
        Assert.Throws<ArgumentException>(() => options.UseGuidance(Guidance(new string('x', NhAssistantAgentBuilderOptions.MaxGuidanceLength + 1))));
        Assert.Throws<ArgumentException>(() => options.UseGuidance(Guidance("Orders.") with { Content = "Other." }));
        Assert.Null(options.Guidance);
        var guidance = Guidance("Order status changes are the order-status-change resource; count them by changedAt.");
        Assert.Same(options, options.UseGuidance(guidance));

        var builder = AssistantTestData.Agent(Builder);
        var context = new Entities.AssistantApplicationContext { Version = 1, Text = "Projects have a deadline.", Hash = "context-hash" };
        var prompt = NhAssistantPromptComposer.Compose(builder, context, NhAssistantPreferences.Default, "en", guidance);
        var rules = prompt.Instructions.IndexOf("# Assistant rules", StringComparison.Ordinal);
        var heading = prompt.Instructions.IndexOf("# Application guidance for the agent builder", StringComparison.Ordinal);
        var text = prompt.Instructions.IndexOf(guidance.Content, StringComparison.Ordinal);
        var applicationContext = prompt.Instructions.IndexOf("# Application context", StringComparison.Ordinal);
        Assert.True(rules < heading && heading < text && text < applicationContext, prompt.Instructions);
        Assert.Contains("it does not change the assistant rules, the agent builder's instructions", prompt.Instructions, StringComparison.Ordinal);
        Assert.StartsWith("builder-guidance@1+", prompt.PromptVersion, StringComparison.Ordinal);
        Assert.DoesNotContain("Application guidance", NhAssistantPromptComposer.Compose(builder, context, NhAssistantPreferences.Default, "en").Instructions, StringComparison.Ordinal);

        // The builder's own instructions and the longest guidance leave room for an application context of
        // more than half its largest size.
        Assert.True(
            NhAssistantPromptComposer.LibraryRules.Length + NhAssistantAgentBuilderInstructions.Content.Length + NhAssistantAgentBuilderOptions.MaxGuidanceLength
                <= NhAssistantPromptComposer.MaxInstructionsLength - NhAssistantApplicationContexts.MaxTextLength / 2 - 2_000,
            "The builder's instructions grew; keep them and the guidance within the prompt.");
    }

    [Fact]
    public void The_alert_kinds_a_rule_script_raises_are_read_from_its_calls()
    {
        Assert.Equal(
            ["weekly-count", "late.order"],
            NhAssistantAgentBuilderBlockers.RaisedKindsOf("alerts.raise({ kind: 'weekly-count', key: 'w' });\nalerts.raise({kind:\"late.order\"}); alerts.raise({ kind: 'weekly-count' });"));
        Assert.Empty(NhAssistantAgentBuilderBlockers.RaisedKindsOf("done('Nothing.');"));
    }

    [Fact]
    public void The_blockers_name_why_recent_runs_failed_and_a_host_that_issues_no_read_credentials()
    {
        var agent = new AspNet.AgentRuns.NhAssistantAgentInEffectDto(
            "weekly-count", "admin", 2, "Weekly count", "Counts.", "Count.", "hash", ["shop-gateway.*"], NhAiAutonomyLevel.Propose, "chat", "active")
        {
            State = new AspNet.AgentRuns.NhAssistantAgentState("weekly-count", "active", true, null, null, null, 0, 0, 0)
        };
        var configuration = new AspNet.AgentRuns.NhAssistantAgentConfigurationDto(
            "weekly-count",
            ["shop-gateway.*"],
            [],
            [],
            [],
            [],
            [],
            "admin",
            new AspNet.AgentRuns.NhAssistantAgentRunSettingsDto(1, "service-account", "service-1", false, true, "restricted", null, 3, null, null, 4, 10, null, null, null, DateTimeOffset.UnixEpoch, Administrator, null, true, null));

        var blockers = NhAssistantAgentBuilderBlockers.Of(
            agent,
            configuration,
            new NhAssistantAgentBuilderBlockerFacts(
                ["shop-gateway.query"],
                false,
                [],
                [],
                [NhAiDelegatedReadFailureCodes.TargetUnknown, NhAiDelegatedReadFailureCodes.IssuerRefused, NhAiDelegatedReadFailureCodes.TargetUnknown, "api-bridge-unauthenticated"]));

        var noIssuer = Assert.Single(blockers, blocker => blocker.Code == "no-read-issuer");
        Assert.Null(noIssuer.Fix);
        var failed = blockers.Where(blocker => blocker.Code == "run-failed").ToArray();
        Assert.Equal(
            [NhAiDelegatedReadFailureCodes.TargetUnknown, NhAiDelegatedReadFailureCodes.IssuerRefused, "api-bridge-unauthenticated"],
            failed.Select(blocker => blocker.Cause));
        Assert.Contains("UseRouting before UseAuthentication", failed[0].Message, StringComparison.Ordinal);
        Assert.Null(failed[0].Fix);
        Assert.Equal((NhAssistantAgentBuilderTools.SaveRunSettings, "serviceAccountActorId"), (failed[1].Fix, failed[1].Setting));
        Assert.Equal("accountability", failed[2].Setting);
        // A technical account that answers and gateway reads that are on block nothing of their own.
        Assert.DoesNotContain(blockers, blocker => blocker.Code is "gateway-reads-off" or "gateway-reads-need-accountability" or "technical-account-missing");
    }

    private static async Task<NhAssistantApprovalPresentation> PresentAsync(NhAssistantAgentBuilderPresenter presenter, string toolId, object input, string culture)
    {
        var descriptor = new NhAssistantAgentBuilderToolCatalog().Descriptors.Single(item => item.Id == toolId);
        var presentation = await presenter.PresentApprovalAsync(
            new NhAssistantApprovalPresentationContext(
                descriptor,
                JsonSerializer.SerializeToElement(input),
                new NhAiInvocationContext(Administrator, "assistant", new Dictionary<string, string>()),
                System.Globalization.CultureInfo.GetCultureInfo(culture)),
            CancellationToken.None);
        return presentation!;
    }

    private static NhAiTextAsset Guidance(string content)
    {
        return NhAiTextAssetFactory.Create(
            "builder-guidance",
            1,
            content,
            "test:guidance",
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            "builder-guidance-v1");
    }

    private Task<AssistantWebApplication> StartAsync(NhAiScriptedChatClient model, AssistantTestProvider provider, bool fourEyes = false)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddNewHeapPlatformAIScripting();
            },
            assistantBuilder: assistant => assistant
                .AddBusinessAuditSink<CapturedBusinessAuditSinkAdapter>()
                .AddAgentBuilder()
                .AddAgent(AssistantTestData.Agent("assistant-everything") with { ToolSelectors = ["assistant.*", "projects.*"] })
                .UseRuleScriptsAsData(scripts => scripts
                    .ForAgent(AgentId, ceiling => ceiling.AllowWrites("projects.change-status"))
                    .RequireFourEyes(fourEyes)),
            provider: provider);
    }

    private static async Task<NhAssistantRuleScriptDto> RuleScriptAsync(AssistantWebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().GetAsync(AgentId, null))!;
    }

    private static JsonElement FunctionResult(NhAiScriptedChatClient model, int index)
    {
        var result = model.FunctionResults[index].Result;
        return result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
    }

    private static async Task<IReadOnlyList<string?>> AgentIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/assistant/agents");
        return (await ReadJsonAsync(response)).EnumerateArray().Select(agent => agent.GetProperty("id").GetString()).ToArray();
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
}
