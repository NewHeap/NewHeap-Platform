using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB7 (CS9): a question or approval that passed its lifetime no longer keeps a chat busy. A new
/// message closes it as expired, exactly as a late answer or decision does (the same codes and audit,
/// the waiting turn released), and then starts its own turn; while it is still valid the message is
/// refused as busy, and reading the chat changes nothing. Proved with a controllable application clock
/// on both relational providers.
/// </summary>
public sealed class AssistantExpiredWaitTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Question = "Which status should the launch project get?";

    private static readonly string[] Choices = ["Active", "On hold", "Closed"];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_message_closes_a_question_that_passed_its_lifetime_and_starts_its_turn(AssistantTestProvider provider)
    {
        var model = new SwitchingChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices }, "call-ask"));
        var clock = NewClock();
        await using var host = await CreateHostAsync(provider, model, clock);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        var asked = (await host.SendAsync(conversation.Id, "Change the status of the launch project."))
            .Single<NhAssistantQuestionAskedEvent>().Question;

        clock.Advance(new NhAssistantLimits().ApprovalLifetime);
        // Reading the chat shows the question as expired and writes nothing.
        var read = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantQuestionStatuses.Expired, read.PendingQuestion?.Status);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(conversation.Id)).Status);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionExpired);

        // The new turn reads the chat's history, not the closed call: a script of its own answers it.
        var answering = new NhAiScriptedChatClient().RespondWithText("Then let us plan the next release.");
        model.Current = answering;
        var next = await host.SendAsync(conversation.Id, "Never mind, plan the next release.");

        Assert.True(next.Start.Success);
        var completed = next.Single<NhAssistantTurnCompletedEvent>();
        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), (completed.Status, completed.ErrorCode));
        Assert.Equal("Then let us plan the next release.", next.Text);
        Assert.Single(answering.Requests);
        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, view.Status);
        Assert.Null(view.PendingQuestion);
        Assert.Equal(NhAssistantQuestionStatuses.Expired, view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantQuestionView>().Single().Status);
        var expired = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionExpired);
        Assert.Equal(
            (asked.QuestionId, AssistantTestHost.UserId, NhAssistantErrorCodes.QuestionExpired, (string?)null),
            (expired.InvocationId, expired.ActorId, expired.ResultCode, expired.Channel));
        // The late answer finds it closed, as after an answer that came too late.
        Assert.Equal(NhAssistantErrorCodes.QuestionNotPending, (await host.AnswerAsync(conversation.Id, asked.QuestionId, 0)).StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_message_closes_an_approval_that_passed_its_lifetime_and_starts_its_turn(AssistantTestProvider provider)
    {
        var model = new SwitchingChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } }));
        var clock = NewClock();
        await using var host = await CreateHostAsync(provider, model, clock);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await host.SendAsync(conversation.Id, "Activate the launch project.");
        var approval = (await host.ReadViewAsync(conversation.Id)).PendingApproval!;

        clock.Advance(new NhAssistantLimits().ApprovalLifetime);
        model.Current = new NhAiScriptedChatClient().RespondWithText("Hello again.");
        var next = await host.SendAsync(conversation.Id, "Hello?");

        Assert.True(next.Start.Success);
        var completed = next.Single<NhAssistantTurnCompletedEvent>();
        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), (completed.Status, completed.ErrorCode));
        Assert.Empty(host.Tools.StatusChanges);
        var store = host.Services.GetRequiredService<INhAssistantStore>();
        var stored = await store.FindApprovalAsync(approval.ApprovalId, CancellationToken.None);
        Assert.Equal((NhAssistantApprovalStatuses.Expired, (string?)null), (stored!.Status, stored.DecidedByActorId));
        var call = await store.FindToolInvocationAsync(stored.ToolInvocationId, CancellationToken.None);
        Assert.Equal((NhAssistantToolCallStatuses.Failed, NhAssistantErrorCodes.ApprovalExpired), (call!.Status, call.ResultCode));
        var expired = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalExpired);
        Assert.Equal(
            (approval.ApprovalId, stored.TurnId, AssistantTestHost.UserId, NhAssistantErrorCodes.ApprovalExpired),
            (expired.ApprovalId, expired.TurnId, expired.ActorId, expired.ResultCode));
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
        // The late decision is refused: the approval closed.
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotPending, (await host.DecideAsync(conversation.Id, approval, approve: true)).StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_message_while_the_question_or_the_approval_is_still_valid_is_refused_as_busy(AssistantTestProvider provider)
    {
        var askingModel = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices }, "call-ask");
        var approvingModel = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } });
        var model = new SwitchingChatClient(askingModel);
        var clock = NewClock();
        await using var host = await CreateHostAsync(provider, model, clock);
        var asking = await host.CreateConversationAsync(agentId: AgentId);
        var approving = await host.CreateConversationAsync(agentId: AgentId);
        await host.SendAsync(asking.Id, "Change the status of the launch project.");
        model.Current = approvingModel;
        await host.SendAsync(approving.Id, "Activate the launch project.");

        // One minute before the end of the wait both still hold their chats.
        clock.Advance(new NhAssistantLimits().ApprovalLifetime - TimeSpan.FromMinutes(1));

        Assert.Equal(NhAssistantErrorCodes.ConversationBusy, (await host.SendAsync(asking.Id, "Hello?")).StartCode);
        Assert.Equal(NhAssistantErrorCodes.ConversationBusy, (await host.SendAsync(approving.Id, "Hello?")).StartCode);
        Assert.Equal(NhAssistantQuestionStatuses.Pending, (await host.ReadViewAsync(asking.Id)).PendingQuestion?.Status);
        Assert.NotNull((await host.ReadViewAsync(approving.Id)).PendingApproval);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind is NhAssistantAuditEventKind.QuestionExpired or NhAssistantAuditEventKind.ApprovalExpired);
        // Neither refused message reached the model.
        Assert.Equal((1, 1), (askingModel.Requests.Count, approvingModel.Requests.Count));
    }

    private static AssistantTestClock NewClock()
    {
        return new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        IChatClient model,
        TimeProvider clock)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services.AddSingleton(clock),
            assistantBuilder: assistant => assistant.ConfigureAgent(AgentId, agent => agent.AllowQuestions()));
    }
}
