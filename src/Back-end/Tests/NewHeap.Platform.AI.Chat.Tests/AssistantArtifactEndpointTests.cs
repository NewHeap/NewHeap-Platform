using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantArtifactEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Chat_tools_return_versioned_files_only_to_current_conversation_members(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("The notes are ready.");
        await using var app = await AssistantWebApplication.StartAsync(database, model, provider: provider);
        app.Tools.SaveSearchArtifact = true;
        using var owner = app.CreateClient();
        using var colleague = app.CreateClient("user-2");
        using var anonymous = app.CreateClient(user: null, access: false);
        using var forbidden = app.CreateClient(access: false);
        var id = await CreateConversationAsync(owner);
        var files = $"/api/assistant/conversations/{id}/artifacts";
        Assert.Empty((await owner.GetFromJsonAsync<NhAssistantArtifactDto[]>(files))!);
        using var turn = await owner.PostAsJsonAsync($"/api/assistant/conversations/{id}/messages", new { text = "Save the notes.", clientMessageId = "notes" });
        turn.EnsureSuccessStatusCode();
        await ReadAllAsync(turn);
        var artifacts = (await owner.GetFromJsonAsync<NhAssistantArtifactDto[]>(files))!;
        Assert.Equal([2, 1], artifacts.Select(file => file.Version));
        Assert.All(artifacts, file => { Assert.Null(file.RunId); Assert.False(file.Restricted); });
        var content = files + $"/{artifacts[0].Id}/content";
        using var download = await owner.GetAsync(content);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/markdown", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(artifacts[0].Name, download.Content.Headers.ContentDisposition.FileNameStar);
        Assert.True(download.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.StartsWith("# ", await download.Content.ReadAsStringAsync());
        foreach (var route in new[] { files, content })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await forbidden.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync(route)).StatusCode);
        }
        var other = await CreateConversationAsync(owner);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/assistant/conversations/{other}/artifacts/{artifacts[0].Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(files + $"/{Guid.NewGuid()}/content")).StatusCode);

        using var link = await owner.PostAsync($"/api/assistant/conversations/{id}/share-link", null);
        var token = (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var joined = await colleague.PostAsJsonAsync($"/api/assistant/conversations/{id}/join", new { token });
        joined.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync(content)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/assistant/conversations/{id}/participants/user-2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync(content)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/assistant/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(files)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(content)).StatusCode);
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/assistant/conversations", new { agentId = "project-assistant" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
