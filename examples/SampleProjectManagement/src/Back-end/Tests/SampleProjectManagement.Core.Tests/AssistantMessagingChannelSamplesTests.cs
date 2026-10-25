using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-332: a person continues a chat with the project assistant in a messaging app. The person links
/// the chat in the portal; the messaging provider delivers their messages and their answer to an
/// approval card, and the sample's channel runs each turn as that person through
/// <c>INhAssistantConversationTurns</c>: in their active division, with their own rights for every tool,
/// without a request of the person, and with the channel in the audit.
/// </summary>
public sealed class AssistantMessagingChannelSamplesTests(AssistantMessagingChannelSampleHost host)
    : IClassFixture<AssistantMessagingChannelSampleHost>
{
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

    private static async Task<string> LinkAsync(HttpClient client, Guid conversationId)
    {
        using var response = await client.PostAsync(
            "/assistant-channel/links",
            AssistantSampleHost.Json(new { conversationId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssistantChannelLinkViewModel>(Json, TestContext.Current.CancellationToken))!.Token;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string deliveryId, string token, string text)
    {
        return client.PostAsync(
            "/assistant-channel/messages",
            AssistantSampleHost.Json(new { deliveryId, token, text }),
            TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, string token, AssistantChannelApprovalViewModel card, string text)
    {
        return client.PostAsync(
            "/assistant-channel/decisions",
            AssistantSampleHost.Json(new { token, approvalId = card.ApprovalId, proposalHash = card.ProposalHash, text }),
            TestContext.Current.CancellationToken);
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
