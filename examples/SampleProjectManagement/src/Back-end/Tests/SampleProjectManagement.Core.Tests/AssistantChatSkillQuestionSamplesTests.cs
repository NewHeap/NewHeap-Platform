using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for people's chats with agents that have run settings:
/// SPM-333, the planner offers its skills in the chat (<c>SkillsInChat</c>): a chat turn starts
/// without a skill, the model loads the task follow-up skill with <c>use_skill</c>, without triage,
/// and continues with the skill's instructions and only its tools, with the person's own rights;
/// the planner's message of the turn names the skill.
/// SPM-334, the status advisor asks a structured question (<c>AllowQuestions</c>): the turn waits for
/// the answer as for an approval, a colleague in the shared chat sees the question but is refused when
/// they answer, and the person it asks answers through
/// <c>POST conversations/{id}/questions/{questionId}/answer</c>, after which the turn continues with the
/// answer as the result of the advisor's call.
/// SPM-343, the chat helper works in people's chats only (<c>ChatOnly()</c>): it offers its meeting
/// notes skill and questions in the chat, but it is on no project's page and a start on a project is
/// refused with <c>assistant-agent-does-not-work-on</c>; the administration says it is chat-only, and its
/// skill always works live.
/// </summary>
public sealed class AssistantChatSkillQuestionSamplesTests(AssistantAgentRunSampleHost host)
    : IClassFixture<AssistantAgentRunSampleHost>
{
    private const string Question = "Which status should the launch project get?";

    [Fact]
    public async Task SPM_333_a_person_chats_with_the_planner_whose_model_loads_a_skill_with_its_own_tools()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(
                "use_skill",
                new { skill = SampleProjectPlannerComposition.TaskFollowUpSkill, reason = "The person asks which task comes next." },
                "call-skill")
            .RespondWithText("The design review comes next.");
        host.Model.Use(model);
        using var client = host.CreateClient(host.OwnerId.ToString());
        var conversationId = await CreateConversationAsync(client, SampleProjectPlannerComposition.AgentId);

        var events = await host.SendAsync(client, conversationId, "Which task of the launch project comes next?");

        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains(events, evt => evt.Name == "tool.started" && evt.Data.GetProperty("toolId").GetString() == "assistant.use-skill");
        // No triage: the planner's model answers twice, the second time in the skill.
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("use_skill", ToolNames(model.Requests[0]));
        var inSkill = model.Requests[1];
        Assert.Contains("## Skill: " + SampleProjectPlannerComposition.TaskFollowUpSkill, inSkill.Options!.Instructions);
        // The follow-up skill selects only the status tool; use_skill still offers the deadline risk skill.
        Assert.Equal(["projects_change_status_v1", "use_skill"], ToolNames(inSkill).Order(StringComparer.Ordinal).ToArray());

        var conversation = await host.GetJsonAsync(client, $"/api/assistant/conversations/{conversationId}");
        var answer = conversation.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("assistant", answer.GetProperty("role").GetString());
        Assert.Equal(SampleProjectPlannerComposition.TaskFollowUpSkill, answer.GetProperty("skillId").GetString());
    }

    [Fact]
    public async Task SPM_334_the_status_advisor_asks_which_status_and_only_the_person_it_asks_answers()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = new[] { "Active", "On hold", "Closed" } }, "call-ask")
            .RespondWithText("I will put the launch project on hold.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        var conversationId = await CreateConversationAsync(owner, SampleStatusAdvisorComposition.AgentId);
        await ShareAsync(owner, colleague, conversationId);

        var asking = await host.SendAsync(owner, conversationId, "Change the status of the launch project.");

        Assert.Equal("waiting-for-approval", asking[^1].Data.GetProperty("status").GetString());
        var asked = asking.Single(evt => evt.Name == "question.asked").Data;
        Assert.Equal("question", asked.GetProperty("type").GetString());
        Assert.Equal(Question, asked.GetProperty("question").GetString());
        Assert.Equal(host.OwnerId.ToString(), asked.GetProperty("addresseeActorId").GetString());
        var questionId = asked.GetProperty("questionId").GetGuid();
        var waiting = await host.GetJsonAsync(colleague, $"/api/assistant/conversations/{conversationId}");
        Assert.Equal(questionId, waiting.GetProperty("pendingQuestion").GetProperty("questionId").GetGuid());

        // The colleague sees the question in the shared chat, but only the person it asks answers it.
        var refused = await host.AnswerQuestionAsync(colleague, conversationId, questionId, 0);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("assistant-question-forbidden", refused.Error!.Value.GetProperty("code").GetString());

        var answered = await host.AnswerQuestionAsync(owner, conversationId, questionId, 1);

        Assert.Equal(HttpStatusCode.OK, answered.Status);
        Assert.Equal(1, answered.Events.Single(evt => evt.Name == "question.answered").Data.GetProperty("answerIndex").GetInt32());
        Assert.Equal("completed", answered.Events[^1].Data.GetProperty("status").GetString());
        var result = model.Requests[1].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single(content => content.CallId == "call-ask");
        Assert.Equal("On hold", JsonSerializer.SerializeToElement(result.Result).GetProperty("answer").GetString());

        var conversation = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}");
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        var question = conversation.GetProperty("messages").EnumerateArray()
            .SelectMany(message => message.GetProperty("parts").EnumerateArray())
            .Single(part => part.GetProperty("type").GetString() == "question");
        Assert.Equal("answered", question.GetProperty("status").GetString());
        Assert.Equal(1, question.GetProperty("answerIndex").GetInt32());
        var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
        Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.QuestionAsked && evt.ObjectId == host.OwnerId.ToString());
        Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.QuestionAnswered && evt.ActorId == host.OwnerId.ToString());
    }

    [Fact]
    public async Task SPM_343_the_chat_helper_offers_its_skill_and_questions_in_the_chat_and_works_on_no_project()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(
                "use_skill",
                new { skill = SampleChatHelperComposition.MeetingNotesSkill, reason = "The person tells about a meeting." },
                "call-notes")
            .RespondWithText("Decided: the launch moves to May. Open: the budget.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var conversationId = await CreateConversationAsync(owner, SampleChatHelperComposition.AgentId);

        var events = await host.SendAsync(owner, conversationId, "We met about the launch project and moved it to May.");

        // In the chat it keeps its run settings for chat: its skill with use_skill, and questions.
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains("use_skill", ToolNames(model.Requests[0]));
        Assert.Contains("ask_question", ToolNames(model.Requests[0]));
        var conversation = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}");
        Assert.Equal(SampleChatHelperComposition.MeetingNotesSkill, conversation.GetProperty("messages").EnumerateArray().Last().GetProperty("skillId").GetString());

        // It works on no project: no project's page lists it and a start on one is refused.
        var project = await host.CreateProjectAsync("CHATONLY", ownerId: host.OwnerId);
        var agents = await host.GetJsonAsync(owner, $"/api/assistant/subjects/project/{project:D}/agents");
        Assert.DoesNotContain(agents.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("agentId").GetString() == SampleChatHelperComposition.AgentId);
        using var start = await owner.PostAsync(
            "/api/assistant/threads/start",
            AssistantSampleHost.Json(new { agentId = SampleChatHelperComposition.AgentId, subjectType = "project", subjectId = project.ToString("D") }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, (await AssistantSampleHost.ReadJsonAsync(start)).GetProperty("code").GetString());

        // The administration says it is chat-only.
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        var listed = (await host.GetJsonAsync(administrator, "/api/assistant/admin/agents")).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == SampleChatHelperComposition.AgentId);
        Assert.True(listed.GetProperty("chatOnly").GetBoolean());

        // Its meeting notes skill always works live: its shadow mode is locked and cannot change.
        var notes = listed.GetProperty("skills").EnumerateArray()
            .Single(skill => skill.GetProperty("skillId").GetString() == SampleChatHelperComposition.MeetingNotesSkill);
        Assert.Equal((false, true), (notes.GetProperty("shadow").GetProperty("shadow").GetBoolean(), notes.GetProperty("shadow").GetProperty("locked").GetBoolean()));
        using var shadow = await administrator.PostAsync(
            $"/api/assistant/admin/agents/{SampleChatHelperComposition.AgentId}/skills/{SampleChatHelperComposition.MeetingNotesSkill}/shadow",
            AssistantSampleHost.Json(new { mode = "shadow" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, shadow.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, (await AssistantSampleHost.ReadJsonAsync(shadow)).GetProperty("code").GetString());
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client, string agentId)
    {
        using var response = await client.PostAsync(
            "/api/assistant/conversations",
            AssistantSampleHost.Json(new { agentId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// The owner shares the chat with an invitation link and the colleague joins it.
    /// </summary>
    private static async Task ShareAsync(HttpClient owner, HttpClient colleague, Guid conversationId)
    {
        using var link = await owner.PostAsync($"/api/assistant/conversations/{conversationId}/share-link", null, TestContext.Current.CancellationToken);
        var token = (await AssistantSampleHost.ReadJsonAsync(link)).GetProperty("token").GetString();
        using var joined = await colleague.PostAsync(
            $"/api/assistant/conversations/{conversationId}/join",
            AssistantSampleHost.Json(new { token }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }
}
