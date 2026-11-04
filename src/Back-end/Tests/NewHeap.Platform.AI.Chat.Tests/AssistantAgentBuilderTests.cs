using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;
using NewHeap.Platform.AI.Chat.Runtime;
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
        var presenter = Presenter();

        var rights = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, accountability = "service-account", serviceAccountActorId = "service-1", allowGatewayReads = true }, "en-US");
        Assert.Equal("Note: this changes whose rights agent project-assistant works with (Accountability, Technical account, Gateway reads). Its runs then read and act with those rights. Nothing changes until you approve this.", rights.Notice);
        Assert.Equal(
            [("Agent", AgentId), ("Accountability", "service-account"), ("Technical account", "service-1"), ("Gateway reads", "yes")],
            rights.Fields.Select(field => (field.Label, field.Value)));
        var dutch = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, accountability = "service-account", serviceAccountActorId = "service-1" }, "nl-NL");
        Assert.Contains("met wiens rechten agent project-assistant werkt", dutch.Notice, StringComparison.Ordinal);
        Assert.Contains(dutch.Fields, field => field.Label == "Verantwoording" && field.Value == "service-account");

        // Another accountability without a technical account named removes the technical account, and says so.
        var away = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, accountability = "subject-responsible" }, "en-US");
        Assert.Contains(away.Fields, field => field.Label == "Technical account" && field.Value == "none (a technical account set before is removed)");
        Assert.Contains("(Accountability, Technical account)", away.Notice, StringComparison.Ordinal);

        // A change of the budget alone is no change of rights, and the card names the value.
        var budget = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, maxConcurrentRuns = 2, approvalPolicy = "review" }, "en-US");
        Assert.Equal("The agent builder works with your own rights. Nothing changes until you approve this.", budget.Notice);
        Assert.Contains(budget.Fields, field => field.Label == "Runs at the same time" && field.Value == "2");
        Assert.Contains(budget.Fields, field => field.Label == "Approval policy" && field.Value == "review");

        // The required policy, MCP servers, who runs and sees whole-application work, a technical account and
        // the routes of consultations are rights.
        foreach (var (toolId, input) in new (string, object)[]
        {
            (NhAssistantAgentBuilderTools.UpdateAgent, new { agentId = AgentId, expectedVersion = 1, requiredPolicy = (string?)null }),
            (NhAssistantAgentBuilderTools.UpdateAgent, new { agentId = AgentId, expectedVersion = 1, mcpServerIds = new[] { "planning" } }),
            (NhAssistantAgentBuilderTools.UpdateAgent, new { agentId = AgentId, expectedVersion = 1, runPolicy = "app.operators" }),
            (NhAssistantAgentBuilderTools.SetTechnicalAccount, new { agentId = AgentId, actorId = "service-2" }),
            (NhAssistantAgentBuilderTools.SetConsultationRoutes, new { agentId = AgentId, subjectAgentRights = "on" }),
            (NhAssistantAgentBuilderTools.SetMcpServer, new { agentId = AgentId, serverId = "planning", assigned = true })
        })
        {
            Assert.Contains("whose rights", (await PresentAsync(presenter, toolId, input, "en-US")).Notice, StringComparison.Ordinal);
        }

        // Clearing who sees the whole application's results says so in a field of its own first, then the
        // other values of a definition.
        var view = await PresentAsync(presenter, NhAssistantAgentBuilderTools.UpdateAgent, new { agentId = AgentId, expectedVersion = 1, viewPolicy = (string?)null, compositions = true, dataClassification = "confidential", offeredInChat = false }, "en-US");
        Assert.Contains("Who sees its results for the whole application", view.Notice, StringComparison.Ordinal);
        Assert.Equal(
            [("Agent", AgentId), ("Who sees its results for the whole application", "cleared (the default applies)"), ("Compositions", "yes"), ("Version", "1"), ("Offered in the chat", "no"), ("Classification", "confidential")],
            view.Fields.Select(field => (field.Label, field.Value)));

        // Every removal names what goes.
        var removal = await PresentAsync(presenter, NhAssistantAgentBuilderTools.Remove, new { agentId = AgentId, part = "trigger", triggerId = "weekly" }, "en-US");
        Assert.Equal("Removes trigger weekly of agent project-assistant.", removal.Summary);
        Assert.StartsWith("Note: this removes trigger weekly", removal.Notice, StringComparison.Ordinal);
        var verwijderd = await PresentAsync(presenter, NhAssistantAgentBuilderTools.Remove, new { agentId = AgentId, part = "agent" }, "nl-NL");
        Assert.StartsWith("Let op: dit verwijdert de agent zelf", verwijderd.Notice, StringComparison.Ordinal);
        Assert.StartsWith("Note: this removes the assignment of MCP server planning", (await PresentAsync(presenter, NhAssistantAgentBuilderTools.SetMcpServer, new { agentId = AgentId, serverId = "planning", assigned = false }, "en-US")).Notice, StringComparison.Ordinal);

        // With the longest input its schema allows, every write has its own card within the presentation's
        // limits, saying when a value is in full only in the technical details, or it is refused with a
        // reason before it is proposed; never the generic summary.
        var (cards, cutCards, refused) = (0, 0, 0);
        foreach (var tool in NhAssistantAgentBuilderTools.All.Where(tool => tool.Approval == NhAiApprovalRequirement.Required))
        {
            var (longest, _) = NhAssistantAgentBuilderInput.Read(LongestInput(tool), tool.Input.Schema);
            Assert.NotNull(longest);
            foreach (var language in new[] { false, true })
            {
                var (card, refusal) = await presenter.BuildAsync(tool, longest, language, CancellationToken.None);
                if (card is null)
                {
                    Assert.False(string.IsNullOrWhiteSpace(refusal), tool.Id);
                    refused++;
                    continue;
                }

                cards++;
                Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(card));
                if (card.Fields.Any(field => field.Value.EndsWith('…')))
                {
                    Assert.Matches("technical details|technische details", card.Notice);
                    cutCards++;
                }
            }
        }
        Assert.True(cards > 0 && cutCards > 0 && refused > 0, $"{cards} cards, {cutCards} shortened, {refused} refused");
    }

    [Fact]
    public async Task Every_value_of_whose_rights_has_a_field_of_its_own_in_full_or_the_change_is_refused()
    {
        var presenter = Presenter();
        var update = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.UpdateAgent);
        var policy = new string('p', 256);

        // Long other values do not push the rights off the card: those come first, in full.
        var (card, refusal) = await BuildAsync(presenter, update, new
        {
            agentId = AgentId,
            expectedVersion = 4,
            displayName = new string('n', 256),
            description = new string('d', 512),
            toolSelectors = Enumerable.Range(0, 3).Select(index => $"tools.{index}.{new string('t', 90)}").ToArray(),
            requiredPolicy = policy,
            mcpServerIds = new[] { "planning", "billing" },
            runPolicy = "app.operators",
            viewPolicy = (string?)null
        });
        Assert.Null(refusal);
        Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(card!));
        Assert.Equal(
            [("Agent", AgentId), ("Required policy", policy), ("MCP servers", "planning, billing"), ("Who may run it for the whole application", "app.operators"), ("Who sees its results for the whole application", "cleared (the default applies)")],
            card!.Fields.Take(5).Select(field => (field.Label, field.Value)));
        // What the agent may use is never shortened: its tools span two fields.
        Assert.Equal(
            ["Tools (1 of 2)", "Tools (2 of 2)"],
            card.Fields.Where(field => field.Label.StartsWith("Tools", StringComparison.Ordinal)).Select(field => field.Label));
        Assert.Equal(
            string.Join(", ", Enumerable.Range(0, 3).Select(index => $"tools.{index}.{new string('t', 90)}")),
            string.Join(", ", card.Fields.Where(field => field.Label.StartsWith("Tools", StringComparison.Ordinal)).Select(field => field.Value)));
        Assert.Contains("technical details", card.Notice, StringComparison.Ordinal);

        // A value of whose rights that cannot be shown in full refuses the change, naming it.
        var (none, why) = await BuildAsync(presenter, update, new
        {
            agentId = AgentId,
            expectedVersion = 4,
            mcpServerIds = Enumerable.Range(0, 64).Select(index => $"server-{index}-{new string('s', 32)}").ToArray()
        });
        Assert.Null(none);
        Assert.Contains("'mcpServerIds'", why, StringComparison.Ordinal);

        // A list that a field does not hold but the card does spans fields in full.
        var (spanned, _) = await BuildAsync(presenter, update, new
        {
            agentId = AgentId,
            expectedVersion = 4,
            mcpServerIds = Enumerable.Range(0, 10).Select(index => $"server-{index}-{new string('s', 32)}").ToArray()
        });
        Assert.Equal(["MCP servers (1 of 2)", "MCP servers (2 of 2)"], spanned!.Fields.Where(field => field.Label.StartsWith("MCP", StringComparison.Ordinal)).Select(field => field.Label));

        // So do values of whose rights that together do not fit the stored card, with their escapes.
        var (tooBig, size) = await BuildAsync(presenter, update, new
        {
            agentId = AgentId,
            expectedVersion = 4,
            requiredPolicy = new string('<', 256),
            runPolicy = new string('\'', 256),
            viewPolicy = new string('&', 256)
        });
        Assert.Null(tooBig);
        Assert.Contains("within its size", size, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Values_with_escapes_or_other_scripts_are_shortened_until_the_stored_card_fits_and_rights_stay_whole()
    {
        var presenter = Presenter();
        var update = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.UpdateAgent);
        var settings = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.SaveRunSettings);
        var policy = string.Concat(Enumerable.Repeat("é日", 100));

        foreach (var dutch in new[] { false, true })
        {
            var (card, refusal) = await presenter.BuildAsync(update, Read(update, new
            {
                agentId = AgentId,
                expectedVersion = 4,
                displayName = new string('<', 256),
                description = string.Concat(Enumerable.Repeat("Ω\"'&", 128)),
                toolSelectors = new[] { "projects.*", "😀" + new string('ж', 60) },
                requiredPolicy = policy
            }), dutch, CancellationToken.None);
            Assert.Null(refusal);
            Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(card!));
            Assert.Contains(card!.Fields, field => field.Value == policy);
            Assert.Contains(card.Fields, field => field.Value == "projects.*, 😀" + new string('ж', 60));
            Assert.Matches("technical details|technische details", card.Notice);

            // A reason or a text of escapes alone does not drop the card either.
            var (reasons, none) = await presenter.BuildAsync(settings, Read(settings, new
            {
                agentId = AgentId,
                accountability = "service-account",
                serviceAccountActorId = "операции-1",
                approvalPolicy = new string('\'', 128),
                triageProfile = new string('<', 128),
                ruleHandler = new string('&', 128),
                reason = new string('"', 500)
            }), dutch, CancellationToken.None);
            Assert.Null(none);
            Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(reasons!));
            Assert.Contains(reasons!.Fields, field => field.Value == "операции-1");
        }
    }

    [Fact]
    public async Task A_promotion_names_the_live_agent_what_it_takes_over_and_that_the_variant_goes()
    {
        var variant = new AspNet.AgentRuns.NhAssistantAgentInEffectDto(
            "planner-next", "admin", 2, "Planner next", "Plans.", "Plan.", "hash", ["projects.*", "mail.send"], NhAiAutonomyLevel.Execute, "chat", "active")
        {
            State = new AspNet.AgentRuns.NhAssistantAgentState("planner-next", "active", true, null, null, null, 0, 0, 0),
            ShadowOf = "planner",
            RequiredPolicy = "app.planners",
            DataClassification = NhAiDataClassification.Confidential
        };
        var live = new AspNet.AgentRuns.NhAssistantAgentInEffectDto(
            "planner", "admin", 3, "Planner", "Plans.", "Plan.", "hash", ["projects.*"], NhAiAutonomyLevel.Propose, "chat", "active")
        {
            State = new AspNet.AgentRuns.NhAssistantAgentState("planner", "active", true, null, null, null, 0, 0, 0)
        };
        var presenter = Presenter(services => services.AddSingleton<AspNet.AgentRuns.INhAssistantAgentDirectory>(new KnownAgents(variant, live)));

        var card = await PresentAsync(presenter, NhAssistantAgentBuilderTools.PromoteShadowVariant, new { agentId = "planner-next", expectedVersion = 3, expectedVariantVersion = 2, moveSkills = true }, "en-US");

        Assert.Equal("Copies the definition of shadow variant planner-next onto live agent planner as its next version, with its skills, then deletes the variant.", card.Summary);
        Assert.Contains(card.Fields, field => field.Label == "Live agent" && field.Value == "planner");
        // The settings of whose rights come before the tools, which have a field of their own.
        Assert.Contains(card.Fields, field => field.Label == "Takes over"
            && field.Value == "required policy: app.planners; autonomy: execute; model profile: chat; classification: confidential; compositions: no; scripts: no");
        Assert.Contains(card.Fields, field => field.Label == "Takes over the tools" && field.Value == "projects.*, mail.send");
        Assert.Contains(card.Fields, field => field.Label == "Moves these skills, live at once" && field.Value == "none");
        Assert.Contains("Its skills become live versions of the live agent's skills at once.", card.Notice, StringComparison.Ordinal);
        Assert.Contains("whose rights agent planner works with", card.Notice, StringComparison.Ordinal);
        Assert.Contains("the model profile, the classification, the compositions and scripts and the tools", card.Notice, StringComparison.Ordinal);
        Assert.Contains("the variant is deleted", card.Notice, StringComparison.Ordinal);

        // The card is of the variant at the version the call names, which the promotion holds it to: another
        // version is refused before it is proposed, and the version is required.
        var promote = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.PromoteShadowVariant);
        var (changed, refusal) = await BuildAsync(presenter, promote, new { agentId = "planner-next", expectedVersion = 3, expectedVariantVersion = 1 });
        Assert.Null(changed);
        Assert.Contains("version 2", refusal, StringComparison.Ordinal);
        Assert.Contains("expectedVariantVersion", NhAssistantAgentBuilderInput.Read(JsonSerializer.SerializeToElement(new { agentId = "planner-next", expectedVersion = 3 }), promote.Input.Schema).Refused, StringComparison.Ordinal);

        // A variant that becomes its package's next version carries parts the card cannot show: the
        // builder does not take it.
        Assert.Contains("'asPackageVersion'", NhAssistantAgentBuilderInput.Read(JsonSerializer.SerializeToElement(new { agentId = "planner-next", expectedVersion = 3, expectedVariantVersion = 2, asPackageVersion = true }), promote.Input.Schema).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_builder_write_is_proposed_only_with_the_builders_own_card_in_every_culture()
    {
        var descriptor = new NhAssistantAgentBuilderToolCatalog().Descriptors.Single(item => item.Id == NhAssistantAgentBuilderTools.UpdateAgent);
        var context = new NhAiInvocationContext(Administrator, "assistant", new Dictionary<string, string>());
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var other = new OtherPresenter();

        // The builder's card is the one proposed, in every culture of the application.
        var resolver = Resolver(Presenter(), other);
        var shown = await resolver.ResolveApprovalInCulturesAsync(descriptor, JsonSerializer.SerializeToElement(new { agentId = AgentId, expectedVersion = 1, requiredPolicy = "app.helpers" }), context, english, CancellationToken.None);
        Assert.Null(shown.Refusal);
        Assert.Contains("whose rights agent project-assistant works with", shown.Presentation!.Notice, StringComparison.Ordinal);
        Assert.Contains("met wiens rechten agent project-assistant werkt", shown.LocalizedJson, StringComparison.Ordinal);

        // A card it cannot build refuses the proposal with its reason; no other card stands in.
        var refused = await resolver.ResolveApprovalInCulturesAsync(descriptor, JsonSerializer.SerializeToElement(new { agentId = AgentId, expectedVersion = 1, mcpServerIds = Enumerable.Range(0, 64).Select(index => $"server-{index}-{new string('s', 32)}").ToArray() }), context, english, CancellationToken.None);
        Assert.Null(refused.Presentation);
        Assert.Contains("'mcpServerIds'", refused.Refusal, StringComparison.Ordinal);

        // So does a presenter that times out, fails, builds nothing or a card beyond the limits.
        var stubs = new (RequiredStub Presenter, string Refusal)[]
        {
            (new RequiredStub(async token => { await Task.Delay(Timeout.Infinite, token); return new NhAssistantRequiredPresentation(null, null); }), NhAssistantRequiredPresentation.TimedOut),
            (new RequiredStub(_ => throw new InvalidOperationException("The directory is not there.")), NhAssistantRequiredPresentation.Failed),
            (new RequiredStub(_ => ValueTask.FromResult(new NhAssistantRequiredPresentation(null, null))), NhAssistantRequiredPresentation.Failed),
            (new RequiredStub(_ => ValueTask.FromResult(new NhAssistantRequiredPresentation(new NhAssistantApprovalPresentation("Change", new string('s', 600), []), null))), NhAssistantRequiredPresentation.Failed)
        };
        foreach (var (stub, expected) in stubs)
        {
            var outcome = await Resolver(stub, other, TimeSpan.FromMilliseconds(50)).ResolveApprovalInCulturesAsync(descriptor, JsonSerializer.SerializeToElement(new { agentId = AgentId }), context, english, CancellationToken.None);
            Assert.Equal((null, expected), (outcome.Presentation, outcome.Refusal));
        }
        Assert.Equal(0, other.Calls);

        // A tool whose proposal needs no card of its own keeps the other presenters.
        var read = new NhAssistantAgentBuilderToolCatalog().Descriptors.Single(item => item.Id == NhAssistantAgentBuilderTools.ReadAgent);
        Assert.NotNull((await Resolver(Presenter(), other).ResolveApprovalInCulturesAsync(read, JsonSerializer.SerializeToElement(new { agentId = AgentId }), context, english, CancellationToken.None)).Presentation);
    }

    [Fact]
    public async Task An_approved_write_reads_nothing_before_the_person_of_the_chat_is_known()
    {
        var directory = new CountingDirectory();
        await using var services = new ServiceCollection()
            .AddSingleton(new NhAssistantRegistrationState())
            .AddSingleton<AspNet.AgentRuns.INhAssistantAgentDirectory>(directory)
            .BuildServiceProvider();
        var handler = new NhAssistantAgentBuilderToolHandler(
            services,
            new NhAssistantRegistrationState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NhAssistantAgentBuilderToolHandler>.Instance);
        var promote = NhAssistantAgentBuilderTools.All.Single(item => item.Id == NhAssistantAgentBuilderTools.PromoteShadowVariant);

        var result = await handler.HandleAsync(
            promote,
            JsonSerializer.SerializeToElement(new { agentId = "planner-next", expectedVersion = 3, expectedVariantVersion = 2 }),
            new NhAiInvocationContext(Administrator, "assistant", new Dictionary<string, string>()),
            CancellationToken.None);

        // Outside a person's chat with the builder the call is refused before its card reads the variant.
        Assert.Equal(NhAssistantAdminErrorCodes.Forbidden, result.GetResultItems().First().Name);
        Assert.Equal(0, directory.Calls);
    }

    [Fact]
    public async Task What_an_agent_skill_or_consultation_may_do_is_never_shortened()
    {
        var presenter = Presenter();
        var create = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.CreateAgent);
        var selectors = Enumerable.Range(0, 10).Select(index => $"tools.{index}.{new string('t', 40)}").Append("*").ToArray();

        // Ten selectors followed by "*" show the "*", next to long other values that are shortened.
        var (card, refusal) = await BuildAsync(presenter, create, new
        {
            agentId = "new-agent",
            displayName = "New",
            description = new string('<', 512),
            instructions = "Help.",
            toolSelectors = selectors,
            autonomy = "execute",
            compositions = true,
            scripts = true,
            requiredPolicy = "app.helpers"
        });
        Assert.Null(refusal);
        Assert.Null(NhAssistantPresentationLimits.FindBrokenRule(card!));
        var tools = card!.Fields.Where(field => field.Label.StartsWith("Tools", StringComparison.Ordinal)).ToArray();
        Assert.Equal(string.Join(", ", selectors), string.Join(", ", tools.Select(field => field.Value)));
        Assert.EndsWith(", *", tools[^1].Value, StringComparison.Ordinal);
        Assert.Contains(card.Fields, field => field.Label == "What it may do" && field.Value == "Autonomy: execute; Compositions: yes; Scripts: yes");
        // Creating an agent with a required policy says whose rights it changes.
        Assert.Contains("whose rights agent new-agent works with (Required policy)", card.Notice, StringComparison.Ordinal);

        // A skill's tools and what a consultation offers are not shortened either.
        var skill = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.SaveSkill);
        var (skillCard, _) = await BuildAsync(presenter, skill, new { agentId = AgentId, skillId = "notes", description = new string('"', 512), tools = selectors });
        Assert.Equal(string.Join(", ", selectors), string.Join(", ", skillCard!.Fields.Where(field => field.Label.StartsWith("Tools", StringComparison.Ordinal)).Select(field => field.Value)));
        var consultation = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.SaveConsultation);
        var (consultationCard, _) = await BuildAsync(presenter, consultation, new { agentId = AgentId, subjectType = "project", description = new string('&', 400), toolSelectors = new[] { "projects.search" }, exposeToMcp = true });
        Assert.Contains(consultationCard!.Fields, field => field.Label == "Tools" && field.Value == "projects.search");
        Assert.Contains(consultationCard.Fields, field => field.Label == "Visible to MCP clients" && field.Value == "yes");

        // A metadata value shows as the JSON it is saved as, also an empty or padded text.
        var metadata = NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.SaveMetadata);
        foreach (var value in new[] { "", "  ", "  x  " })
        {
            var (entry, _) = await BuildAsync(presenter, metadata, new { agentId = AgentId, name = "team", value });
            Assert.Contains(entry!.Fields, field => field.Label == "Value" && field.Value == JsonSerializer.Serialize(value));
        }
    }

    [Theory]
    [InlineData(NhAssistantAgentBuilderTools.SaveSkill, """{"agentId":"a","skillId":"notes","description":"d","expectedVersion":"3"}""", "expectedVersion")]
    [InlineData(NhAssistantAgentBuilderTools.SaveTrigger, """{"agentId":"a","triggerId":"t","source":"schedule","expectedVersion":"3"}""", "expectedVersion")]
    [InlineData(NhAssistantAgentBuilderTools.SetLive, """{"agentId":"a","part":"rule-script","version":"1"}""", "version")]
    [InlineData(NhAssistantAgentBuilderTools.Remove, """{"agentId":"a","part":"skill-candidate","skillId":"s","version":"2"}""", "version")]
    [InlineData(NhAssistantAgentBuilderTools.SetShadow, """{"agentId":"a","part":"agent","shadow":"true"}""", "shadow")]
    [InlineData(NhAssistantAgentBuilderTools.SaveRunSettings, """{"agentId":"a","accountability":""}""", "accountability")]
    [InlineData(NhAssistantAgentBuilderTools.SaveRunSettings, """{"agentId":"a","retryCount":-1}""", "retryCount")]
    [InlineData(NhAssistantAgentBuilderTools.SaveRunSettings, """{"agentId":"a","serviceAccountActorId":"  "}""", "serviceAccountActorId")]
    [InlineData(NhAssistantAgentBuilderTools.SaveRunSettings, """{"agentId":"a","approvalPolicy":""}""", "approvalPolicy")]
    [InlineData(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":1,"requiredPolicy":"  "}""", "requiredPolicy")]
    [InlineData(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":1,"runPolicy":" "}""", "runPolicy")]
    [InlineData(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":1,"viewPolicy":"\t"}""", "viewPolicy")]
    [InlineData(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":1,"autonomy":"Execute"}""", "autonomy")]
    [InlineData(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":1,"toolSelectors":["projects.*",""]}""", "toolSelectors[1]")]
    [InlineData(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":1.5}""", "expectedVersion")]
    [InlineData(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":null}""", "expectedVersion")]
    [InlineData(NhAssistantAgentBuilderTools.SetTechnicalAccount, """{"agentId":"a","actorId":" "}""", "actorId")]
    [InlineData(NhAssistantAgentBuilderTools.SaveConsultation, """{"agentId":"a","subjectType":"project","askerPolicy":"  "}""", "askerPolicy")]
    [InlineData(NhAssistantAgentBuilderTools.SaveSkill, """{"agentId":"a","skillId":"notes","dataClassification":"secret"}""", "dataClassification")]
    [InlineData(NhAssistantAgentBuilderTools.SetMcpServer, """{"agentId":"a","serverId":"planning","assigned":true,"secret":"guess"}""", "secret")]
    [InlineData(NhAssistantAgentBuilderTools.SetTechnicalAccount, """{"agentId":"a"}""", "actorId")]
    [InlineData(NhAssistantAgentBuilderTools.SetTechnicalAccount, """{"agentId":null,"actorId":"service-1"}""", "agentId")]
    public void Input_that_the_card_and_the_handler_could_read_differently_is_refused_before_a_proposal(string toolId, string json, string property)
    {
        var tool = NhAssistantAgentBuilderTools.All.Single(item => item.Id == toolId);

        var (input, refusal) = NhAssistantAgentBuilderInput.Read(JsonSerializer.Deserialize<JsonElement>(json), tool.Input.Schema);

        Assert.Null(input);
        Assert.Contains(property, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_for_an_optional_property_is_left_out_and_a_whole_number_with_a_fraction_is_that_number()
    {
        Assert.Equal(1, Accepted(NhAssistantAgentBuilderTools.SetLive, """{"agentId":"a","part":"rule-script","version":1.0,"skillId":null,"expectedRevision":2e0,"reason":null}""", input =>
        {
            Assert.False(input.Given("skillId"));
            Assert.False(input.Given("reason"));
            Assert.Equal(2, input.Int("expectedRevision"));
            return input.Int("version");
        }));
        // Null for a setting that takes none keeps it, as leaving it out does, for the card and the handler.
        Assert.Equal(0, Accepted(NhAssistantAgentBuilderTools.SaveRunSettings, """{"agentId":"a","elevatedReads":null,"accountability":null,"delegateFromInitiator":null,"allowGatewayReads":null,"expectedVersion":null}""", input =>
            new[] { "elevatedReads", "accountability", "delegateFromInitiator", "allowGatewayReads", "expectedVersion" }.Count(input.Given)));
        Assert.False(Accepted(NhAssistantAgentBuilderTools.UpdateAgent, """{"agentId":"a","expectedVersion":1,"mcpServerIds":null}""", input => input.Given("mcpServerIds")));
        // Null where the schema allows it clears: a nullable choice is an anyOf of the choice and null,
        // without null in its enum.
        Assert.True(Accepted(NhAssistantAgentBuilderTools.SaveSkill, """{"agentId":"a","skillId":"notes","dataClassification":null}""", input => input.Given("dataClassification") && !input.Has("dataClassification")));
        var classification = (System.Text.Json.Nodes.JsonObject)NhAssistantAgentBuilderTools.All.Single(tool => tool.Id == NhAssistantAgentBuilderTools.SaveSkill)
            .Input.Schema["properties"]!["dataClassification"]!;
        Assert.Equal(2, classification["anyOf"]!.AsArray().Count);
        Assert.DoesNotContain("null", classification["anyOf"]![0]!["enum"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_card_shows_what_runs_for_a_change_that_clears_or_leaves_out_values()
    {
        var presenter = Presenter();

        // A version that is given is the version the handler changes, live at once.
        var skill = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveSkill, new { agentId = AgentId, skillId = "notes", expectedVersion = 3, description = "Writes notes." }, "en-US");
        Assert.Equal("Saves skill notes of agent project-assistant: live at once; fields it does not name stay as they are.", skill.Summary);
        Assert.Contains(skill.Fields, field => field.Label == "Version" && field.Value == "3");

        // A setting cleared with null says so; one left out is not on the card and stays as it is.
        var cleared = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new { agentId = AgentId, expectedVersion = 2, retryCount = (int?)null, approvalPolicy = (string?)null }, "en-US");
        Assert.Equal(
            [("Agent", AgentId), ("Version", "2"), ("Approval policy", "cleared (the default applies)"), ("Retries", "cleared (the default applies)")],
            cleared.Fields.Select(field => (field.Label, field.Value)));
        Assert.Contains("settings it does not name stay as they are", cleared.Summary, StringComparison.Ordinal);

        // More values than the card has fields share fields, and none is left out.
        var many = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveRunSettings, new
        {
            agentId = AgentId, expectedVersion = 2, accountability = "service-account", serviceAccountActorId = "service-1", delegateFromInitiator = true,
            allowGatewayReads = true, elevatedReads = "deny", approvalPolicy = "review", retryCount = 2, maxConcurrentRuns = 3, maxRunsPerSubjectPerDay = 4,
            triageProfile = "chat", ruleHandler = "rules", alertKinds = new[] { "weekly-count" }, acceptsFiles = false
        }, "en-US");
        Assert.True(many.Fields.Count <= NhAssistantPresentationLimits.MaxFields);
        var shown = string.Join(" | ", many.Fields.Select(field => field.Label + ": " + field.Value));
        foreach (var expected in new[] { "Elevated reads: deny", "Rights of whoever starts a run: yes", "Alert kinds: weekly-count", "Rule handler: rules", "Accepts files: no" })
        {
            Assert.Contains(expected, shown, StringComparison.Ordinal);
        }

        // A hand-off's change keeps what it leaves out, as the card says.
        var handOff = await PresentAsync(presenter, NhAssistantAgentBuilderTools.SaveHandOff, new { agentId = AgentId, subjectType = "project", expectedVersion = 1, maxPerSubjectPerDay = 2 }, "en-US");
        Assert.EndsWith("fields it does not name stay as they are.", handOff.Summary, StringComparison.Ordinal);
    }

    private static T Accepted<T>(string toolId, string json, Func<NhAssistantAgentBuilderInput, T> read)
    {
        var tool = NhAssistantAgentBuilderTools.All.Single(item => item.Id == toolId);
        var (input, refusal) = NhAssistantAgentBuilderInput.Read(JsonSerializer.Deserialize<JsonElement>(json), tool.Input.Schema);
        Assert.True(input is not null, refusal);
        return read(input!);
    }

    private static NhAssistantAgentBuilderInput Read(NhAssistantAgentBuilderTool tool, object value)
    {
        var (input, refusal) = NhAssistantAgentBuilderInput.Read(JsonSerializer.SerializeToElement(value), tool.Input.Schema);
        Assert.True(input is not null, refusal);
        return input!;
    }

    private static Task<(NhAssistantApprovalPresentation? Card, string? Refusal)> BuildAsync(NhAssistantAgentBuilderPresenter presenter, NhAssistantAgentBuilderTool tool, object value)
    {
        return presenter.BuildAsync(tool, Read(tool, value), dutch: false, CancellationToken.None);
    }

    private static NhAssistantAgentBuilderPresenter Presenter(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        return new NhAssistantAgentBuilderPresenter(new NhAssistantRegistrationState(), services.BuildServiceProvider());
    }

    /// <summary>The longest input a tool's schema allows: every property at its largest value.</summary>
    private static JsonElement LongestInput(NhAssistantAgentBuilderTool tool)
    {
        var input = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, schema) in (System.Text.Json.Nodes.JsonObject)tool.Input.Schema["properties"]!)
        {
            input[name] = LongestValue((System.Text.Json.Nodes.JsonObject)schema!);
        }

        return JsonSerializer.SerializeToElement(input);
    }

    private static System.Text.Json.Nodes.JsonNode? LongestValue(System.Text.Json.Nodes.JsonObject schema)
    {
        if (schema["anyOf"] is System.Text.Json.Nodes.JsonArray branches)
        {
            return LongestValue((System.Text.Json.Nodes.JsonObject)branches[0]!);
        }

        var type = schema["type"] is System.Text.Json.Nodes.JsonArray types ? types[0]!.GetValue<string>() : schema["type"]?.GetValue<string>();
        return type switch
        {
            "string" when schema["enum"] is System.Text.Json.Nodes.JsonArray choices => choices[0]!.DeepClone(),
            "string" => new string('x', int.Parse(schema["maxLength"]!.ToJsonString(), CultureInfo.InvariantCulture)),
            "integer" => int.MaxValue,
            "boolean" => true,
            "array" when schema["items"] is System.Text.Json.Nodes.JsonObject { } items && items["type"]!.GetValue<string>() == "string" =>
                new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, int.Parse(schema["maxItems"]!.ToJsonString(), CultureInfo.InvariantCulture))
                    .Select(_ => (System.Text.Json.Nodes.JsonNode?)new string('y', int.Parse(items["maxLength"]!.ToJsonString(), CultureInfo.InvariantCulture)))
                    .ToArray()),
            "array" => new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "project", ["id"] = new string('z', 256) }),
            "object" => new System.Text.Json.Nodes.JsonObject { ["entry"] = new string('v', 600) },
            _ => new System.Text.Json.Nodes.JsonObject { ["any"] = new string('w', 600) }
        };
    }

    private static NhAssistantToolPresentationResolver Resolver(INhAssistantToolPresenter required, INhAssistantToolPresenter other, TimeSpan? timeout = null)
    {
        return new NhAssistantToolPresentationResolver(
            [required, other],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NhAssistantToolPresentationResolver>.Instance,
            new TwoCultures(),
            timeout);
    }

    private sealed class TwoCultures : INhAssistantPresentationCultures
    {
        public IReadOnlyList<System.Globalization.CultureInfo> GetCultures()
        {
            return [System.Globalization.CultureInfo.GetCultureInfo("en-US"), System.Globalization.CultureInfo.GetCultureInfo("nl-NL")];
        }
    }

    /// <summary>A presenter of the application that would present any tool, and counts when it is asked.</summary>
    private sealed class OtherPresenter : INhAssistantToolPresenter
    {
        public int Calls { get; private set; }

        public ValueTask<string?> GetDisplayNameAsync(NhAssistantToolDisplayNameContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(NhAssistantApprovalPresentationContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(new NhAssistantApprovalPresentation("Other", "Another card.", []));
        }
    }

    /// <summary>A presenter the builder's writes need, with a scripted answer.</summary>
    private sealed class RequiredStub(Func<CancellationToken, ValueTask<NhAssistantRequiredPresentation>> answer) : INhAssistantToolPresenter, INhAssistantRequiredToolPresenter
    {
        public bool Requires(NhAiToolDescriptor descriptor)
        {
            return NhAssistantAgentBuilderIds.IsAgentBuilderTool(descriptor.Id);
        }

        public ValueTask<NhAssistantRequiredPresentation> PresentRequiredAsync(NhAssistantApprovalPresentationContext context, CancellationToken cancellationToken)
        {
            return answer(cancellationToken);
        }

        public ValueTask<string?> GetDisplayNameAsync(NhAssistantToolDisplayNameContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(NhAssistantApprovalPresentationContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(null);
        }
    }

    /// <summary>A directory that knows no agent and counts its reads.</summary>
    private sealed class CountingDirectory : AspNet.AgentRuns.INhAssistantAgentDirectory
    {
        public int Calls { get; private set; }

        public Task<AspNet.AgentRuns.NhAssistantAgentInEffectDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<AspNet.AgentRuns.NhAssistantAgentInEffectDto?>(null);
        }

        public Task<IReadOnlyList<AspNet.AgentRuns.NhAssistantAgentInEffectDto>> ListAsync(
            AspNet.AgentRuns.NhAssistantAgentDirectoryFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<AspNet.AgentRuns.NhAssistantAgentInEffectDto>>([]);
        }
    }

    /// <summary>A directory that knows the agents a promotion's card reads: the variant and its live agent.</summary>
    private sealed class KnownAgents(params AspNet.AgentRuns.NhAssistantAgentInEffectDto[] agents) : AspNet.AgentRuns.INhAssistantAgentDirectory
    {
        public Task<AspNet.AgentRuns.NhAssistantAgentInEffectDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(agents.FirstOrDefault(agent => agent.AgentId == agentId));
        }

        public Task<IReadOnlyList<AspNet.AgentRuns.NhAssistantAgentInEffectDto>> ListAsync(
            AspNet.AgentRuns.NhAssistantAgentDirectoryFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AspNet.AgentRuns.NhAssistantAgentInEffectDto>>(agents);
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
