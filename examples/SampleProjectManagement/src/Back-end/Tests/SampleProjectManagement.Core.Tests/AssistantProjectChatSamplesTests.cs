using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-363: a person's chat about one project. The project dialog starts the project chat with the
/// project as the chat's subject (<c>NhAssistantStore.setChatSubject</c>, which names it in
/// <c>POST conversations</c>); the agent works on projects and is offered only for chats about one
/// (<c>RequireChatSubject()</c>). The project's access resolver decides when the chat is created and on
/// every turn and read, so a member removed from the project loses the chat at once, while the person
/// still deletes it. Each message reads the project's briefing.
/// </summary>
public sealed class AssistantProjectChatSamplesTests(AssistantAgentRunSampleHost host)
    : IClassFixture<AssistantAgentRunSampleHost>
{
    [Fact]
    public async Task SPM_363_a_member_chats_about_their_project_and_loses_the_chat_once_removed_from_it()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("The project is a draft without a deadline.");
        host.Model.Use(model);
        var project = await host.CreateProjectAsync("CHATSUBJECT", ownerId: host.OwnerId);
        var membership = await host.AddMemberAsync(project, host.ReaderId, ProjectMemberRole.Reader);
        using var reader = host.CreateClient(host.ReaderId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        var subjectQuery = $"subjectType={ProjectSubjects.Type}&subjectId={project:D}";

        // The project chat is offered only for a chat about a project the person may read.
        Assert.DoesNotContain(SampleProjectChatComposition.AgentId, await AgentIdsAsync(reader, "/api/assistant/agents"));
        Assert.Contains(SampleProjectChatComposition.AgentId, await AgentIdsAsync(reader, "/api/assistant/agents?" + subjectQuery));
        using (var refusedAgents = await outsider.GetAsync("/api/assistant/agents?" + subjectQuery, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refusedAgents.StatusCode);
        }

        // The chat about the project keeps it; without a subject the agent is refused.
        using var created = await CreateAsync(reader, new { agentId = SampleProjectChatComposition.AgentId, subjectType = ProjectSubjects.Type, subjectId = project.ToString("D") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var chat = await AssistantSampleHost.ReadJsonAsync(created);
        var chatId = chat.GetProperty("id").GetGuid();
        var subject = chat.GetProperty("subject");
        Assert.Equal((ProjectSubjects.Type, project.ToString("D")), (subject.GetProperty("subjectType").GetString(), subject.GetProperty("subjectId").GetString()));
        Assert.StartsWith("CHATSUBJECT-", subject.GetProperty("subjectDisplayName").GetString());
        using (var withoutSubject = await CreateAsync(reader, new { agentId = SampleProjectChatComposition.AgentId }))
        {
            Assert.Equal((HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectRequired), (withoutSubject.StatusCode, await CodeOfAsync(withoutSubject)));
        }
        using (var notAMember = await CreateAsync(outsider, new { agentId = SampleProjectChatComposition.AgentId, subjectType = ProjectSubjects.Type, subjectId = project.ToString("D") }))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (notAMember.StatusCode, await CodeOfAsync(notAMember)));
        }

        // A message reads the project's briefing, which names the project the chat is about.
        var events = await host.SendAsync(reader, chatId, "Where does the project stand?");
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
        var briefing = model.Requests[0].Messages
            .Where(message => message.Role == ChatRole.System)
            .Select(message => message.Text)
            .Single(text => text.Contains("<subject-briefing>", StringComparison.Ordinal));
        Assert.Contains("The subject of this chat", briefing, StringComparison.Ordinal);
        Assert.Contains("CHATSUBJECT-", briefing, StringComparison.Ordinal);

        // Removed from the project, the reader can no longer read or continue the chat, but deletes it.
        await host.RemoveMemberAsync(membership);
        using (var read = await reader.GetAsync($"/api/assistant/conversations/{chatId}", TestContext.Current.CancellationToken))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (read.StatusCode, await CodeOfAsync(read)));
        }
        using (var message = await reader.PostAsync(
            $"/api/assistant/conversations/{chatId}/messages",
            AssistantSampleHost.Json(new { text = "And now?", clientMessageId = Guid.NewGuid().ToString("N") }),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (message.StatusCode, await CodeOfAsync(message)));
        }
        Assert.Single(model.Requests);
        using (var deleted = await reader.DeleteAsync($"/api/assistant/conversations/{chatId}", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, object body)
    {
        return client.PostAsync("/api/assistant/conversations", AssistantSampleHost.Json(body), TestContext.Current.CancellationToken);
    }

    private async Task<string[]> AgentIdsAsync(HttpClient client, string path)
    {
        return (await host.GetJsonAsync(client, path)).EnumerateArray()
            .Select(agent => agent.GetProperty("id").GetString()!)
            .ToArray();
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
