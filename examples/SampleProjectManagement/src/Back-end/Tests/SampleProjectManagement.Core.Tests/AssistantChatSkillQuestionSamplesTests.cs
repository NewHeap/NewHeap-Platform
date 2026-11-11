using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
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
/// skill always works live. People may still chat with it about a project (<c>ChatsAbout</c>), which the
/// project's access resolver decides on creation and on every turn and read.
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
        // The switch is recorded as a successful call, not as a failed tool call.
        Assert.Equal("succeeded", events.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        // No triage: the planner's model answers twice, the second time in the skill. Before the
        // choice it is offered nothing else (routesFirst).
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(["use_skill"], ToolNames(model.Requests[0]));
        var inSkill = model.Requests[1];
        Assert.Contains("## Skill: " + SampleProjectPlannerComposition.TaskFollowUpSkill, inSkill.Options!.Instructions);
        // The follow-up skill selects only the status tool; use_skill still offers the deadline risk skill,
        // and once a skill is chosen the planner may keep its plan (KeepPlan()).
        Assert.Equal(["projects_change_status_v1", "update_plan", "use_skill"], ToolNames(inSkill).Order(StringComparer.Ordinal).ToArray());

        var conversation = await host.GetJsonAsync(client, $"/api/assistant/conversations/{conversationId}");
        var answer = conversation.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("assistant", answer.GetProperty("role").GetString());
        Assert.Equal(SampleProjectPlannerComposition.TaskFollowUpSkill, answer.GetProperty("skillId").GetString());
    }

    [Fact]
    public async Task SPM_333_the_planner_keeps_a_visible_plan_of_its_work_in_the_chat()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(
                "use_skill",
                new { skill = SampleProjectPlannerComposition.TaskFollowUpSkill, reason = "The person asks which task comes next." },
                "call-skill")
            .RespondWithFunctionCall(
                "update_plan",
                new
                {
                    steps = new[]
                    {
                        new { text = "Find the open tasks of the launch project", status = "in_progress" },
                        new { text = "Name the task that comes next", status = "pending" }
                    }
                },
                "call-plan")
            .RespondWithFunctionCall("update_plan", new { advance = true }, "call-advance")
            .RespondWithText("The design review comes next.");
        host.Model.Use(model);
        using var client = host.CreateClient(host.OwnerId.ToString());
        var conversationId = await CreateConversationAsync(client, SampleProjectPlannerComposition.AgentId);

        var events = await host.SendAsync(client, conversationId, "Which task of the launch project comes next?");

        // The planner keeps no plan before it chose a skill, and may once it did (KeepPlan()).
        Assert.DoesNotContain("update_plan", ToolNames(model.Requests[0]));
        Assert.Contains("update_plan", ToolNames(model.Requests[1]));
        // Every update streams the whole plan as plan.updated, never as a tool call of its own.
        var plans = events.Where(evt => evt.Name == "plan.updated").Select(evt => evt.Data).ToArray();
        Assert.Equal(2, plans.Length);
        Assert.All(plans, plan => Assert.True(plan.TryGetProperty("turnId", out _) && plan.TryGetProperty("messageId", out _)));
        Assert.Equal(
            ["completed", "in-progress"],
            plans[1].GetProperty("plan").GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("status").GetString()));
        Assert.DoesNotContain(events, evt => evt.Name == "tool.started" && evt.Data.GetProperty("toolId").GetString() == "assistant.plan");

        // A reloaded conversation shows the plan as it ended in the planner's message.
        var conversation = await host.GetJsonAsync(client, $"/api/assistant/conversations/{conversationId}");
        var plan = conversation.GetProperty("messages").EnumerateArray().Last()
            .GetProperty("parts").EnumerateArray()
            .Single(part => part.GetProperty("type").GetString() == "plan");
        Assert.Equal(
            [("Find the open tasks of the launch project", "completed"), ("Name the task that comes next", "in-progress")],
            plan.GetProperty("steps").EnumerateArray().Select(step => (step.GetProperty("text").GetString(), step.GetProperty("status").GetString())));
    }

    [Fact]
    public async Task SPM_333_a_decision_continues_the_chat_in_the_skill_that_proposed_the_change()
    {
        var project = await host.CreateProjectAsync("SKILLDECIDE", ownerId: host.OwnerId);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(
                "use_skill",
                new { skill = SampleChatHelperComposition.MeetingNotesSkill, reason = "The person tells about a meeting." },
                "call-notes")
            .RespondWithFunctionCall("projects_get_v1", new { input = new { projectId = project } }, "call-read")
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = project, status = "OnHold" } }, "call-hold")
            .RespondWithText("Decided: the project is on hold.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var conversationId = await CreateConversationAsync(owner, SampleChatHelperComposition.AgentId);

        var waiting = await host.SendAsync(owner, conversationId, "We met and put the project on hold.");
        var decided = await host.DecideAsync(owner, conversationId, waiting.Single(evt => evt.Name == "approval.required").Data, "approve");

        // The model reads the outcome in the skill: its instructions and only its tools, not the helper's others.
        Assert.Equal("completed", decided[^1].Data.GetProperty("status").GetString());
        var afterDecision = model.Requests[3];
        Assert.Contains("## Skill: " + SampleChatHelperComposition.MeetingNotesSkill, afterDecision.Options!.Instructions);
        Assert.Equal(["ask_question", "projects_change_status_v1", "projects_get_v1"], ToolNames(afterDecision).Order(StringComparer.Ordinal).ToArray());
        var conversation = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}");
        Assert.Equal(SampleChatHelperComposition.MeetingNotesSkill, conversation.GetProperty("messages").EnumerateArray().Last().GetProperty("skillId").GetString());
    }

    [Fact]
    public async Task SPM_349_the_chat_helper_proposes_no_change_to_a_project_it_did_not_read_in_the_turn()
    {
        var project = await host.CreateProjectAsync("UNREAD", ownerId: host.OwnerId);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(
                "use_skill",
                new { skill = SampleChatHelperComposition.MeetingNotesSkill, reason = "The person tells about a meeting." },
                "call-notes")
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = project, status = "OnHold" } }, "call-hold")
            .RespondWithText("I need to look the project up first.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var conversationId = await CreateConversationAsync(owner, SampleChatHelperComposition.AgentId);

        var events = await host.SendAsync(owner, conversationId, "We met and put the project on hold.");

        // No result of this turn holds the id, so nothing is proposed and the model reads why.
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.DoesNotContain(events, evt => evt.Name == "approval.required");
        var result = JsonSerializer.Serialize(model.FunctionResults.Last().Result);
        Assert.Contains(NhAssistantErrorCodes.ProposalNotNeeded, result, StringComparison.Ordinal);
        Assert.Contains(ProjectReadBeforeChangeProposalGuard.NotReadCode, result, StringComparison.Ordinal);
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
    public async Task SPM_334_an_answer_brings_the_status_advisor_no_new_tool_calls()
    {
        // The sample allows four tool calls per turn; a question is one, and answering gives none back.
        var model = new NhAiScriptedChatClient();
        foreach (var question in Enumerable.Range(1, 5))
        {
            model.RespondWithFunctionCall("ask_question", new { question = Question, choices = new[] { "Active", "Closed" } }, "call-" + question);
        }

        model.RespondWithText("I asked enough; tell me which status you want.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var conversationId = await CreateConversationAsync(owner, SampleStatusAdvisorComposition.AgentId);

        var events = await host.SendAsync(owner, conversationId, "Change the status of the launch project.");
        foreach (var _ in Enumerable.Range(1, 4))
        {
            var questionId = events.Single(evt => evt.Name == "question.asked").Data.GetProperty("questionId").GetGuid();
            events = (await host.AnswerQuestionAsync(owner, conversationId, questionId, 0)).Events;
        }

        // The fifth question was refused at the limit, so the advisor closed with an answer instead.
        Assert.DoesNotContain(events, evt => evt.Name == "question.asked");
        Assert.Equal(NhAssistantErrorCodes.ToolCallLimitReached, events[^1].Data.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SPM_291_the_project_assistant_fits_its_budget_with_the_application_context_stored_here()
    {
        using var scope = host.Services.CreateScope();

        var budgets = await scope.ServiceProvider.GetRequiredService<INhAssistantInstructionBudgets>()
            .GetAsync(SampleAssistantComposition.AgentId, TestContext.Current.CancellationToken);

        Assert.True(budgets.Success);
        var budget = budgets.Data![0];
        Assert.Equal(4_000, budget.RetrievalCharacters);
        Assert.True(budget.Fits, $"{budget.RemainingCharacters} characters remain.");
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

    [Fact]
    public async Task SPM_343_a_member_chats_with_the_chat_helper_about_their_project_and_loses_the_chat_once_removed()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Decided: the launch moves to May.");
        host.Model.Use(model);
        var project = await host.CreateProjectAsync("CHATABOUT", ownerId: host.OwnerId);
        var membership = await host.AddMemberAsync(project, host.ReaderId, ProjectMemberRole.Reader);
        using var reader = host.CreateClient(host.ReaderId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        var aboutProject = new { agentId = SampleChatHelperComposition.AgentId, subjectType = ProjectSubjects.Type, subjectId = project.ToString("D") };

        // A chat about a project the person may read lists the helper, as a chat without a subject does.
        var listed = await host.GetJsonAsync(reader, $"/api/assistant/agents?subjectType={ProjectSubjects.Type}&subjectId={project:D}");
        Assert.Contains(listed.EnumerateArray(), agent => agent.GetProperty("id").GetString() == SampleChatHelperComposition.AgentId);
        using (var refusedAgents = await outsider.GetAsync(
            $"/api/assistant/agents?subjectType={ProjectSubjects.Type}&subjectId={project:D}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refusedAgents.StatusCode);
        }

        // The chat keeps the project; someone outside the project is refused.
        using var created = await reader.PostAsync("/api/assistant/conversations", AssistantSampleHost.Json(aboutProject), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var chat = await AssistantSampleHost.ReadJsonAsync(created);
        var chatId = chat.GetProperty("id").GetGuid();
        Assert.Equal(project.ToString("D"), chat.GetProperty("subject").GetProperty("subjectId").GetString());
        using (var notAMember = await outsider.PostAsync("/api/assistant/conversations", AssistantSampleHost.Json(aboutProject), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAMember.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, (await AssistantSampleHost.ReadJsonAsync(notAMember)).GetProperty("code").GetString());
        }

        // A message reads the project's briefing and keeps the helper's chat settings.
        var events = await host.SendAsync(reader, chatId, "What did we decide about the launch?");
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        Assert.Contains("ask_question", ToolNames(model.Requests[0]));
        Assert.Contains(
            model.Requests[0].Messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text),
            text => text.Contains("<subject-briefing>", StringComparison.Ordinal) && text.Contains("CHATABOUT-", StringComparison.Ordinal));

        // The helper still works on no project: nothing offers a start, and a start by its owner is refused.
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var agents = await host.GetJsonAsync(owner, $"/api/assistant/subjects/project/{project:D}/agents");
        Assert.DoesNotContain(agents.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("agentId").GetString() == SampleChatHelperComposition.AgentId);
        using (var start = await owner.PostAsync(
            "/api/assistant/threads/start",
            AssistantSampleHost.Json(new { agentId = SampleChatHelperComposition.AgentId, subjectType = "project", subjectId = project.ToString("D") }),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, (await AssistantSampleHost.ReadJsonAsync(start)).GetProperty("code").GetString());
        }

        // Removed from the project, the reader can no longer read or continue the chat.
        await host.RemoveMemberAsync(membership);
        using (var read = await reader.GetAsync($"/api/assistant/conversations/{chatId}", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        }
        using (var message = await reader.PostAsync(
            $"/api/assistant/conversations/{chatId}/messages",
            AssistantSampleHost.Json(new { text = "And now?", clientMessageId = Guid.NewGuid().ToString("N") }),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, message.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, (await AssistantSampleHost.ReadJsonAsync(message)).GetProperty("code").GetString());
        }
        Assert.Single(model.Requests);
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
