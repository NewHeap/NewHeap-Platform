using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for SPM-400: the project assistant's client tools run in the person's portal. The
/// API declares them (<c>AddClientTool</c>); the portal names the tools it can run with its message, the
/// deterministic model opens a project's page or the create-task form through them, the person's own
/// stream carries the arguments, and the portal's report continues the turn. A browser that names no
/// tools is offered none, and someone who may not create tasks never gets the form.
/// </summary>
public sealed class ClientToolSamplesTests(ClientToolSampleHost host) : IClassFixture<ClientToolSampleHost>
{
    private static readonly string[] PortalTools = [SampleClientToolsComposition.OpenProjectToolId, SampleClientToolsComposition.CreateTaskToolId];

    [Fact]
    public async Task SPM_400_the_assistant_hands_a_new_task_to_the_persons_portal_which_reports_that_it_was_saved()
    {
        host.Model.Use(new SampleAssistantChatClient());
        var projectId = Guid.NewGuid();
        using var client = host.CreateClient("spm-400-manager", permissions: SampleClientToolsComposition.CreateTaskPolicy);
        var conversationId = await host.CreateConversationAsync(client);

        var events = await SendAsync(client, conversationId, $"Create task Book the venue for project {projectId}", PortalTools);

        Assert.Equal("waiting-for-approval", events[^1].Data.GetProperty("status").GetString());
        var requested = events.Single(evt => evt.Name == "client-tool.requested").Data;
        Assert.Equal(SampleClientToolsComposition.CreateTaskToolId, requested.GetProperty("toolId").GetString());
        Assert.Equal(projectId, requested.GetProperty("arguments").GetProperty("projectId").GetGuid());
        Assert.Equal("Book the venue", requested.GetProperty("arguments").GetProperty("title").GetString());
        var conversation = await host.GetJsonAsync(client, $"/api/assistant/conversations/{conversationId}");
        Assert.False(conversation.GetProperty("pendingClientTool").TryGetProperty("arguments", out _));

        var (status, resumed) = await ReportAsync(client, conversationId, requested, new { status = "completed", outcome = "saved", id = "task-7" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("completed", resumed[^1].Data.GetProperty("status").GetString());
        Assert.Equal("You saved the task in your portal.", Text(resumed));
    }

    [Fact]
    public async Task SPM_400_the_assistant_opens_a_project_in_the_portal_only_when_the_portal_can_run_it()
    {
        host.Model.Use(new SampleAssistantChatClient());
        var projectId = Guid.NewGuid();
        using var client = host.CreateClient("spm-400-reader");
        var conversationId = await host.CreateConversationAsync(client);

        var opening = await SendAsync(client, conversationId, $"Open project {projectId}", PortalTools);
        var requested = opening.Single(evt => evt.Name == "client-tool.requested").Data;
        Assert.Equal(SampleClientToolsComposition.OpenProjectToolId, requested.GetProperty("toolId").GetString());
        var (_, opened) = await ReportAsync(client, conversationId, requested, new { status = "completed", outcome = "navigated" });
        Assert.Equal("The project is open in your portal.", Text(opened));

        // A browser that names no tools, such as another app, is offered none: the model searches instead.
        var elsewhere = await SendAsync(client, conversationId, $"Open project {projectId}", []);
        Assert.DoesNotContain(elsewhere, evt => evt.Name == "client-tool.requested");
        Assert.Contains(elsewhere, evt => evt.Name == "tool.started" && evt.Data.GetProperty("toolId").GetString() == "projects.search");
        Assert.Equal("completed", elsewhere[^1].Data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SPM_400_someone_who_may_not_create_tasks_never_gets_the_form()
    {
        host.Model.Use(new SampleAssistantChatClient());
        using var client = host.CreateClient("spm-400-viewer");
        var conversationId = await host.CreateConversationAsync(client);

        var events = await SendAsync(client, conversationId, $"Create task Book the venue for project {Guid.NewGuid()}", PortalTools);

        // The invocation gate checks the person's policy before anything reaches the portal.
        Assert.DoesNotContain(events, evt => evt.Name == "client-tool.requested");
        Assert.Equal("completed", events[^1].Data.GetProperty("status").GetString());
    }

    private static async Task<IReadOnlyList<SampleServerSentEvent>> SendAsync(
        HttpClient client,
        Guid conversationId,
        string text,
        string[] clientTools)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/messages",
            AssistantSampleHost.Json(new { text, clientMessageId = Guid.NewGuid().ToString("N"), clientTools }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadEventsAsync(response);
    }

    private static async Task<(HttpStatusCode Status, IReadOnlyList<SampleServerSentEvent> Events)> ReportAsync(
        HttpClient client,
        Guid conversationId,
        JsonElement requested,
        object report)
    {
        using var response = await client.PostAsync(
            $"/api/assistant/conversations/{conversationId}/client-tools/{requested.GetProperty("invocationId").GetGuid()}/result",
            AssistantSampleHost.Json(report),
            TestContext.Current.CancellationToken);
        return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? await ReadEventsAsync(response) : []);
    }

    private static async Task<IReadOnlyList<SampleServerSentEvent>> ReadEventsAsync(HttpResponseMessage response)
    {
        var events = new List<SampleServerSentEvent>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        string? name = null;
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && name is not null)
            {
                using var document = JsonDocument.Parse(line["data: ".Length..]);
                events.Add(new SampleServerSentEvent(name, document.RootElement.Clone()));
                name = null;
            }
        }
        return events;
    }

    private static string Text(IEnumerable<SampleServerSentEvent> events)
    {
        return string.Concat(events
            .Where(evt => evt.Name == "message.delta")
            .Select(evt => evt.Data.GetProperty("text").GetString()));
    }
}

/// <summary>
/// The assistant sample with the client tools of Program.cs and the API's policy of creating tasks, which
/// the test sign-in grants with the permission claim.
/// </summary>
public sealed class ClientToolSampleHost(SamplePostgreSqlServer postgreSql) : AssistantSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        services.AddSampleClientTools();
    }

    protected override void ConfigureAuthorization(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        base.ConfigureAuthorization(options);
        options.AddPolicy(
            SampleClientToolsComposition.CreateTaskPolicy,
            policy => policy.RequireClaim("permission", SampleClientToolsComposition.CreateTaskPolicy));
    }
}
