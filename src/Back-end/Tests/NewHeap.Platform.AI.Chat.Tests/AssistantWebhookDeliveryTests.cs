using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A webhook delivery starts at most one run, also once the run that took it claimed it: a delivery
/// that joined a pending run, and one that waited while the agent was paused, is deduplicated when it
/// is delivered again after that run took it, on both relational providers.
/// </summary>
public sealed class AssistantWebhookDeliveryTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string TriggerId = "review-board";
    private const string Secret = "a-review-board-secret-for-tests-only-123";
    private const string Admin = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_delivery_that_joined_a_pending_run_starts_no_second_run_when_it_is_delivered_again(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var board = app.CreateClient(user: null, access: false);
        var project = Guid.NewGuid().ToString("N");

        // The first delivery starts a run that waits out the debounce; the second joins it.
        var first = await DeliverAsync(board, project, "delivery-a");
        Assert.Equal(NhAssistantTriggerOutcomes.Started, first.Outcome);
        var joined = await DeliverAsync(board, project, "delivery-b");
        Assert.Equal((NhAssistantTriggerOutcomes.Coalesced, first.RunId), (joined.Outcome, joined.RunId));

        // The run starts, takes both deliveries and ends.
        await ClaimAndCompleteAsync(app, first.ConversationId!.Value, first.RunId!.Value);

        // Delivered again, both are known: no second run processes them.
        var again = await DeliverAsync(board, project, "delivery-b");
        Assert.Equal((NhAssistantTriggerOutcomes.Deduplicated, first.RunId), (again.Outcome, again.RunId));
        Assert.Equal(NhAssistantTriggerOutcomes.Deduplicated, (await DeliverAsync(board, project, "delivery-a")).Outcome);
        await using var context = Context(app);
        Assert.Single(await context.Runs.Where(run => run.ConversationId == first.ConversationId).ToListAsync());
        Assert.Equal(2, await context.QueuedInputs.CountAsync(input => input.ConversationId == first.ConversationId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_delivery_that_waited_for_a_paused_agent_starts_no_second_run_when_it_is_delivered_again(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        using var board = app.CreateClient(user: null, access: false);
        var project = Guid.NewGuid().ToString("N");
        await ControlAsync(app, control => control.PauseAsync(AgentId, Admin));

        // Both deliveries wait while the agent is paused; the resume starts one run that takes them.
        var waiting = await DeliverAsync(board, project, "delivery-c");
        Assert.Equal(NhAssistantTriggerOutcomes.Waiting, waiting.Outcome);
        Assert.Equal(NhAssistantTriggerOutcomes.Waiting, (await DeliverAsync(board, project, "delivery-d")).Outcome);
        await ControlAsync(app, control => control.ResumeAsync(AgentId, Admin));
        Guid resumed;
        await using (var context = Context(app))
        {
            resumed = (await context.Runs.SingleAsync(run => run.ConversationId == waiting.ConversationId)).Id;
        }
        await ClaimAndCompleteAsync(app, waiting.ConversationId!.Value, resumed);

        // The resume run names the first delivery only; the second is known through its input.
        var again = await DeliverAsync(board, project, "delivery-d");
        Assert.Equal((NhAssistantTriggerOutcomes.Deduplicated, (Guid?)resumed), (again.Outcome, again.RunId));
        await using (var context = Context(app))
        {
            Assert.Single(await context.Runs.Where(run => run.ConversationId == waiting.ConversationId).ToListAsync());
        }
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                // Runs are recorded, not executed: the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(AgentId, run => run.WorksOn("project"))
                // The defaults: a delivery joins a pending run, which waits two minutes for more.
                .AddWebhookTrigger(TriggerId, webhook => webhook
                    .StartAgent(AgentId)
                    .Secret(_ => Secret)
                    .Subject(body => new NhAiSubjectRef("project", body.GetProperty("projectId").GetString()!))
                    .Message(_ => "The review board answered.")),
            provider: provider);
    }

    /// <summary>
    /// Posts one delivery for the project, signed as the review board signs it.
    /// </summary>
    private static async Task<NhAssistantTriggerReceiptDto> DeliverAsync(HttpClient client, string projectId, string deliveryId)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { projectId });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signed = Encoding.UTF8.GetBytes(timestamp + "." + deliveryId + ".");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), (byte[])[.. signed, .. body]));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assistant/triggers/" + TriggerId)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(NhAssistantWebhookEndpoints.TimestampHeader, timestamp);
        request.Headers.Add(NhAssistantWebhookEndpoints.DeliveryHeader, deliveryId);
        request.Headers.Add(NhAssistantWebhookEndpoints.SignatureHeader, "sha256=" + signature);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>())!;
    }

    /// <summary>
    /// Takes the thread's waiting inputs for the run, as the run does when it starts, and ends it.
    /// </summary>
    private static async Task ClaimAndCompleteAsync(AssistantWebApplication app, Guid conversationId, Guid runId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>();
        Assert.Equal(2, (await runs.ClaimQueuedInputsAsync(conversationId, runId, CancellationToken.None)).Count);
        Assert.True(await runs.TrySetRunStatusAsync(runId, [NhAssistantRunStatuses.Queued], NhAssistantRunStatuses.Completed, CancellationToken.None));
    }

    private static async Task ControlAsync(
        AssistantWebApplication app,
        Func<INhAssistantAgentControl, Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantAgentState>>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True((await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>())).Success);
    }

    private static NhAssistantDbContext Context(AssistantWebApplication app)
    {
        return app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
    }
}
