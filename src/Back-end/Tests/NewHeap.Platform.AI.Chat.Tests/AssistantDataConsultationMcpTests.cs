using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A consultation defined as data reaches MCP clients over real Streamable HTTP (AC8, AC26): when an
/// administrator saves or removes it, a session of a stateful MCP server of
/// <c>WithNewHeapPlatformAITools</c> hears <c>notifications/tools/list_changed</c> and lists the change,
/// and a call of the removed consultation from the client's stale list fails safely before the
/// consulted agent runs (AC12).
/// </summary>
public sealed class AssistantDataConsultationMcpTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string StarterId = "project-starter";
    private const string ConsultTool = "consult_project-starter_project_v1";
    private const string Admin = "admin-1";
    private const string Member = "member-1";
    private const string ProjectId = "project-0001";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task An_mcp_session_hears_when_a_data_consultation_comes_and_goes_and_a_stale_call_never_consults_the_agent()
    {
        var target = new NhAiScriptedChatClient()
            .RespondWithText("The draft can start once the budget is approved.");
        await using var host = await AssistantWebApplication.StartAsync(
            database,
            target,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, KnownAccountPrincipalFactory>());
                // A session per client: the SDK's stateless default cannot tell a client of a change.
                services.AddMcpServer()
                    .WithHttpTransport(options => options.Stateless = false)
                    .WithNewHeapPlatformAITools();
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .UseConsultationsAsData(data => data
                    .AllowRoutes(NhAssistantConsultationRoutes.AskerDecides)
                    .AllowMcp()),
            map: app => app.MapMcp("/mcp"));
        await CreateStarterAsync(host);

        var http = host.CreateClient(Member);
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                http,
                ownsHttpClient: true),
            new McpClientOptions { ProtocolVersion = "2025-11-25" });
        Assert.True(client.ServerCapabilities.Tools?.ListChanged);
        using var changes = new SemaphoreSlim(0);
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                changes.Release();
                return ValueTask.CompletedTask;
            });
        Assert.DoesNotContain(ConsultTool, await ToolNamesAsync(client));

        // An administrator offers the consultation to MCP clients; the session hears it and lists it.
        var saved = await ConfigureAsync(host, configuration => configuration.SaveConsultationAsync(
            StarterId,
            "project",
            new NhAssistantDataConsultationRequest("Knows whether a draft project can start.", ExposeToMcp: true),
            Admin));
        Assert.True(saved.Success);
        Assert.True(await changes.WaitAsync(SignalTimeout));
        var consult = Assert.Single(await client.ListToolsAsync(), tool => tool.Name == ConsultTool);

        var answered = await consult.CallAsync(Question());
        Assert.NotEqual(true, answered.IsError);
        Assert.Single(target.Requests);

        // The administrator removes it; the session hears it and no longer lists it.
        var removed = await ConfigureAsync(host, configuration => configuration.RemoveConsultationAsync(StarterId, "project", 1, Admin));
        Assert.True(removed.Success);
        Assert.True(await changes.WaitAsync(SignalTimeout));
        Assert.DoesNotContain(ConsultTool, await ToolNamesAsync(client));

        // A call from the stale list fails at the export, before the consultation's late binding: the
        // agent is not consulted and nothing is recorded.
        var stale = await consult.CallAsync(Question());
        Assert.True(stale.IsError);
        Assert.Equal("ai-tool-not-authorized", stale.Meta![NhAiMcpResultMetadata.CodeKey]!.GetValue<string>());
        Assert.Single(target.Requests);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal(1, await context.Consultations.CountAsync());
    }

    /// <summary>
    /// The project starter, created in the administration with run settings that let it answer to a
    /// technical account.
    /// </summary>
    private static async Task CreateStarterAsync(AssistantWebApplication host)
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
                new NhAssistantAgentInput(
                    StarterId,
                    "Project starter",
                    "Starts draft projects.",
                    "Say whether a draft project can start.",
                    ["projects.*"],
                    [],
                    null,
                    NhAiAutonomyLevel.Execute),
                Admin,
                CancellationToken.None);
            Assert.True(created.Success);
        }

        var settings = await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
            StarterId,
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, KnownAccountPrincipalFactory.First),
            Admin));
        Assert.True(settings.Success);
    }

    private static async Task<T> ConfigureAsync<T>(AssistantWebApplication host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task<IReadOnlyList<string>> ToolNamesAsync(McpClient client)
    {
        return (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();
    }

    private static Dictionary<string, object?> Question()
    {
        return new Dictionary<string, object?>
        {
            ["input"] = new Dictionary<string, object?>
            {
                ["subjectId"] = ProjectId,
                ["question"] = "Can the draft start?"
            }
        };
    }
}
