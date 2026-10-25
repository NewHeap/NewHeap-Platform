using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Structured questions in a person's chat (LS21): <c>run.AllowQuestions()</c> offers
/// <c>ask_question</c>; the turn waits for the answer as for an approval, only the addressee answers,
/// the turn resumes with the answer as the call's result, and a question expires with the turn's wait
/// limit on the application's clock. Proved on both relational providers.
/// </summary>
public sealed class AssistantQuestionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string PlainAgentId = "plain-assistant";
    private const string Member = "member-1";
    private const string MemberName = "Mia";
    private const string Question = "Which status should the launch project get?";

    private static readonly string[] Choices = ["Active", "On hold", "Closed"];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_question_waits_for_its_answer_and_the_turn_resumes_with_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices }, "call-ask")
            .RespondWithText("I will put the launch project on hold.");
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, model, clock);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var asking = await host.SendAsync(conversation.Id, "Change the status of the launch project.");

        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, asking.Single<NhAssistantTurnCompletedEvent>().Status);
        var asked = asking.Single<NhAssistantQuestionAskedEvent>().Question;
        Assert.Equal(Question, asked.Question);
        Assert.Equal(Choices, asked.Choices);
        Assert.Equal(AssistantTestHost.UserId, asked.AddresseeActorId);
        Assert.Equal(NhAssistantQuestionStatuses.Pending, asked.Status);
        Assert.Equal(clock.GetUtcNow().Add(new NhAssistantLimits().ApprovalLifetime), asked.ExpiresAt);
        Assert.Contains("ask_question", ToolNames(model.Requests[0]));
        var waiting = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, waiting.Status);
        Assert.Null(waiting.PendingApproval);
        Assert.Equal(asked.QuestionId, waiting.PendingQuestion?.QuestionId);
        var call = Assert.Single(waiting.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>());
        Assert.Equal(NhAssistantToolCallStatuses.AwaitingAnswer, call.Status);

        var answered = await host.AnswerAsync(conversation.Id, asked.QuestionId, 1);

        Assert.True(answered.Start.Success);
        Assert.Equal(1, answered.Single<NhAssistantQuestionAnsweredEvent>().Question.AnswerIndex);
        Assert.Equal(NhAssistantTurnStatuses.Completed, answered.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Contains("on hold", answered.Text, StringComparison.Ordinal);
        // The model reads the answer as the result of its call.
        var result = model.Requests[1].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single(content => content.CallId == "call-ask");
        var answer = JsonSerializer.SerializeToElement(result.Result);
        Assert.Equal("On hold", answer.GetProperty("answer").GetString());
        Assert.Equal(1, answer.GetProperty("choice").GetInt32());

        var after = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, after.Status);
        Assert.Null(after.PendingQuestion);
        var question = Assert.Single(after.Messages.SelectMany(message => message.Parts).OfType<NhAssistantQuestionView>());
        Assert.Equal(NhAssistantQuestionStatuses.Answered, question.Status);
        Assert.Equal(1, question.AnswerIndex);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionAsked
            && evt.InvocationId == asked.QuestionId
            && evt.ObjectId == AssistantTestHost.UserId);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionAnswered
            && evt.InvocationId == asked.QuestionId
            && evt.ActorId == AssistantTestHost.UserId);

        // An answered question takes no second answer.
        var again = await host.AnswerAsync(conversation.Id, asked.QuestionId, 0);
        Assert.Equal(NhAssistantErrorCodes.QuestionNotPending, again.StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_the_addressee_answers_and_the_turn_continues_with_their_rights(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices, addressee = MemberName }, "call-ask")
            .RespondWithText("Mia chose to close it.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await AddParticipantAsync(host, conversation.Id, Member, MemberName);

        var asking = await host.SendAsync(conversation.Id, "Ask Mia what happens to the launch project.");
        var asked = asking.Single<NhAssistantQuestionAskedEvent>().Question;
        Assert.Equal(Member, asked.AddresseeActorId);
        Assert.Contains(MemberName, model.Requests[0].Options!.Tools!.Single(tool => tool.Name == "ask_question").Description);

        // The person whose turn asked may not answer for the addressee.
        var refused = await host.AnswerAsync(conversation.Id, asked.QuestionId, 2);
        Assert.Equal(NhAssistantErrorCodes.QuestionForbidden, refused.StartCode);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(conversation.Id)).Status);
        var invalid = await host.AnswerAsync(conversation.Id, asked.QuestionId, 3, Member);
        Assert.Equal(NhAssistantErrorCodes.QuestionAnswerInvalid, invalid.StartCode);

        var answered = await host.AnswerAsync(conversation.Id, asked.QuestionId, 2, Member);

        Assert.Equal(NhAssistantTurnStatuses.Completed, answered.Single<NhAssistantTurnCompletedEvent>().Status);
        // The model reads who answered; the rest of the turn is the turn of the person who answered.
        var answer = JsonSerializer.SerializeToElement(model.Requests[1].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single(content => content.CallId == "call-ask")
            .Result);
        Assert.Equal("Closed", answer.GetProperty("answer").GetString());
        Assert.Equal(MemberName, answer.GetProperty("answeredBy").GetString());
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionAnswered && evt.ActorId == Member);
        var view = await host.ReadViewAsync(conversation.Id, Member);
        Assert.Equal(2, Assert.Single(view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantQuestionView>()).AnswerIndex);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_question_expires_with_the_turns_wait_limit_and_releases_the_conversation(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices }, "call-ask");
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(provider, model, clock);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        var asked = (await host.SendAsync(conversation.Id, "Change the status of the launch project."))
            .Single<NhAssistantQuestionAskedEvent>().Question;

        clock.Advance(new NhAssistantLimits().ApprovalLifetime);
        Assert.Equal(NhAssistantQuestionStatuses.Expired, (await host.ReadViewAsync(conversation.Id)).PendingQuestion?.Status);
        var late = await host.AnswerAsync(conversation.Id, asked.QuestionId, 0);

        Assert.Equal(NhAssistantErrorCodes.QuestionExpired, late.StartCode);
        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, view.Status);
        Assert.Null(view.PendingQuestion);
        Assert.Equal(NhAssistantQuestionStatuses.Expired, Assert.Single(view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantQuestionView>()).Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionExpired && evt.InvocationId == asked.QuestionId);
        Assert.Equal(NhAssistantErrorCodes.QuestionNotPending, (await host.AnswerAsync(conversation.Id, asked.QuestionId, 0)).StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Stopping_the_waiting_turn_cancels_its_question(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices }, "call-ask");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        var asked = (await host.SendAsync(conversation.Id, "Change the status of the launch project."))
            .Single<NhAssistantQuestionAskedEvent>().Question;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var cancelled = await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>()
                .CancelAsync(await host.FindAccessAsync(conversation.Id), CancellationToken.None);
            Assert.True(cancelled.Success);
        }

        var view = await host.ReadViewAsync(conversation.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, view.Status);
        Assert.Equal(NhAssistantQuestionStatuses.Cancelled, Assert.Single(view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantQuestionView>()).Status);
        Assert.Equal(NhAssistantErrorCodes.QuestionNotPending, (await host.AnswerAsync(conversation.Id, asked.QuestionId, 0)).StartCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_opt_in_no_question_is_offered_and_a_question_out_of_bounds_is_not_posted(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I can help with projects.")
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = new[] { "Active" } }, "call-ask")
            .RespondWithText("Which status do you want?");
        await using var host = await CreateHostAsync(provider, model);
        var plain = await host.CreateConversationAsync(agentId: PlainAgentId);
        var opted = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(plain.Id, "What can you do?");
        var single = await host.SendAsync(opted.Id, "Change the status of the launch project.");

        Assert.DoesNotContain("ask_question", ToolNames(model.Requests[0]));
        Assert.Contains("ask_question", ToolNames(model.Requests[1]));
        // One choice is out of bounds: nothing waits, and the model reads why.
        Assert.Empty(single.Events.OfType<NhAssistantQuestionAskedEvent>());
        Assert.Equal(NhAssistantTurnStatuses.Completed, single.Single<NhAssistantTurnCompletedEvent>().Status);
        var refusal = JsonSerializer.SerializeToElement(model.Requests[2].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single(content => content.CallId == "call-ask")
            .Result);
        Assert.Equal(NhAssistantErrorCodes.QuestionInvalid, refusal.GetProperty("code").GetString());
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(opted.Id)).Status);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        TimeProvider? clock = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(PlainAgentId) with { ProfileName = "project-chat" })
                .ConfigureAgent(AgentId, agent => agent.AllowQuestions()));
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

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }
}
