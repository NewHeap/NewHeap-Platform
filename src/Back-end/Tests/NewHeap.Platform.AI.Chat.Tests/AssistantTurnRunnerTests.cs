using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantTurnRunnerTests(AssistantDatabaseFixture database)
{
    private const string SearchFunction = "projects_search_v1";
    private const string ChangeStatusFunction = "projects_change_status_v1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_read_only_turn_streams_one_governed_tool_call_and_completes(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "roadmap" } }, "call-search")
            .RespondWithText("I found one roadmap project.");
        await using var host = await AssistantTestHost.CreateAsync(database, provider, model);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Which roadmap projects exist?");

        Assert.True(turn.Start.Success);
        Assert.IsType<NhAssistantTurnStartedEvent>(turn.Events[0]);
        var started = turn.Single<NhAssistantToolStartedEvent>();
        var completed = turn.Single<NhAssistantToolCompletedEvent>();
        var end = Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]);
        Assert.Equal("projects.search", started.ToolId);
        Assert.Equal(started.InvocationId, completed.InvocationId);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, completed.Status);
        Assert.Contains("Roadmap project", completed.ResultPreview);
        Assert.Equal("I found one roadmap project.", turn.Text);
        Assert.Equal(NhAssistantTurnStatuses.Completed, end.Status);
        Assert.Equal(1, end.Usage.ToolCalls);
        Assert.True(end.Usage.InputTokens > 0);
        Assert.Null(end.ErrorCode);
        Assert.Equal("call-search", Assert.Single(model.FunctionResults).CallId);

        var context = Assert.Single(host.Tools.Contexts);
        Assert.Equal("assistant-agent:project-assistant", context.ActorId);
        Assert.Equal(NhAiActorKind.Agent, context.ActorKind);
        Assert.Equal(AssistantTestHost.UserId, context.AccountableOwnerId);
        Assert.Equal("assistant", context.Purpose);
        Assert.Equal(((NhAssistantTurnStartedEvent)turn.Events[0]).TurnId.ToString(), context.RunId);

        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, view.Status);
        Assert.Equal("Which roadmap projects exist?", view.Title);
        Assert.Equal([NhAssistantMessageRoles.User, NhAssistantMessageRoles.Assistant], view.Messages.Select(message => message.Role));
        var assistantParts = view.Messages[1].Parts;
        Assert.IsType<NhAssistantToolCallPartView>(assistantParts[0]);
        Assert.Equal("I found one roadmap project.", Assert.IsType<NhAssistantTextPartView>(assistantParts[1]).Text);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_mutation_pauses_for_approval_and_resumes_after_approve(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithText("The project is now active.");
        await using var host = await AssistantTestHost.CreateAsync(database, provider, model);
        var conversation = await host.CreateConversationAsync();

        var paused = await host.SendAsync(conversation.Id, "Activate the project.");

        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        var pausedEnd = Assert.IsType<NhAssistantTurnCompletedEvent>(paused.Events[^1]);
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, pausedEnd.Status);
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Empty(paused.Events.OfType<NhAssistantToolCompletedEvent>());
        Assert.Equal("projects.change-status", approval.ToolId);
        Assert.Equal(NhAssistantApprovalStatuses.Pending, approval.Status);
        Assert.Equal(64, approval.ProposalHash.Length);
        Assert.Contains(projectId.ToString(), approval.ArgumentsPreview);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(conversation.Id)).Status);
        var busy = await host.SendAsync(conversation.Id, "Hello?");
        Assert.Equal(NhAssistantErrorCodes.ConversationBusy, busy.StartCode);

        var resumed = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.True(resumed.Start.Success);
        var completed = resumed.Single<NhAssistantToolCompletedEvent>();
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, completed.Status);
        Assert.Equal("The project is now active.", resumed.Text);
        var end = Assert.IsType<NhAssistantTurnCompletedEvent>(resumed.Events[^1]);
        Assert.Equal(NhAssistantTurnStatuses.Completed, end.Status);
        Assert.Equal(pausedEnd.TurnId, end.TurnId);
        var change = Assert.Single(host.Tools.StatusChanges);
        Assert.Equal(new ProjectStatusInput(projectId, "Active"), change);
        var context = Assert.Single(host.Tools.Contexts);
        Assert.Equal(approval.ProposalId.ToString(), context.ProposalId);
        Assert.Equal(approval.ApprovalId.ToString(), context.ApprovalId);
        Assert.Equal("call-status", Assert.Single(model.FunctionResults).CallId);
        Assert.Contains(host.Audit.Records, record => record.ApprovalCode == "approved" && record.Outcome == NhAiOutcomeKind.Succeeded);

        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, view.Status);
        Assert.Null(view.PendingApproval);
        var approvalPart = view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantApprovalView>().Single();
        Assert.Equal(NhAssistantApprovalStatuses.Approved, approvalPart.Status);
        var toolPart = view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>().Single();
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, toolPart.Status);

        var replay = await host.DecideAsync(conversation.Id, approval, approve: true);
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotPending, replay.StartCode);
        Assert.Single(host.Tools.StatusChanges);
    }

    [Fact]
    public async Task A_rejected_approval_lets_the_model_close_without_executing_the_tool()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Closed" } }, "call-close")
            .RespondWithText("Understood, I left the project unchanged.");
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();
        var paused = await host.SendAsync(conversation.Id, "Close the project.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;

        var closed = await host.DecideAsync(conversation.Id, approval, approve: false);

        Assert.Equal("Understood, I left the project unchanged.", closed.Text);
        Assert.Equal(NhAssistantTurnStatuses.Completed, Assert.IsType<NhAssistantTurnCompletedEvent>(closed.Events[^1]).Status);
        Assert.Empty(host.Tools.StatusChanges);
        var result = Assert.Single(model.FunctionResults);
        Assert.Contains("assistant-approval-rejected", JsonSerializer.Serialize(result.Result));
        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(
            NhAssistantToolCallStatuses.Rejected,
            view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>().Single().Status);
        Assert.Equal(
            NhAssistantApprovalStatuses.Rejected,
            view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantApprovalView>().Single().Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRejected);
    }

    [Fact]
    public async Task An_expired_proposal_ends_the_decision_with_a_stable_error_code()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } });
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();
        var paused = await host.SendAsync(conversation.Id, "Activate the project.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.Approvals
                .Where(item => item.Id == approval.ApprovalId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        var decision = await host.DecideAsync(conversation.Id, approval, approve: true);

        var error = Assert.IsType<NhAssistantErrorEvent>(decision.Events[^1]);
        Assert.Equal(NhAssistantErrorCodes.ApprovalExpired, error.Code);
        Assert.Equal("nh-assistant.errors.assistant-approval-expired", error.MessageKey);
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalExpired);
    }

    [Fact]
    public async Task An_exhausted_daily_budget_ends_the_turn_with_an_error()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "first" } })
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "second" } });
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            limits => limits.DailyToolCallBudgetPerActor = 1);
        var conversation = await host.CreateConversationAsync();

        var exhausted = await host.SendAsync(conversation.Id, "Search twice.");
        var next = await host.SendAsync(conversation.Id, "Search again.");

        Assert.Equal(NhAssistantErrorCodes.BudgetExhausted, Assert.IsType<NhAssistantErrorEvent>(exhausted.Events[^1]).Code);
        Assert.Single(host.Tools.Contexts);
        Assert.Equal(NhAssistantErrorCodes.BudgetExhausted, Assert.IsType<NhAssistantErrorEvent>(next.Events[^1]).Code);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
    }

    [Fact]
    public async Task Exceeding_the_tool_calls_per_turn_ends_the_turn_gracefully()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "one" } })
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "two" } });
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            limits => limits.MaxToolCallsPerTurn = 1);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Keep searching.");

        var end = Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]);
        Assert.Equal(NhAssistantTurnStatuses.Completed, end.Status);
        Assert.Equal(NhAssistantErrorCodes.ToolCallLimitReached, end.ErrorCode);
        Assert.Single(host.Tools.Contexts);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
    }

    [Fact]
    public async Task Cancel_stops_a_running_turn_and_releases_the_conversation()
    {
        var model = new BlockingChatClient();
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.StartWithoutWaitingAsync(conversation.Id, "Think for a long time.", async runner =>
        {
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await runner.CancelAsync(conversation.Id, AssistantTestHost.UserId, CancellationToken.None);
        });

        var end = Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]);
        Assert.Equal(NhAssistantTurnStatuses.Cancelled, end.Status);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
    }

    [Fact]
    public async Task Approval_is_bound_to_the_expected_proposal_hash_and_never_to_the_agent()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } });
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();
        var paused = await host.SendAsync(conversation.Id, "Activate the project.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;

        var wrongHash = await host.DecideAsync(conversation.Id, approval, approve: true, expectedHash: new string('0', 64));
        var otherUser = await host.DecideAsync(conversation.Id, approval, approve: true, userId: "user-2");
        var agentConversation = await host.CreateConversationAsync("assistant-agent:project-assistant");
        var agentDecision = await host.DecideAsync(
            agentConversation.Id,
            approval,
            approve: true,
            userId: "assistant-agent:project-assistant");

        Assert.Equal(NhAssistantErrorCodes.ProposalHashMismatch, wrongHash.StartCode);
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, otherUser.StartCode);
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, agentDecision.StartCode);
        Assert.Empty(host.Tools.StatusChanges);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(conversation.Id)).Status);
    }

    [Fact]
    public async Task No_prompt_argument_or_result_content_reaches_audit_usage_business_sinks_or_logs()
    {
        const string secretPrompt = "SECRET-PROMPT-7f3a";
        const string secretArgument = "SECRET-ARGUMENT-91c2";
        const string secretResult = "SECRET-RESULT-5b8d";
        const string secretAnswer = "SECRET-ANSWER-2e6f";
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = secretArgument } })
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = secretArgument } })
            .RespondWithText(secretAnswer);
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        host.Tools.SearchResultName = secretResult;
        var conversation = await host.CreateConversationAsync();

        var paused = await host.SendAsync(conversation.Id, secretPrompt);
        await host.DecideAsync(conversation.Id, paused.Single<NhAssistantApprovalRequiredEvent>().Approval, approve: true);

        Assert.NotEmpty(host.Audit.Records);
        Assert.NotEmpty(host.Usage.Records);
        Assert.NotEmpty(host.Business.Events);
        var captured = string.Join(
            "\n",
            host.Audit.Records.Select(record => JsonSerializer.Serialize(record))
                .Concat(host.Usage.Records.Select(record => JsonSerializer.Serialize(record)))
                .Concat(host.Business.Events.Select(evt => JsonSerializer.Serialize(evt)))
                .Concat(host.Logs.Messages));
        Assert.DoesNotContain(secretPrompt, captured);
        Assert.DoesNotContain(secretArgument, captured);
        Assert.DoesNotContain(secretResult, captured);
        Assert.DoesNotContain(secretAnswer, captured);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked && evt.ResultCode == "succeeded");
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved);
    }

    [Fact]
    public async Task The_accountable_owner_comes_from_the_registered_context_resolver()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } })
            .RespondWithText("Done.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            configure: services => services.AddNewHeapPlatformAIAspNet(ai => ai
                .UseAuthenticatedInvocationContextResolver<OwnerSettingContextResolver>()));
        var conversation = await host.CreateConversationAsync();

        var paused = await host.SendAsync(conversation.Id, "Activate the project.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        await host.DecideAsync(conversation.Id, approval, approve: true);

        var context = Assert.Single(host.Tools.Contexts);
        Assert.Equal(OwnerSettingContextResolver.AccountableOwner, context.AccountableOwnerId);
        Assert.Null(context.TenantId);
        await using var storage = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var stored = await storage.Approvals.SingleAsync(item => item.Id == approval.ApprovalId);
        Assert.Equal(
            OwnerSettingContextResolver.AccountableOwner,
            NhAssistantProposalSerializer.Deserialize(stored.ProposalJson).AccountableOwnerId);
        Assert.Single(host.Tools.StatusChanges);
    }

    private sealed class OwnerSettingContextResolver : NewHeap.Platform.AI.AspNet.INhAiAuthenticatedInvocationContextResolver
    {
        public const string AccountableOwner = "accountable-owner-1";

        public ValueTask<NewHeap.Platform.Common.Models.TaskResult<NhAiInvocationContext>> ResolveAsync(
            Microsoft.AspNetCore.Http.HttpContext httpContext,
            CancellationToken cancellationToken = default)
        {
            var actorId = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;
            return ValueTask.FromResult(NewHeap.Platform.Common.Models.TaskResult<NhAiInvocationContext>.Succeeded(
                new NhAiInvocationContext(actorId, "project-assistance", new Dictionary<string, string>())
                {
                    AccountableOwnerId = AccountableOwner
                }));
        }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
