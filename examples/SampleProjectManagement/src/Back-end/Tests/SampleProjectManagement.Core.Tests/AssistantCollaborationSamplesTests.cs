using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.DAL;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for shared assistant conversations (SPM-263) and push notifications
/// (SPM-264) in the sample composition: invitation links, the division directory, a colleague who
/// continues the conversation with their own permissions, read state and the Development push keys.
/// </summary>
public sealed class AssistantCollaborationSamplesTests(AssistantCollaborationSampleHost host)
    : IClassFixture<AssistantCollaborationSampleHost>
{
    [Fact]
    public async Task SPM_263_an_owner_shares_a_conversation_and_a_colleague_continues_it()
    {
        host.Model.Use(new NhAiScriptedChatClient(loop: true).RespondWithText("Here is the project overview."));
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        var conversationId = await host.CreateConversationAsync(owner);
        await host.SendAsync(owner, conversationId, "Which projects are active?");

        var status = await host.GetJsonAsync(owner, "/api/assistant/status");
        var collaboration = status.GetProperty("collaboration");
        Assert.True(collaboration.GetProperty("directory").GetBoolean());
        Assert.Equal("/hub/assistant", collaboration.GetProperty("hubPath").GetString());
        Assert.Equal(10, collaboration.GetProperty("maxParticipants").GetInt32());

        // The directory finds colleagues of the active division only.
        var candidates = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}/participant-candidates?query=colleague");
        Assert.Equal([host.ColleagueId.ToString()], candidates.EnumerateArray().Select(entry => entry.GetProperty("actorId").GetString()));
        var outsiders = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}/participant-candidates?query=outsider");
        Assert.Empty(outsiders.EnumerateArray());

        using var link = await owner.PostAsync($"/api/assistant/conversations/{conversationId}/share-link", null, TestContext.Current.CancellationToken);
        var token = (await AssistantSampleHost.ReadJsonAsync(link)).GetProperty("token").GetString();
        using var joined = await colleague.PostAsync(
            $"/api/assistant/conversations/{conversationId}/join",
            AssistantSampleHost.Json(new { token }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        await host.SendAsync(colleague, conversationId, "Summarize it for the team.");

        var conversation = await host.GetJsonAsync(owner, $"/api/assistant/conversations/{conversationId}");
        var authors = conversation.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "user")
            .Select(message => message.GetProperty("authorActorId").GetString());
        Assert.Equal([host.OwnerId.ToString(), host.ColleagueId.ToString()], authors);
        Assert.Equal(2, conversation.GetProperty("members").GetArrayLength());

        // The colleague's answer is unread for the owner until the owner reads it.
        var item = Assert.Single((await host.GetJsonAsync(owner, "/api/assistant/conversations")).GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("lastReadSequence").GetInt32() < item.GetProperty("lastMessageSequence").GetInt32());
        using var read = await owner.PostAsync(
            $"/api/assistant/conversations/{conversationId}/read",
            AssistantSampleHost.Json(new { }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        item = Assert.Single((await host.GetJsonAsync(owner, "/api/assistant/conversations")).GetProperty("items").EnumerateArray());
        Assert.Equal(item.GetProperty("lastMessageSequence").GetInt32(), item.GetProperty("lastReadSequence").GetInt32());

        var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>().Events;
        Assert.Contains(audit, evt => evt.Kind == NewHeap.Platform.AI.Chat.NhAssistantAuditEventKind.ConversationShareLinkCreated);
        Assert.Contains(audit, evt => evt.Kind == NewHeap.Platform.AI.Chat.NhAssistantAuditEventKind.ConversationParticipantJoined
            && evt.ObjectId == host.ColleagueId.ToString());
    }

    [Fact]
    public async Task SPM_264_development_push_keys_let_browsers_subscribe_only_to_allowed_push_services()
    {
        using var owner = host.CreateClient(host.OwnerId.ToString());

        var settings = await host.GetJsonAsync(owner, "/api/assistant/notifications");
        Assert.True(settings.GetProperty("pushEnabled").GetBoolean());
        Assert.True(settings.GetProperty("pushAvailable").GetBoolean());
        Assert.False(string.IsNullOrEmpty(settings.GetProperty("publicKey").GetString()));
        Assert.True((await host.GetJsonAsync(owner, "/api/assistant/status")).GetProperty("collaboration").GetProperty("push").GetBoolean());

        using var blocked = await owner.PutAsync(
            "/api/assistant/notifications/push-subscription",
            AssistantSampleHost.Json(new { endpoint = "https://attacker.example/push", keys = new { p256dh = "x", auth = "y" } }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);

        using var optOut = await owner.PutAsync(
            "/api/assistant/notifications",
            AssistantSampleHost.Json(new { pushEnabled = false }),
            TestContext.Current.CancellationToken);
        Assert.False((await AssistantSampleHost.ReadJsonAsync(optOut)).GetProperty("pushEnabled").GetBoolean());
    }
}

/// <summary>
/// The assistant sample host plus the sample DAL, so the division directory finds real users.
/// </summary>
public sealed class AssistantCollaborationSampleHost : AssistantSampleHost
{
    public Guid OwnerId { get; } = Guid.NewGuid();

    public Guid ColleagueId { get; } = Guid.NewGuid();

    public Guid OutsiderId { get; } = Guid.NewGuid();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        await dbContext.Database.MigrateAsync();

        var otherDivision = Guid.NewGuid();
        dbContext.Set<NhDivision>().AddRange(
            new NhDivision { Id = DivisionId, Name = "Assistant sample division", TimeZoneId = "Europe/Amsterdam" },
            new NhDivision { Id = otherDivision, Name = "Other division", TimeZoneId = "Europe/Amsterdam" });
        AddUser(dbContext, OwnerId, "owner@sample.localhost", DivisionId);
        AddUser(dbContext, ColleagueId, "colleague@sample.localhost", DivisionId);
        AddUser(dbContext, OutsiderId, "outsider@sample.localhost", otherDivision);
        await dbContext.SaveChangesAsync();
    }

    private static void AddUser(SampleProjectManagementDbContext dbContext, Guid id, string email, Guid divisionId)
    {
        dbContext.Users.Add(new NhUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            ActiveDivisionId = divisionId
        });
        dbContext.Set<NhDivisionUser>().Add(new NhDivisionUser { Id = Guid.NewGuid(), UserId = id, DivisionId = divisionId });
    }
}
