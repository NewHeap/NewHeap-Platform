using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Persistence;
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
        var found = Assert.Single(FunctionResult(model, 1).GetProperty("data").GetProperty("accounts").EnumerateArray());
        Assert.Equal(KnownAccountPrincipalFactory.First, found.GetProperty("actorId").GetString());

        // The change of whose rights the agent uses waits for the approval, on a card that says so.
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        var presentation = approval.GetProperty("presentation");
        Assert.Matches("whose rights agent weekly-count works with|met wiens rechten agent weekly-count werkt", presentation.GetProperty("notice").GetString());
        var account = presentation.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("label").GetString() is "Technical account" or "Technisch account");
        Assert.Equal(KnownAccountPrincipalFactory.First, account.GetProperty("value").GetString());
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
        Assert.Matches("Changes only whether|Wijzigt alleen of", headlessCard.GetProperty("presentation").GetProperty("summary").GetString());

        // It changes only whether runs without a person use the server: what another administrator changed in
        // the screens between the card and the approval stays, as do the connection and the secret.
        using (var renamed = await administrator.PutAsync("/api/assistant/admin/mcp-servers/planning", Json(new
        {
            id = "planning",
            displayName = "Planning (renamed)",
            url = "http://localhost/planning/mcp",
            authMode = "bearer",
            isEnabled = false
        })))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }
        await DecideAsync(administrator, conversation, headlessCard, "approve");
        using var servers = await administrator.GetAsync("/api/assistant/admin/mcp-servers");
        var stored = Assert.Single((await ReadJsonAsync(servers)).EnumerateArray());
        Assert.Equal(
            ("organization", true, "Planning (renamed)", false, "bearer"),
            (stored.GetProperty("headlessUse").GetString(), stored.GetProperty("hasSecret").GetBoolean(), stored.GetProperty("displayName").GetString(), stored.GetProperty("isEnabled").GetBoolean(), stored.GetProperty("authMode").GetString()));

        // The write holds to the authentication the value was checked against: after another change of it,
        // nothing is written.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NhAssistantAdminStore>();
            Assert.False(await store.SetMcpHeadlessUseAsync("planning", "never", "none", "organization", CancellationToken.None));
            Assert.Equal("organization", (await store.FindMcpServerAsync("planning", CancellationToken.None))!.HeadlessUse);
            Assert.True(await store.SetMcpHeadlessUseAsync("planning", "organization", "bearer", "organization", CancellationToken.None));
        }
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
            // FB93: the builder lists the kind in the run settings, after the approval.
            .RespondWithFunctionCall("builder_save_run_settings", new { input = new { agentId = Weekly, expectedVersion = 1, alertKinds = new[] { "weekly-count" } } })
            .RespondWithFunctionCall("builder_read_agent", new { input = new { agentId = Weekly } })
            .RespondWithText("The weekly count may raise its alert now.")
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

        // The builder lists the kind after the approval, on a card that names it; the blocker is gone then.
        var listing = await SendAsync(administrator, conversation, "List the weekly count's alert kind.");
        var listCard = Assert.Single(listing, evt => evt.Name == "approval.required").Data;
        Assert.Contains(
            listCard.GetProperty("presentation").GetProperty("fields").EnumerateArray(),
            field => field.GetProperty("label").GetString() is "Alert kinds" or "Soorten waarschuwingen" && field.GetProperty("value").GetString() == "weekly-count");
        await DecideAsync(administrator, conversation, listCard, "approve");
        Assert.Equal(["weekly-count"], (await ConfigurationAsync(app)).RunSettings!.AlertKinds);
        var after = FunctionResult(model, 4).GetProperty("data");
        Assert.Equal("weekly-count", after.GetProperty("configuration").GetProperty("runSettings").GetProperty("alertKinds")[0].GetString());
        Assert.DoesNotContain(after.GetProperty("blockers").EnumerateArray(), blocker => blocker.GetProperty("code").GetString() == "alert-kind-not-listed");

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

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_refuses_input_its_card_could_read_otherwise_and_a_change_keeps_what_it_leaves_out(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("builder_update_agent", new { input = new { agentId = Weekly, expectedVersion = 1, requiredPolicy = "   " } })
            .RespondWithFunctionCall("builder_update_agent", new { input = new { agentId = Weekly, expectedVersion = 1, mcpServerIds = Enumerable.Range(0, 10).Select(index => $"server-{index}-{new string('s', 32)}").ToArray() } })
            .RespondWithFunctionCall("builder_save_skill", new { input = new { agentId = Weekly, skillId = "notes", expectedVersion = "1", description = "Writes short notes." } })
            .RespondWithFunctionCall("builder_save_skill", new { input = new { agentId = Weekly, skillId = "notes", expectedVersion = 1, description = "Writes short notes." } })
            .RespondWithText("Only the description changed.")
            .RespondWithFunctionCall("builder_save_trigger", new { input = new { agentId = Weekly, triggerId = "monday", expectedVersion = 1, source = "schedule", message = "Count the week before." } })
            .RespondWithText("Only the message changed.");
        await using var app = await StartAsync(model, provider);
        await CreateWeeklyAsync(app, runSettings: true);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
            var skill = await configuration.SaveSkillAsync(Weekly, "notes", new NhAssistantDataSkillRequest("Writes notes.", Instructions: "Write one note per change.", Tools: ["projects.*"]), Admin);
            Assert.True(skill.Success, string.Join(", ", skill.AllErrorMessages));
            var trigger = await configuration.SaveTriggerAsync(Weekly, "monday", new NhAssistantDataTriggerRequest("schedule", Cron: "0 6 * * 1", TimeZone: "Europe/Amsterdam", Message: "Count last week."), Admin);
            Assert.True(trigger.Success, string.Join(", ", trigger.AllErrorMessages));
        }
        using var administrator = app.CreateClient(Admin, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        // Input the card and the change could read differently, or that its card cannot show in full, is
        // refused before anything is proposed: a blank policy, more MCP servers than the card shows, and a
        // number as a string.
        var asked = await SendAsync(administrator, conversation, "Give the notes skill a shorter description.");
        var refused = asked.Where(evt => evt.Name == "tool.completed").Select(evt => evt.Data).ToArray();
        Assert.Equal(3, refused.Length);
        Assert.All(refused, result => Assert.Equal(("failed", NhAiToolFailureCodes.InputInvalid), (result.GetProperty("status").GetString(), result.GetProperty("resultCode").GetString())));
        foreach (var (index, property) in new[] { (0, "requiredPolicy"), (1, "mcpServerIds"), (2, "expectedVersion") })
        {
            Assert.Contains(property, FunctionResult(model, index).GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal(1, (await AgentAsync(app)).Version);
        Assert.Empty((await AgentAsync(app)).McpServerIds);

        // The card shows the version and the value that change; what the call leaves out stays.
        var approval = Assert.Single(asked, evt => evt.Name == "approval.required").Data;
        var presentation = approval.GetProperty("presentation");
        Assert.Matches("fields it does not name stay as they are|velden die niet genoemd worden blijven zoals ze zijn", presentation.GetProperty("summary").GetString());
        var shown = presentation.GetProperty("fields").EnumerateArray()
            .ToDictionary(field => field.GetProperty("label").GetString()!, field => field.GetProperty("value").GetString());
        Assert.Equal(("1", "Writes short notes."), (shown.GetValueOrDefault("Version") ?? shown["Versie"], shown.GetValueOrDefault("Description") ?? shown["Omschrijving"]));
        await DecideAsync(administrator, conversation, approval, "approve");
        var skillNow = Assert.Single((await ConfigurationAsync(app)).DataSkills, skill => skill.SkillId == "notes");
        Assert.Equal(
            (2, "Writes short notes.", "Write one note per change.", "projects.*"),
            (skillNow.Version, skillNow.Description, skillNow.Instructions, string.Join(",", skillNow.Tools)));

        // So does a trigger's change: its schedule and time zone stay.
        var again = await SendAsync(administrator, conversation, "Let the Monday trigger count the week before.");
        await DecideAsync(administrator, conversation, Assert.Single(again, evt => evt.Name == "approval.required").Data, "approve");
        var triggerNow = Assert.Single((await ConfigurationAsync(app)).DataTriggers, trigger => trigger.TriggerId == "monday");
        Assert.Equal(
            (2, "0 6 * * 1", "Europe/Amsterdam", "Count the week before."),
            (triggerNow.Version, triggerNow.Cron, triggerNow.TimeZone, triggerNow.Message));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_change_of_a_hand_off_keeps_what_it_leaves_out_and_null_and_whole_numbers_read_as_meant(AssistantTestProvider provider)
    {
        const string Target = "project-assistant";
        var model = new NhAiScriptedChatClient()
            // Models often send null for what they leave out and a version as 1.0.
            .RespondWithFunctionCall("builder_save_hand_off", new { input = new { agentId = Target, subjectType = "project", expectedVersion = 1.0, maxPerSubjectPerDay = 2, reason = (string?)null } })
            .RespondWithText("Only the daily limit changed.");
        await using var app = await StartAsync(
            model,
            provider,
            configure: services => services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator))),
            more: assistant => assistant
                .ConfigureAgent(Target, _ => { })
                .UseHandOffsAsData(handOffs => handOffs.Allow("project", rule => rule.To(Target).FromAgentsCreatedInAdministration())));
        await CreateWeeklyAsync(app, runSettings: true);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<INhAssistantDataHandOffs>().SaveAsync(
                Target,
                "project",
                new NhAssistantDataHandOffRequest([Weekly], "Hands over projects that need a count.", NotifySource: true, MaxPerSubjectPerDay: 3),
                Admin);
            Assert.True(saved.Success, string.Join(", ", saved.AllErrorMessages));
        }
        using var administrator = app.CreateClient(Admin, admin: true);
        var conversation = await CreateConversationAsync(administrator, Builder);

        var asked = await SendAsync(administrator, conversation, "Let the weekly count hand over at most two projects a day.");
        var card = Assert.Single(asked, evt => evt.Name == "approval.required").Data.GetProperty("presentation");
        var shown = card.GetProperty("fields").EnumerateArray()
            .ToDictionary(field => field.GetProperty("label").GetString()!, field => field.GetProperty("value").GetString());
        Assert.Equal("1", shown.GetValueOrDefault("Version") ?? shown["Versie"]);
        Assert.DoesNotContain(shown.Keys, label => label is "Reason" or "Reden" or "Sources" or "Bronnen");
        await DecideAsync(administrator, conversation, Assert.Single(asked, evt => evt.Name == "approval.required").Data, "approve");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var handOff = Assert.Single((await scope.ServiceProvider.GetRequiredService<INhAssistantDataHandOffs>().GetAsync(Target))!.DataHandOffs);
            Assert.Equal(
                (2, "Hands over projects that need a count.", true, 2, Weekly),
                (handOff.Version, handOff.Description, handOff.NotifySource, handOff.MaxPerSubjectPerDay, string.Join(",", handOff.Sources)));
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_builder_gets_no_mcp_server_and_its_turns_load_none(AssistantTestProvider provider)
    {
        const string PlanningUrl = "http://localhost/planning/mcp";
        var servers = new InMemoryMcpServers();
        servers.Register(PlanningUrl, () => servers.PlanningTools());
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I build agents.")
            .RespondWithText("I can look up the planning.");
        await using var app = await StartAsync(model, provider, mcp: servers);
        using var administrator = app.CreateClient(Admin, admin: true);
        using (var created = await administrator.PostAsync("/api/assistant/admin/mcp-servers", Json(new { id = "planning", displayName = "Planning", url = PlanningUrl, authMode = "none", isEnabled = true })))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        using (var synced = await administrator.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", Json(new { })))
        {
            Assert.Equal(HttpStatusCode.OK, synced.StatusCode);
        }
        using (var enabled = await administrator.PutAsync("/api/assistant/admin/mcp-servers/planning/tools/Lookup_Order", Json(new { isEnabled = true, effect = "read-only" })))
        {
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        }
        using (var assigned = await administrator.PutAsync("/api/assistant/admin/mcp-servers/planning/agents/project-assistant", Json(new { })))
        {
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        }

        // The administration does not assign a server to the builder.
        using (var builder = await administrator.PutAsync($"/api/assistant/admin/mcp-servers/planning/agents/{Builder}", Json(new { })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, builder.StatusCode);
            var body = await builder.Content.ReadAsStringAsync();
            Assert.Contains("agentId", body, StringComparison.Ordinal);
            Assert.Contains(NhAssistantFieldErrors.NotAllowed, body, StringComparison.Ordinal);
        }

        // An assignment that is stored all the same, such as one from before, gives its turns no MCP tools and
        // opens no connection.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(Builder, includeDisabled: true, CancellationToken.None));
            var stored = await scope.ServiceProvider.GetRequiredService<NhAssistantAdminStore>().SetMcpAssignmentAsync(Builder, "planning", true, 8, CancellationToken.None);
            Assert.True(stored.Value);
        }
        var connections = servers.Connections.Count;
        var conversation = await CreateConversationAsync(administrator, Builder);
        await SendAsync(administrator, conversation, "Which tools do you have?");
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
        Assert.Equal(connections, servers.Connections.Count);

        // The same server serves another agent it is assigned to.
        var other = await CreateConversationAsync(administrator, "project-assistant");
        await SendAsync(administrator, other, "Which tools do you have?");
        Assert.Contains(model.Requests[^1].Options!.Tools!, tool => tool.Name.StartsWith("mcp_planning_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_builder_is_offered_only_where_the_screens_let_the_person_in_also_with_a_policy_that_decides_by_resource()
    {
        // An administration policy that passes only for a resource: the screens check it without one.
        await using var app = await StartAsync(
            new NhAiScriptedChatClient(),
            AssistantTestProvider.PostgreSql,
            configure: services => services.PostConfigure<AuthorizationOptions>(options => options.AddPolicy(
                "app.assistant.admin",
                policy => policy.RequireAssertion(context => context.Resource is not null && context.User.HasClaim("permission", "assistant-admin")))));
        using var administrator = app.CreateClient(Admin, admin: true);

        using (var screens = await administrator.GetAsync("/api/assistant/admin/agents"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, screens.StatusCode);
        }
        using (var refused = await administrator.PostAsync("/api/assistant/conversations", Json(new { agentId = Builder })))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // Nor does a check with a resource, as a tool's gate makes it, let the person use the builder.
        var person = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Admin), new Claim("permission", "assistant"), new Claim("permission", "assistant-admin")],
            "test"));
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        Assert.True((await authorization.AuthorizeAsync(person, new object(), "app.assistant.admin")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(person, new object(), NhAssistantAgentBuilderIds.Policy)).Succeeded);
    }

    private Task<AssistantWebApplication> StartAsync(
        NhAiScriptedChatClient model,
        AssistantTestProvider provider,
        bool gateway = false,
        InMemoryMcpServers? mcp = null,
        Action<IServiceCollection>? configure = null,
        Func<NhAssistantBuilder, NhAssistantBuilder>? more = null)
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
                if (mcp is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(mcp));
                }
                configure?.Invoke(services);
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
                .AddAlertKind("weekly-count", kind => kind
                    .DisplayName("nh-assistant.alert-kinds.weekly-count.name")
                    .Urgency(NhAssistantAlertUrgency.Warning)
                    .Title((_, _) => "Many changes last week")
                    .Text((_, _) => "More changes than the threshold.")
                    .Recipients(NhAssistantAlertRecipients.SubjectResponsible))
                .UseRuleScriptsAsData(scripts => scripts.ForAgentsCreatedInAdministration())
                .Apply(more),
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

/// <summary>Lets a test add to the assistant's registration in the builder chain.</summary>
internal static class AssistantBuilderTestExtensions
{
    public static NhAssistantBuilder Apply(this NhAssistantBuilder builder, Func<NhAssistantBuilder, NhAssistantBuilder>? more)
    {
        return more is null ? builder : more(builder);
    }
}