using System.Net;
using System.Text.Json;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-245 over AG-UI: the project assistant's AG-UI endpoint
/// (<see cref="SampleAssistantComposition.AGUIPath"/>) runs the same agent for an AG-UI client. The thread id
/// names the person's conversation, the run streams AG-UI events, the portal's client tool is a frontend
/// tool call the client runs, and the client's tool message continues the turn, as the portal's own report
/// does in SPM-400. The test plays the AG-UI client with plain HTTP.
/// </summary>
public sealed class AssistantAGUISamplesTests(ClientToolSampleHost host) : IClassFixture<ClientToolSampleHost>
{
    [Fact]
    public async Task SPM_245_an_AG_UI_client_runs_the_project_assistant_and_runs_its_client_tool()
    {
        host.Model.Use(new SampleAssistantChatClient());
        var projectId = Guid.NewGuid();
        using var client = host.CreateClient("spm-245-agui");
        var thread = Guid.NewGuid().ToString();
        // The client names the tools it can run; only ids the API declared count, never their descriptions.
        var tools = new[] { new { name = SampleClientToolsComposition.OpenProjectToolId, description = "Runs in the AG-UI client." } };
        var ask = new { id = "m-1", role = "user", content = $"Open project {projectId}" };

        var opening = await RunAsync(client, new { threadId = thread, runId = "run-1", messages = new[] { ask }, tools });

        Assert.Equal(
            ["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "RUN_FINISHED"],
            opening.Select(TypeOf));
        var call = opening[1];
        Assert.Equal(SampleClientToolsComposition.OpenProjectToolId, call.GetProperty("toolCallName").GetString());
        using (var arguments = JsonDocument.Parse(opening[2].GetProperty("delta").GetString()!))
        {
            Assert.Equal(projectId, arguments.RootElement.GetProperty("projectId").GetGuid());
        }
        // The run ends without an interrupt: the client runs the frontend tool call.
        Assert.False(opening[^1].TryGetProperty("outcome", out _));

        var callId = call.GetProperty("toolCallId").GetString();
        var report = JsonSerializer.Serialize(new { status = "completed", outcome = "navigated" });
        var resumed = await RunAsync(client, new
        {
            threadId = thread,
            runId = "run-2",
            messages = new object[] { ask, new { id = "m-2", role = "tool", toolCallId = callId, content = report } },
            tools
        });

        Assert.Equal("RUN_FINISHED", TypeOf(resumed[^1]));
        Assert.Equal(
            "The project is open in your portal.",
            string.Concat(resumed.Where(evt => TypeOf(evt) == "TEXT_MESSAGE_CONTENT").Select(evt => evt.GetProperty("delta").GetString())));
        // The thread is the person's own assistant conversation, with its history in the assistant's store.
        var conversation = await host.GetJsonAsync(client, $"/api/assistant/conversations/{thread}");
        Assert.Equal(SampleAssistantComposition.AgentId, conversation.GetProperty("agentId").GetString());
        Assert.Equal("idle", conversation.GetProperty("status").GetString());

        // Another person's thread answers as one that does not exist.
        using var stranger = host.CreateClient("spm-245-stranger");
        using var foreign = await stranger.PostAsync(
            SampleAssistantComposition.AGUIPath,
            AssistantSampleHost.Json(new { threadId = thread, runId = "run-3", messages = new[] { new { id = "m-3", role = "user", content = "Hello?" } } }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static string? TypeOf(JsonElement evt)
    {
        return evt.GetProperty("type").GetString();
    }

    /// <summary>
    /// Posts a <c>RunAgentInput</c> and reads the run's AG-UI events, one JSON object per <c>data:</c> line.
    /// </summary>
    private static async Task<IReadOnlyList<JsonElement>> RunAsync(HttpClient client, object input)
    {
        using var response = await client.PostAsync(
            SampleAssistantComposition.AGUIPath,
            AssistantSampleHost.Json(input),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<JsonElement>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line["data: ".Length..]);
                events.Add(document.RootElement.Clone());
            }
        }
        return events;
    }
}
