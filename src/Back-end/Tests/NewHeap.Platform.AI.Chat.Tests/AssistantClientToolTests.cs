using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Client tools (<c>AddClientTool</c>): the application declares them on the server, a person's chat turn
/// offers them only when the person's browser declared their handler, the call waits for that browser as a
/// question waits for its answer, and only that person's report resumes the turn with the report as the
/// call's result. A wait ends on the application's clock; a stop cancels it. Proved on both relational
/// providers.
/// </summary>
public sealed class AssistantClientToolTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string OpenToolId = "projects.open-in-app";
    private const string OpenFunction = "projects_open_in_app_v1";
    private const string CreateTaskToolId = "projects.create-task-in-app";
    private const string CreateTaskFunction = "projects_create_task_in_app_v1";
    private const string ManagedToolId = "projects.manage-in-app";
    private const string ManagedFunction = "projects_manage_in_app_v1";
    private const string ArchiveToolId = "projects.archive-in-app";
    private const string ArchiveFunction = "projects_archive_in_app_v1";
    private const string ProposingAgentId = "proposing-assistant";
    private const string ClientOnlyAgentId = "client-only-assistant";
    private const string Member = "member-1";

    private static readonly Guid ProjectId = Guid.Parse("7f3c2a90-4d1e-4c55-9a51-0b8f3c6d2e11");

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_client_tool_waits_for_the_browser_and_the_turn_resumes_with_its_report(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(CreateTaskFunction, new { input = new { projectId = ProjectId, title = "Book the venue", ignored = "x" } }, "call-create")
            .RespondWithText("The task is saved.");
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, model, clock);
        var conversation = await host.CreateConversationAsync();

        var asking = await host.SendWithClientToolsAsync(conversation.Id, "Create a task to book the venue.", [CreateTaskToolId, OpenToolId, "unknown.tool"]);

        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, asking.Single<NhAssistantTurnCompletedEvent>().Status);
        var requested = asking.Single<NhAssistantClientToolRequestedEvent>();
        Assert.Equal(CreateTaskToolId, requested.ClientTool.ToolId);
        Assert.Equal(AssistantTestHost.UserId, requested.ClientTool.ActorId);
        Assert.Equal(NhAssistantClientToolStatuses.Pending, requested.ClientTool.Status);
        Assert.Equal(clock.GetUtcNow().Add(new NhAssistantLimits().ClientToolLifetime), requested.ClientTool.ExpiresAt);
        // The browser receives the arguments bound to the tool's input, never the model's other text.
        var arguments = requested.Arguments!.Value;
        Assert.Equal(ProjectId, arguments.GetProperty("projectId").GetGuid());
        Assert.Equal("Book the venue", arguments.GetProperty("title").GetString());
        Assert.False(arguments.TryGetProperty("ignored", out _));
        // The application's description, with the platform's guidance on hand-offs, is what the model reads.
        var offered = model.Requests[0].Options!.Tools!.Single(tool => tool.Name == CreateTaskFunction);
        Assert.Contains("prefer it over proposing the same change", offered.Description, StringComparison.Ordinal);
        Assert.Contains(OpenFunction, ToolNames(model.Requests[0]));

        var waiting = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, waiting.Status);
        Assert.Equal(requested.ClientTool.InvocationId, waiting.PendingClientTool?.InvocationId);
        Assert.Null(waiting.PendingQuestion);
        var call = Assert.Single(waiting.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>());
        Assert.Equal(NhAssistantToolCallStatuses.AwaitingClient, call.Status);

        var reported = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed, "saved", "task-42");

        Assert.True(reported.Start.Success);
        Assert.Equal(NhAssistantTurnStatuses.Completed, reported.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Contains("saved", reported.Text, StringComparison.Ordinal);
        var result = ResultOf(model, 1, "call-create");
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(NhAssistantClientTools.CompletedCode, result.GetProperty("code").GetString());
        Assert.Equal("saved", result.GetProperty("data").GetProperty("outcome").GetString());
        Assert.Equal("task-42", result.GetProperty("data").GetProperty("id").GetString());

        var after = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, after.Status);
        Assert.Null(after.PendingClientTool);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, Assert.Single(after.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>()).Status);
        // The audit names the tool, the call and the person; never the arguments or what was reported.
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ClientToolRequested
            && evt.InvocationId == requested.ClientTool.InvocationId
            && evt.ToolId == CreateTaskToolId
            && evt.ObjectId == AssistantTestHost.UserId);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ClientToolCompleted
            && evt.ResultCode == NhAssistantClientToolStatuses.Completed);
        Assert.DoesNotContain(host.Business.Events, evt => JsonSerializer.Serialize(evt).Contains("Book the venue", StringComparison.Ordinal)
            || JsonSerializer.Serialize(evt).Contains("task-42", StringComparison.Ordinal));
        // The shared invoker authorized and audited the call as a read.
        Assert.Contains(host.Audit.Records, record => record.ToolId == CreateTaskToolId);

        // A report that came already takes no second one.
        var again = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed);
        Assert.Equal(NhAssistantErrorCodes.ClientToolNotPending, again.StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_declined_client_tool_reaches_the_model_and_the_call_did_not_run(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(OpenFunction, new { input = new { projectId = ProjectId } }, "call-open")
            .RespondWithText("You chose not to open it.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();
        var requested = (await host.SendWithClientToolsAsync(conversation.Id, "Open the launch project.", [OpenToolId]))
            .Single<NhAssistantClientToolRequestedEvent>();

        var declined = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Declined);

        Assert.Equal(NhAssistantTurnStatuses.Completed, declined.Single<NhAssistantTurnCompletedEvent>().Status);
        var result = ResultOf(model, 1, "call-open");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(NhAssistantErrorCodes.ClientToolDeclined, result.GetProperty("code").GetString());
        var call = Assert.Single((await host.ReadViewAsync(conversation.Id)).Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>());
        Assert.Equal(NhAssistantToolCallStatuses.NotExecuted, call.Status);
        Assert.Equal(NhAssistantErrorCodes.ClientToolDeclined, call.ResultCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_client_tool_expires_on_the_application_clock_and_releases_the_conversation(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(OpenFunction, new { input = new { projectId = ProjectId } }, "call-open");
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, model, clock, limits => limits.ClientToolLifetime = TimeSpan.FromMinutes(5));
        var conversation = await host.CreateConversationAsync();
        var requested = (await host.SendWithClientToolsAsync(conversation.Id, "Open the launch project.", [OpenToolId]))
            .Single<NhAssistantClientToolRequestedEvent>();

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(NhAssistantClientToolStatuses.Expired, (await host.ReadViewAsync(conversation.Id)).PendingClientTool?.Status);
        var late = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed, "navigated");

        Assert.Equal(NhAssistantErrorCodes.ClientToolExpired, late.StartCode);
        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, view.Status);
        Assert.Null(view.PendingClientTool);
        var call = Assert.Single(view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>());
        Assert.Equal(NhAssistantToolCallStatuses.Failed, call.Status);
        Assert.Equal(NhAssistantErrorCodes.ClientToolExpired, call.ResultCode);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ClientToolExpired && evt.InvocationId == requested.ClientTool.InvocationId);
        Assert.Equal(NhAssistantErrorCodes.ClientToolNotPending, (await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed)).StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_the_person_whose_turn_called_it_reports_a_bounded_result(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(CreateTaskFunction, new { input = new { projectId = ProjectId, title = "Book the venue" } }, "call-create")
            .RespondWithText("Noted.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();
        await AddParticipantAsync(host, conversation.Id, Member, "Mia");
        var requested = (await host.SendWithClientToolsAsync(conversation.Id, "Create a task to book the venue.", [CreateTaskToolId]))
            .Single<NhAssistantClientToolRequestedEvent>();

        // Another person of the conversation sees it waits, but cannot report for the person's browser.
        var other = await host.ReadViewAsync(conversation.Id, Member);
        Assert.Equal(AssistantTestHost.UserId, other.PendingClientTool?.ActorId);
        var forbidden = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed, "saved", userId: Member);
        Assert.Equal(NhAssistantErrorCodes.ClientToolForbidden, forbidden.StartCode);
        // Someone outside the conversation does not even find it.
        Assert.Equal(
            NhAssistantErrorCodes.ConversationNotFound,
            (await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed, userId: "stranger")).StartCode);
        Assert.Equal(
            NhAssistantErrorCodes.ClientToolNotFound,
            (await host.ReportClientToolAsync(conversation.Id, Guid.NewGuid(), NhAssistantClientToolStatuses.Completed)).StartCode);

        // What the browser reports is bounded: a known status, a dash-case outcome, a plain id and data
        // only where the tool declares it.
        var invalid = new (string? Status, string? Outcome, string? Id, JsonElement? Data)[]
        {
            ("done", null, null, null),
            (NhAssistantClientToolStatuses.Completed, "Saved it!", null, null),
            (NhAssistantClientToolStatuses.Completed, "saved", "id with spaces", null),
            (NhAssistantClientToolStatuses.Completed, "saved", new string('a', 65), null),
            (NhAssistantClientToolStatuses.Completed, "saved", null, JsonSerializer.SerializeToElement(new { taskId = "t-1" }))
        };
        foreach (var (status, outcome, id, data) in invalid)
        {
            var refused = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, status, outcome, id, data);
            Assert.Equal(NhAssistantErrorCodes.ClientToolResultInvalid, refused.StartCode);
        }
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(conversation.Id)).Status);

        var reported = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed, "cancelled");
        Assert.Equal(NhAssistantTurnStatuses.Completed, reported.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Equal("cancelled", ResultOf(model, 1, "call-create").GetProperty("data").GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Data_the_tool_declares_is_bound_and_undeclared_properties_are_dropped(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ManagedFunction, new { input = new { projectId = ProjectId } }, "call-manage")
            .RespondWithText("Done.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();
        var requested = (await host.SendWithClientToolsAsync(conversation.Id, "Manage the launch project.", [ManagedToolId]))
            .Single<NhAssistantClientToolRequestedEvent>();

        var notBound = await host.ReportClientToolAsync(
            conversation.Id,
            requested.ClientTool.InvocationId,
            NhAssistantClientToolStatuses.Completed,
            "saved",
            data: JsonSerializer.SerializeToElement(new { taskId = 42 }));
        Assert.Equal(NhAssistantErrorCodes.ClientToolResultInvalid, notBound.StartCode);

        await host.ReportClientToolAsync(
            conversation.Id,
            requested.ClientTool.InvocationId,
            NhAssistantClientToolStatuses.Completed,
            "saved",
            data: JsonSerializer.SerializeToElement(new { taskId = "t-1", secret = "never" }));

        var result = ResultOf(model, 1, "call-manage").GetProperty("data").GetProperty("result");
        Assert.Equal("t-1", result.GetProperty("taskId").GetString());
        Assert.False(result.TryGetProperty("secret", out _));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_browsers_handler_the_tool_is_not_offered_and_a_call_is_not_available_at_once(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I can help with projects.")
            .RespondWithFunctionCall(OpenFunction, new { input = new { projectId = ProjectId } }, "call-open")
            .RespondWithText("Open it from the project list.");
        await using var host = await CreateHostAsync(provider, model);
        var plain = await host.CreateConversationAsync();
        var clientOnly = await host.CreateConversationAsync(agentId: ClientOnlyAgentId);

        await host.SendAsync(plain.Id, "What can you do?");
        // An agent whose selectors select only the client tool falls back to its selectors when the turn
        // offers nothing, so its model sees the tool; the call still never waits for a browser.
        var fallback = await host.SendAsync(clientOnly.Id, "Open the launch project.");

        Assert.DoesNotContain(OpenFunction, ToolNames(model.Requests[0]));
        Assert.Empty(fallback.Events.OfType<NhAssistantClientToolRequestedEvent>());
        Assert.Equal(NhAssistantTurnStatuses.Completed, fallback.Single<NhAssistantTurnCompletedEvent>().Status);
        var unavailable = ResultOf(model, 2, "call-open");
        Assert.Equal(NhAssistantErrorCodes.ClientToolUnavailable, unavailable.GetProperty("code").GetString());
        var call = Assert.Single((await host.ReadViewAsync(clientOnly.Id)).Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>());
        Assert.Equal(NhAssistantToolCallStatuses.NotExecuted, call.Status);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(clientOnly.Id)).Status);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ClientToolRequested);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_hand_off_is_offered_only_where_the_agent_may_change_data_and_the_person_may_use_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I can only propose.")
            .RespondWithFunctionCall(ArchiveFunction, new { input = new { projectId = ProjectId } }, "call-archive")
            .RespondWithText("You may not archive it.");
        await using var host = await CreateHostAsync(provider, model);
        var proposing = await host.CreateConversationAsync(agentId: ProposingAgentId);
        var executing = await host.CreateConversationAsync();

        await host.SendWithClientToolsAsync(proposing.Id, "Create a task.", [CreateTaskToolId, OpenToolId]);
        var denied = await host.SendWithClientToolsAsync(executing.Id, "Archive the launch project.", [ArchiveToolId]);

        // A propose agent is offered the page but not the hand-off of a change.
        Assert.Contains(OpenFunction, ToolNames(model.Requests[0]));
        Assert.DoesNotContain(CreateTaskFunction, ToolNames(model.Requests[0]));
        // The invocation gate checks the person's policy before anything reaches the browser.
        Assert.Empty(denied.Events.OfType<NhAssistantClientToolRequestedEvent>());
        Assert.Equal(NhAssistantTurnStatuses.Completed, denied.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.False(ResultOf(model, 2, "call-archive").GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Stopping_the_waiting_turn_cancels_its_client_tool(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(OpenFunction, new { input = new { projectId = ProjectId } }, "call-open");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();
        var requested = (await host.SendWithClientToolsAsync(conversation.Id, "Open the launch project.", [OpenToolId]))
            .Single<NhAssistantClientToolRequestedEvent>();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var cancelled = await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>()
                .CancelAsync(await host.FindAccessAsync(conversation.Id), CancellationToken.None);
            Assert.True(cancelled.Success);
        }

        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, view.Status);
        var call = Assert.Single(view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>());
        Assert.Equal(NhAssistantToolCallStatuses.NotExecuted, call.Status);
        Assert.Equal(NhAssistantErrorCodes.ClientToolCancelled, call.ResultCode);
        Assert.Equal(NhAssistantErrorCodes.ClientToolNotPending, (await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed)).StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_client_tool_counts_against_the_tool_calls_of_its_turn(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(OpenFunction, new { input = new { projectId = ProjectId } }, "call-open")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "launch", limit = 5 } }, "call-1")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "launch", limit = 5 } }, "call-2")
            .RespondWithText("I searched once before the limit.");
        await using var host = await CreateHostAsync(provider, model, limits: limits => limits.MaxToolCallsPerTurn = 2);
        var conversation = await host.CreateConversationAsync();
        var requested = (await host.SendWithClientToolsAsync(conversation.Id, "Open and search.", [OpenToolId]))
            .Single<NhAssistantClientToolRequestedEvent>();

        var reported = await host.ReportClientToolAsync(conversation.Id, requested.ClientTool.InvocationId, NhAssistantClientToolStatuses.Completed, "navigated");

        // The client tool and one search used both calls: the report brought no new ones.
        Assert.Equal(NhAssistantErrorCodes.ToolCallLimitReached, reported.Single<NhAssistantTurnCompletedEvent>().ErrorCode);
        Assert.Single(host.Tools.Contexts);
    }

    [Fact]
    public void The_client_tool_event_carries_its_arguments_only_on_the_persons_own_stream()
    {
        var view = new NhAssistantClientToolView(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            OpenToolId,
            AssistantTestHost.UserId,
            new DateTimeOffset(2031, 5, 6, 7, 23, 0, TimeSpan.Zero),
            NhAssistantClientToolStatuses.Pending);
        var arguments = JsonSerializer.SerializeToElement(new { projectId = ProjectId });

        var (ownName, ownData) = NhAssistantServerSentEventsResult.Serialize(new NhAssistantClientToolRequestedEvent(view) { Arguments = arguments });
        var (sharedName, sharedData) = NhAssistantServerSentEventsResult.Serialize(new NhAssistantClientToolRequestedEvent(view));

        Assert.Equal("client-tool.requested", ownName);
        Assert.Equal("client-tool.requested", sharedName);
        using var own = JsonDocument.Parse(ownData);
        Assert.Equal(
            ["invocationId", "toolId", "actorId", "expiresAt", "status", "arguments"],
            own.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(ProjectId, own.RootElement.GetProperty("arguments").GetProperty("projectId").GetGuid());
        using var shared = JsonDocument.Parse(sharedData);
        Assert.Equal(
            ["invocationId", "toolId", "actorId", "expiresAt", "status"],
            shared.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void A_client_tool_is_declared_by_exact_id_with_a_description()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddNewHeapAssistant(assistant => assistant
            .AddClientTool<OpenProjectInput>("projects.*", tool => tool.Describe("Opens a project."))));
        Assert.Throws<InvalidOperationException>(() => services.AddNewHeapAssistant(assistant => assistant
            .AddClientTool<OpenProjectInput>("projects.open", _ => { })));
        Assert.Throws<ArgumentException>(() => services.AddNewHeapAssistant(assistant => assistant
            .AddClientTool<OpenProjectInput>("projects.open", tool => tool.Describe(new string('d', NhAssistantClientToolLimits.MaxDescriptionLength + 1)))));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        TimeProvider? clock = null,
        Action<NhAssistantLimits>? limits = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            limits,
            configure: services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(ProposingAgentId, autonomy: NhAiAutonomyLevel.Propose))
                .AddAgent(AssistantTestData.Agent(ClientOnlyAgentId) with { ToolSelectors = [OpenToolId] })
                .AddClientTool<OpenProjectInput>(OpenToolId, tool => tool
                    .Describe("Opens the page of a project in the application.")
                    .RequirePolicy(TestProjectToolCatalog.ReadPolicy))
                .AddClientTool<CreateTaskInput>(CreateTaskToolId, tool => tool
                    .Describe("Opens the application's form to create a task of a project, prefilled.")
                    .RequirePolicy(TestProjectToolCatalog.ManagePolicy)
                    .HandsOffChange())
                .AddClientTool<OpenProjectInput>(ManagedToolId, tool => tool
                    .Describe("Opens the form to manage a project.")
                    .RequirePolicy(TestProjectToolCatalog.ManagePolicy)
                    .HandsOffChange()
                    .Returns<ManagedResult>())
                // The test user has no claim of this policy.
                .AddClientTool<OpenProjectInput>(ArchiveToolId, tool => tool
                    .Describe("Opens the form to archive a project.")
                    .RequirePolicy("app.project.manage")
                    .HandsOffChange()));
    }

    private static async Task AddParticipantAsync(AssistantTestHost host, Guid conversationId, string actorId, string displayName)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Participants.Add(new AssistantConversationParticipant
        {
            ConversationId = conversationId,
            ActorId = actorId,
            DisplayName = displayName,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static JsonElement ResultOf(NhAiScriptedChatClient model, int request, string callId)
    {
        return JsonSerializer.SerializeToElement(model.Requests[request].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single(content => content.CallId == callId)
            .Result);
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    public sealed record OpenProjectInput(Guid ProjectId);

    public sealed record CreateTaskInput(Guid ProjectId, string Title);

    public sealed record ManagedResult(string TaskId);
}
