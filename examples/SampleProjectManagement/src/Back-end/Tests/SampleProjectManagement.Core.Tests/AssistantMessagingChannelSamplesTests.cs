using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-332: a person continues a chat with the project assistant in a messaging app. The person links
/// the chat in the portal; the messaging provider delivers their messages and their answer to an
/// approval card, and the sample's channel runs each turn as that person through
/// <c>INhAssistantConversationTurns</c>: in their active division, with their own rights for every tool,
/// without a request of the person, and with the channel in the audit. SPM-341: the person also starts,
/// reads and stops a chat and answers its question card through <c>INhAssistantConversations</c> and
/// <c>AnswerAsync</c>. SPM-342: the turn's situation names the person and the channel, and the status
/// change names the conversation and the channel of the turn that made it. SPM-349: a proposal guard
/// reads the channel of the call that proposes and keeps the messaging app from proposing to archive a
/// project before any card. SPM-380: the provider's web widget sends the page the person has open, which
/// the turn shows as the person's screen.
/// </summary>
public sealed class AssistantMessagingChannelSamplesTests(AssistantMessagingChannelSampleHost host)
    : IClassFixture<AssistantMessagingChannelSampleHost>
{
    private const string LaunchQuestion = "Which status should the launch project get?";

    private static readonly string[] LaunchChoices = ["Active", "On hold", "Closed"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_332_a_person_chats_and_approves_a_status_change_from_the_messaging_app()
    {
        var projectId = Guid.NewGuid();
        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap", limit = 5 } })
            .RespondWithText("One roadmap project is visible in your division.")
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = (int)ProjectStatus.Active } })
            .RespondWithText("The project is active now."));
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var conversationId = await host.CreateConversationAsync(owner);
        var token = await LinkAsync(owner, conversationId);

        // The person asks in the messaging app; the tool searches their active division.
        var answer = await AnswerOfAsync(await SendAsync(messaging, "delivery-1", token, "Which roadmap projects exist?"));
        Assert.Equal(("completed", "One roadmap project is visible in your division.", null), (answer.Status, answer.Text, answer.Approval));
        Assert.Contains(host.DivisionId, host.Projects.SearchedDivisions);

        // A delivery the provider repeats starts no second turn, and a forged token none at all.
        using (var repeated = await SendAsync(messaging, "delivery-1", token, "Which roadmap projects exist?"))
        {
            Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.MessageDuplicate, (await ErrorOfAsync(repeated)).Code);
        }
        using (var forged = await SendAsync(messaging, "delivery-forged", "not-a-token", "Archive everything."))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            Assert.Equal(SampleAssistantMessagingChannel.DeliveryInvalidCode, (await ErrorOfAsync(forged)).Code);
        }

        // The status change waits for the person's own approval card.
        var waiting = await AnswerOfAsync(await SendAsync(messaging, "delivery-2", token, "Activate the roadmap project."));
        Assert.Equal("waiting-for-approval", waiting.Status);
        var card = waiting.Approval!;
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);
        using (var unclear = await DecideAsync(messaging, token, card, "maybe"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unclear.StatusCode);
        }

        var approved = await AnswerOfAsync(await DecideAsync(messaging, token, card, "yes"));

        Assert.Equal(("completed", "The project is active now."), (approved.Status, approved.Text));
        Assert.Contains(host.Projects.StatusChanges, change => change.ProjectId == projectId && change.Status == ProjectStatus.Active);
        Assert.Contains(
            host.Services.GetRequiredService<SampleAssistantAuditLog>().Events,
            evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved
                && evt.ApprovalId == card.ApprovalId
                && evt.Channel == SampleAssistantMessagingChannel.Channel);

        // The portal shows the same chat.
        var conversation = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}");
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        Assert.Contains("The project is active now.", conversation.GetProperty("messages").ToString());
    }

    [Fact]
    public async Task SPM_332_a_link_to_someone_elses_chat_finds_nothing()
    {
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var conversationId = await host.CreateConversationAsync(owner);

        // The outsider links the owner's chat to their own messaging app: the turn would run as the
        // outsider, who does not take part in it.
        var token = await LinkAsync(outsider, conversationId);
        using var sent = await SendAsync(messaging, "delivery-outsider", token, "What is in this chat?");

        Assert.Equal(HttpStatusCode.NotFound, sent.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, (await ErrorOfAsync(sent)).Code);
        var conversation = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}");
        Assert.Empty(conversation.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task SPM_341_a_person_starts_reads_answers_and_stops_a_chat_from_the_messaging_app()
    {
        host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = LaunchQuestion, choices = LaunchChoices }, "call-ask")
            .RespondWithText("I will put the launch project on hold.")
            .RespondWithFunctionCall("ask_question", new { question = LaunchQuestion, choices = LaunchChoices }, "call-ask-again"));
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var linked = await LinkAsync(owner, await host.CreateConversationAsync(owner));

        // The person starts a chat with the status advisor from the messaging app, which continues there.
        using (var unknown = await PostAsync(messaging, "chats", new { token = linked, agentId = "no-such-agent" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.AgentNotFound, (await ErrorOfAsync(unknown)).Code);
        }
        var started = await ReadAsync<AssistantChannelChatViewModel>(await PostAsync(
            messaging,
            "chats",
            new { token = linked, agentId = SampleStatusAdvisorComposition.AgentId, title = "Launch project" }));
        Assert.Equal(("idle", "Launch project", null), (started.Chat.Status, started.Chat.Title, started.Chat.LastAnswer));
        var token = started.Token;

        // The advisor asks; the messaging app shows the question card, also when it reads the chat later.
        var asked = await AnswerOfAsync(await SendAsync(messaging, "delivery-341-1", token, "Change the status of the launch project."));
        Assert.Equal("waiting-for-approval", asked.Status);
        var card = asked.Question!;
        Assert.Equal(LaunchQuestion, card.Question);
        Assert.Equal(LaunchChoices, card.Choices);
        var waiting = await ReadAsync<AssistantChannelChatStateViewModel>(await PostAsync(messaging, "chat", new { token }));
        Assert.Equal(("waiting-for-approval", card.QuestionId), (waiting.Status, waiting.Question!.QuestionId));

        // A reply that names no choice answers nothing; the card's number answers.
        using (var unclear = await PostAsync(messaging, "answers", new { token, questionId = card.QuestionId, text = "perhaps" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unclear.StatusCode);
            Assert.Equal(SampleAssistantMessagingChannel.DeliveryInvalidCode, (await ErrorOfAsync(unclear)).Code);
        }
        var answered = await AnswerOfAsync(await PostAsync(messaging, "answers", new { token, questionId = card.QuestionId, text = "2" }));
        Assert.Equal(("completed", "I will put the launch project on hold.", null), (answered.Status, answered.Text, answered.Question));
        // A repeated delivery of the same reply finds the question answered.
        using (var again = await PostAsync(messaging, "answers", new { token, questionId = card.QuestionId, text = "2" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.QuestionNotPending, (await ErrorOfAsync(again)).Code);
        }
        var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
        Assert.Contains(audit, evt => evt.Kind == NhAssistantAuditEventKind.QuestionAnswered
            && evt.InvocationId == card.QuestionId
            && evt.Channel == SampleAssistantMessagingChannel.Channel);

        // The person stops the next question from the messaging app; someone else's link finds nothing.
        var second = await AnswerOfAsync(await SendAsync(messaging, "delivery-341-2", token, "Ask me again."));
        Assert.NotNull(second.Question);
        var chatId = (await host.GetJsonAsync(owner, "/api/assistant/conversations")).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == "Launch project")
            .GetProperty("id")
            .GetGuid();
        var outsiderToken = await LinkAsync(outsider, chatId);
        using (var foreign = await PostAsync(messaging, "stops", new { token = outsiderToken }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, (await ErrorOfAsync(foreign)).Code);
        }
        using (var stopped = await PostAsync(messaging, "stops", new { token }))
        {
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }
        var idle = await ReadAsync<AssistantChannelChatStateViewModel>(await PostAsync(messaging, "chat", new { token }));
        Assert.Equal(("idle", null), (idle.Status, idle.Question));
        // The portal shows the same chat with the stopped question.
        var portal = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{chatId}");
        Assert.Contains("\"status\":\"cancelled\"", portal.GetProperty("messages").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SPM_342_the_turn_of_the_messaging_app_names_its_person_conversation_and_channel()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = (int)ProjectStatus.OnHold } })
            .RespondWithText("The project is on hold now.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var conversationId = await host.CreateConversationAsync(owner);
        var token = await LinkAsync(owner, conversationId);

        var waiting = await AnswerOfAsync(await SendAsync(messaging, "delivery-342", token, "Put the launch project on hold."));
        var approved = await AnswerOfAsync(await DecideAsync(messaging, token, waiting.Approval!, "yes"));

        Assert.Equal("completed", approved.Status);
        // The situation names the person from the user store and the channel, without their request.
        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.Contains("- User: owner@sample.localhost", instructions, StringComparison.Ordinal);
        Assert.Contains("- Channel: messaging app; answer in a few short sentences of plain text", instructions, StringComparison.Ordinal);
        // The status change names the chat and the channel of the turn that made it.
        var cause = host.Projects.ChangeCauses.Single(change => change.ProjectId == projectId).Cause!;
        Assert.Equal((conversationId, SampleAssistantMessagingChannel.Channel), (cause.ConversationId, cause.Channel));
    }

    [Fact]
    public async Task SPM_380_the_web_widget_shows_the_assistant_the_page_the_person_has_open()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithText("You are looking at the roadmap project.")
            .RespondWithText("Nothing else is open.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var conversationId = await host.CreateConversationAsync(owner);
        var token = await LinkAsync(owner, conversationId);
        var page = new
        {
            route = $"/projects/{projectId}",
            title = "Roadmap <draft>",
            entities = new[] { new { type = "project", id = projectId.ToString(), label = "Roadmap" } }
        };

        var answer = await AnswerOfAsync(await SendAsync(messaging, "delivery-380", token, "What am I looking at?", page));

        Assert.Equal(("completed", "You are looking at the roadmap project."), (answer.Status, answer.Text));
        // The turn shows the page as the person's screen, cleaned as the portal's own messages are.
        Assert.Contains(
            $"<page-data>\n- Route: /projects/{projectId}\n- Title: Roadmap ‹draft›\n- Entity: project {projectId} (Roadmap)\n</page-data>",
            model.Requests[0].Options!.Instructions,
            StringComparison.Ordinal);

        // A page the platform would not keep whole, here more records than the five it shows, is refused
        // with the portal endpoint's code and status, and starts no turn.
        var crowded = new
        {
            route = "/projects",
            title = (string?)null,
            entities = Enumerable.Range(1, 6).Select(index => new { type = "project", id = index.ToString(), label = (string?)null }).ToArray()
        };
        using (var refused = await SendAsync(messaging, "delivery-380-crowded", token, "And these?", crowded))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.MessageInvalid, (await ErrorOfAsync(refused)).Code);
        }
        Assert.Single(model.Requests);

        // A message from the messaging app itself carries no page.
        await AnswerOfAsync(await SendAsync(messaging, "delivery-380-app", token, "Anything else?"));
        Assert.DoesNotContain("<page-data>", model.Requests[1].Options!.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SPM_349_the_messaging_app_does_not_propose_to_archive_a_project_and_reads_why_before_any_card()
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = (int)ProjectStatus.Archived } })
            .RespondWithText("Archive the project in the portal, where you choose what to keep.")
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = (int)ProjectStatus.Archived } })
            .RespondWithText("Approve the archiving first.");
        host.Model.Use(model);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var conversationId = await host.CreateConversationAsync(owner);
        var token = await LinkAsync(owner, conversationId);

        // The proposal guard knows the call came from the messaging app: nothing is proposed, so no card.
        var answer = await AnswerOfAsync(await SendAsync(messaging, "delivery-349", token, "Archive the launch project."));

        Assert.Equal(("completed", null), (answer.Status, answer.Approval));
        var result = JsonSerializer.Serialize(Assert.Single(model.FunctionResults).Result);
        Assert.Contains(NhAssistantErrorCodes.ProposalNotNeeded, result, StringComparison.Ordinal);
        Assert.Contains(ProjectArchiveChannelProposalGuard.ArchiveInPortalCode, result, StringComparison.Ordinal);
        Assert.Contains("Archive the project in the portal, where you choose what to keep.", result, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);

        // The same chat in the portal proposes it, and the card waits for the person.
        var events = await host.SendAsync(owner, conversationId, "Archive the launch project.");

        var card = Assert.Single(events, evt => evt.Name == "approval.required").Data;
        Assert.Equal(ProjectAiTools.ChangeStatusToolId, card.GetProperty("toolId").GetString());
        Assert.Equal("waiting-for-approval", events[^1].Data.GetProperty("status").GetString());
        Assert.DoesNotContain(host.Projects.StatusChanges, change => change.ProjectId == projectId);
    }

    [Fact]
    public async Task SPM_332_only_the_persons_own_session_links_a_chat_and_a_password_change_or_revocation_ends_its_links()
    {
        var personId = await host.CreatePersonAsync();
        using var person = host.CreateClient(personId.ToString());
        using var impersonated = host.CreateImpersonationClient(personId.ToString(), host.OwnerId.ToString());
        using var messaging = host.CreateAnonymousClient();
        var conversationId = await host.CreateConversationAsync(person);

        // An administrator who impersonates the person gets no link: it would act as the person after
        // the impersonation ended.
        using (var refused = await impersonated.PostAsync(
            "/assistant-channel/links",
            AssistantSampleHost.Json(new { conversationId }),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.NotAllowedWhileImpersonating, (await ErrorOfAsync(refused)).Code);
        }

        // The person's own link works, and a chat started through it, or through that chat's link,
        // expires with it instead of getting a fresh lifetime.
        var link = await LinkWithExpiryAsync(person, conversationId);
        Assert.Equal("idle", (await ReadAsync<AssistantChannelChatStateViewModel>(await PostAsync(messaging, "chat", new { token = link.Token }))).Status);
        var started = await ReadAsync<AssistantChannelChatViewModel>(await PostAsync(
            messaging,
            "chats",
            new { token = link.Token, agentId = SampleStatusAdvisorComposition.AgentId }));
        var startedAgain = await ReadAsync<AssistantChannelChatViewModel>(await PostAsync(
            messaging,
            "chats",
            new { token = started.Token, agentId = SampleStatusAdvisorComposition.AgentId }));
        Assert.Equal(link.ExpiresAt, started.ExpiresAt);
        Assert.Equal(link.ExpiresAt, startedAgain.ExpiresAt);

        // A password change or a revocation of the person's sessions replaces the security stamp, and
        // every link of the person ends with it.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(personId.ToString()))!)).Succeeded);
        }
        foreach (var token in new[] { link.Token, started.Token, startedAgain.Token })
        {
            using var ended = await PostAsync(messaging, "chat", new { token });
            Assert.Equal(HttpStatusCode.BadRequest, ended.StatusCode);
            Assert.Equal(SampleAssistantMessagingChannel.DeliveryInvalidCode, (await ErrorOfAsync(ended)).Code);
        }
        using (var renewal = await PostAsync(messaging, "chats", new { token = started.Token, agentId = SampleStatusAdvisorComposition.AgentId }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, renewal.StatusCode);
        }

        // Linking again in the person's own session works with the new stamp.
        var relinked = await LinkAsync(person, conversationId);
        Assert.Equal("idle", (await ReadAsync<AssistantChannelChatStateViewModel>(await PostAsync(messaging, "chat", new { token = relinked }))).Status);
    }

    private static async Task<string> LinkAsync(HttpClient client, Guid conversationId)
    {
        return (await LinkWithExpiryAsync(client, conversationId)).Token;
    }

    private static async Task<AssistantChannelLinkViewModel> LinkWithExpiryAsync(HttpClient client, Guid conversationId)
    {
        using var response = await client.PostAsync(
            "/assistant-channel/links",
            AssistantSampleHost.Json(new { conversationId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssistantChannelLinkViewModel>(Json, TestContext.Current.CancellationToken))!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string deliveryId, string token, string text)
    {
        return client.PostAsync(
            "/assistant-channel/messages",
            AssistantSampleHost.Json(new { deliveryId, token, text }),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// SPM-380: a message the provider's web widget delivers with the page the person has open.
    /// </summary>
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string deliveryId, string token, string text, object page)
    {
        return client.PostAsync(
            "/assistant-channel/messages",
            AssistantSampleHost.Json(new { deliveryId, token, text, page }),
            TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, string token, AssistantChannelApprovalViewModel card, string text)
    {
        return client.PostAsync(
            "/assistant-channel/decisions",
            AssistantSampleHost.Json(new { token, approvalId = card.ApprovalId, proposalHash = card.ProposalHash, text }),
            TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        return client.PostAsync(
            "/assistant-channel/" + path,
            AssistantSampleHost.Json(body),
            TestContext.Current.CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;
        }
    }

    private static async Task<AssistantChannelAnswerViewModel> AnswerOfAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AssistantChannelAnswerViewModel>(Json, TestContext.Current.CancellationToken))!;
        }
    }

    private static async Task<AssistantChannelErrorViewModel> ErrorOfAsync(HttpResponseMessage response)
    {
        return (await response.Content.ReadFromJsonAsync<AssistantChannelErrorViewModel>(Json, TestContext.Current.CancellationToken))!;
    }
}

/// <summary>
/// SPM-332: the agent run host with the messaging channel and its controller, as in Program.cs.
/// </summary>
public sealed class AssistantMessagingChannelSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new ChannelController()));
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddScoped<SampleAssistantMessagingChannel>();
    }

    private sealed class ChannelController : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo)
        {
            return typeInfo.AsType() == typeof(AssistantChannelController);
        }
    }
}
