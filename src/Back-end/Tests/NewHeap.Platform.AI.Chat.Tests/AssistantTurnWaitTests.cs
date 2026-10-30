using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB67, FB69 and FB70: a channel follows what its turn does, waits for a turn it no longer holds
/// without polling, and sees where the time of a turn went. The wait applies the rules of a turn: only
/// a person who may read the chat waits, and access that ends also ends the wait.
/// </summary>
public sealed class AssistantTurnWaitTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ChannelCode = "messaging-chat";
    private const string SearchFunction = "projects_search_v1";
    private const string SecretQuery = "the-secret-query";

    [Fact]
    public async Task A_channel_follows_its_turn_and_learns_no_arguments_or_results()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = SecretQuery } })
            .RespondWithText("One project matches.");
        await using var app = await StartAsync(model);
        var id = await AddConversationAsync(app, "user-1");
        var progress = new CollectedProgress();

        var sent = await SendAsync(app, Person("user-1"), id, new NhAssistantConversationTurnOptions(ChannelCode) { Progress = progress });

        Assert.True(sent.Success, CodeOf(sent));
        var kinds = progress.Steps.Select(step => step.Kind).ToArray();
        Assert.Equal(NhAssistantTurnProgressKinds.Started, kinds[0]);
        Assert.Equal(NhAssistantTurnProgressKinds.Ended, kinds[^1]);
        Assert.True(
            Array.IndexOf(kinds, NhAssistantTurnProgressKinds.ToolStarted) < Array.IndexOf(kinds, NhAssistantTurnProgressKinds.ToolCompleted)
            && Array.IndexOf(kinds, NhAssistantTurnProgressKinds.ToolCompleted) < Array.IndexOf(kinds, NhAssistantTurnProgressKinds.Text));
        var started = progress.Steps.Single(step => step.Kind == NhAssistantTurnProgressKinds.ToolStarted);
        var completed = progress.Steps.Single(step => step.Kind == NhAssistantTurnProgressKinds.ToolCompleted);
        Assert.Equal("projects.search", started.Tool!.ToolId);
        Assert.Equal(started.Tool.InvocationId, completed.Tool!.InvocationId);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, completed.Tool.Status);
        Assert.Equal("One project matches.", string.Concat(progress.Steps.Select(step => step.Text)));
        Assert.All(progress.Steps, step => Assert.Equal(sent.Data!.TurnId, step.TurnId));
        Assert.Equal(NhAssistantTurnStatuses.Completed, progress.Steps[^1].Status);
        Assert.DoesNotContain(SecretQuery, JsonSerializer.Serialize(progress.Steps), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_progress_handler_that_throws_stops_nothing_and_is_asked_no_further()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The answer has several words in it.");
        await using var app = await StartAsync(model);
        var id = await AddConversationAsync(app, "user-1");
        var failing = new FailingProgress();

        var sent = await SendAsync(app, Person("user-1"), id, new NhAssistantConversationTurnOptions(ChannelCode) { Progress = failing });

        Assert.True(sent.Success, CodeOf(sent));
        Assert.Equal(NhAssistantTurnStatuses.Completed, sent.Data!.Status);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task A_turn_shows_where_its_time_went_in_its_result_and_its_log()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = SecretQuery } })
            .RespondWithText("One project matches.");
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Find the roadmap.");

        var timings = turn.Single<NhAssistantTurnCompletedEvent>().Timings!;
        Assert.Equal(2, timings.ModelCalls);
        Assert.True(timings.Tools > TimeSpan.Zero);
        Assert.True(timings.Model > TimeSpan.Zero);
        Assert.True(timings.Preparation > TimeSpan.Zero);
        Assert.True(timings.Total >= timings.Preparation);
        Assert.True(timings.FirstText is { } firstText && firstText >= timings.Preparation && firstText <= timings.Total);
        var line = Assert.Single(host.Logs.Messages, message => message.Contains("tool calls in", StringComparison.Ordinal));
        Assert.Contains("preparation", line, StringComparison.Ordinal);
        Assert.Contains("in 2 calls", line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretQuery, line, StringComparison.Ordinal);
        Assert.DoesNotContain("One project matches.", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_that_never_reached_its_model_spent_its_time_preparing()
    {
        var timings = new NhAssistantTurnClock().Snapshot();

        Assert.Equal(timings.Total, timings.Preparation);
        Assert.Null(timings.FirstText);
        Assert.Equal(0, timings.ModelCalls);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_wait_follows_a_turn_its_caller_gave_up_on_and_returns_the_answer()
    {
        var model = new GatedChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "roadmap" } })
            .RespondWithText("Found the roadmap."));
        await using var app = await StartAsync(model);
        var id = await AddConversationAsync(app, "user-1");
        var person = Person("user-1");
        using var gaveUp = new CancellationTokenSource();
        var sending = SendAsync(app, person, id, new NhAssistantConversationTurnOptions(ChannelCode) { KeepTurnAfterCallerLeaves = true }, gaveUp.Token);
        await model.WaitForTurnAsync();
        await gaveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        // The bound passes first: the turn still runs and nothing else is known yet.
        var running = await WaitAsync(app, person, id, new NhAssistantTurnWait(TimeSpan.Zero));
        Assert.True(running.Success, CodeOf(running));
        Assert.Equal(NhAssistantTurnStatuses.Running, running.Data!.Status);
        Assert.NotEqual(Guid.Empty, running.Data.TurnId);
        Assert.Null(running.Data.Message);

        var progress = new CollectedProgress();
        var waiting = WaitAsync(app, person, id, new NhAssistantTurnWait(TimeSpan.FromSeconds(60)) { Progress = progress });
        Assert.False(waiting.IsCompleted);
        await app.Services.GetRequiredService<NhAssistantTurnWatchers>().WaitForWatchAsync(id);
        model.Release();
        var ended = await waiting.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(ended.Success, CodeOf(ended));
        Assert.Equal(NhAssistantTurnStatuses.Completed, ended.Data!.Status);
        Assert.Equal(running.Data.TurnId, ended.Data.TurnId);
        Assert.Equal("Found the roadmap.", TextOf(ended.Data.Message!));
        Assert.Equal(1, ended.Data.Usage.ToolCalls);
        Assert.NotNull(ended.Data.Timings);
        Assert.Contains(progress.Steps, step => step.Kind == NhAssistantTurnProgressKinds.ToolStarted);
        Assert.Equal(NhAssistantTurnProgressKinds.Ended, progress.Steps[^1].Kind);
    }

    [Fact]
    public async Task A_wait_for_a_chat_without_a_running_turn_answers_from_the_conversation()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("All done.");
        await using var app = await StartAsync(model);
        var id = await AddConversationAsync(app, "user-1");
        var person = Person("user-1");

        var empty = await WaitAsync(app, person, id, new NhAssistantTurnWait(TimeSpan.FromMinutes(5)));
        Assert.True(empty.Success, CodeOf(empty));
        Assert.Equal((NhAssistantTurnStatuses.Completed, Guid.Empty), (empty.Data!.Status, empty.Data.TurnId));
        Assert.Null(empty.Data.Message);

        var sent = await SendAsync(app, person, id, new NhAssistantConversationTurnOptions(ChannelCode));
        var read = await WaitAsync(app, person, id, new NhAssistantTurnWait(TimeSpan.FromMinutes(5)));

        Assert.True(read.Success, CodeOf(read));
        Assert.Equal(NhAssistantTurnStatuses.Completed, read.Data!.Status);
        Assert.Equal(sent.Data!.TurnId, read.Data.TurnId);
        Assert.Equal("All done.", TextOf(read.Data.Message!));
        Assert.Null(read.Data.Timings);
    }

    [Fact]
    public async Task A_wait_shows_the_approval_a_turn_ended_at()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } })
            .RespondWithText("Waiting for your decision.");
        await using var app = await StartAsync(model);
        var id = await AddConversationAsync(app, "user-1");
        var person = Person("user-1");

        var sent = await SendAsync(app, person, id, new NhAssistantConversationTurnOptions(ChannelCode));
        var read = await WaitAsync(app, person, id, new NhAssistantTurnWait(TimeSpan.Zero));

        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, sent.Data!.Status);
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, read.Data!.Status);
        Assert.Equal(sent.Data.PendingApprovals.Single().ApprovalId, read.Data.PendingApprovals.Single().ApprovalId);
    }

    [Fact]
    public async Task A_wait_sees_the_end_of_a_turn_on_another_host()
    {
        await using var app = await StartAsync(new NhAiScriptedChatClient(), checkInterval: TimeSpan.FromMilliseconds(20));
        var id = await AddConversationAsync(app, "user-1");
        var turnId = Guid.NewGuid();
        await SetStatusAsync(app, id, NhAssistantConversationStatuses.Running, turnId);
        var progress = new CollectedProgress();

        var waiting = WaitAsync(app, Person("user-1"), id, new NhAssistantTurnWait(TimeSpan.FromSeconds(60)) { Progress = progress });
        await app.Services.GetRequiredService<NhAssistantTurnWatchers>().WaitForWatchAsync(id);
        await SetStatusAsync(app, id, NhAssistantConversationStatuses.Failed, null);
        var ended = await waiting.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(ended.Success, CodeOf(ended));
        Assert.Equal(NhAssistantTurnStatuses.Failed, ended.Data!.Status);
        Assert.Empty(progress.Steps);
    }

    [Fact]
    public async Task Access_that_ends_during_a_wait_ends_the_wait()
    {
        await using var app = await StartAsync(new NhAiScriptedChatClient(), checkInterval: TimeSpan.FromMilliseconds(20));
        var id = await AddConversationAsync(app, "user-1");
        await SetStatusAsync(app, id, NhAssistantConversationStatuses.Running, Guid.NewGuid());

        var waiting = WaitAsync(app, Person("user-1"), id, new NhAssistantTurnWait(TimeSpan.FromSeconds(60)));
        await app.Services.GetRequiredService<NhAssistantTurnWatchers>().WaitForWatchAsync(id);
        await SetStatusAsync(app, id, NhAssistantConversationStatuses.Archived, null);
        var ended = await waiting.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(ended.Success);
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(ended));
    }

    [Fact]
    public async Task A_wait_is_refused_as_a_turn_is()
    {
        await using var app = await StartAsync(new NhAiScriptedChatClient());
        var id = await AddConversationAsync(app, "user-1");
        var wait = new NhAssistantTurnWait(TimeSpan.Zero);

        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await WaitAsync(app, Person("user-2"), id, wait)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await WaitAsync(app, Person("user-1"), Guid.NewGuid(), wait)));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await WaitAsync(app, PersonWithoutAccess("user-1"), id, wait)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WaitAsync(app, Person("user-1"), id, new NhAssistantTurnWait(TimeSpan.FromSeconds(-1))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WaitAsync(app, Person("user-1"), id, new NhAssistantTurnWait(NhAssistantTurnWait.MaxTimeout + TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public async Task The_callers_cancellation_ends_the_wait_and_leaves_the_turn_alone()
    {
        var model = new GatedChatClient(new NhAiScriptedChatClient().RespondWithText("Still done."));
        await using var app = await StartAsync(model);
        var id = await AddConversationAsync(app, "user-1");
        var person = Person("user-1");
        var sending = SendAsync(app, person, id, new NhAssistantConversationTurnOptions(ChannelCode));
        await model.WaitForTurnAsync();
        using var leaving = new CancellationTokenSource();

        var waiting = WaitAsync(app, person, id, new NhAssistantTurnWait(TimeSpan.FromSeconds(60)), leaving.Token);
        await app.Services.GetRequiredService<NhAssistantTurnWatchers>().WaitForWatchAsync(id);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        model.Release();
        var sent = await sending.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(NhAssistantTurnStatuses.Completed, sent.Data!.Status);
    }

    [Fact]
    public async Task Watchers_hear_a_turn_until_they_stop_and_a_stopped_watch_is_gone()
    {
        var watchers = new NhAssistantTurnWatchers();
        var id = Guid.NewGuid();
        var first = watchers.Watch(id);
        var second = watchers.Watch(id);
        var evt = new NhAssistantMessageDeltaEvent(Guid.NewGuid(), "text");

        watchers.Publish(id, evt);
        watchers.Publish(Guid.NewGuid(), evt);
        first.Dispose();
        watchers.Publish(id, evt);

        Assert.True(first.Events.TryRead(out _));
        Assert.False(first.Events.TryRead(out _));
        Assert.True(second.Events.TryRead(out _));
        Assert.True(second.Events.TryRead(out _));
        second.Dispose();
        Assert.False(watchers.IsWatching(id));
        await Task.CompletedTask;
    }

    private Task<AssistantWebApplication> StartAsync(IChatClient model, TimeSpan? checkInterval = null)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                if (checkInterval is { } interval)
                {
                    services.AddSingleton(new NhAssistantTiming { TurnWaitCheckInterval = interval });
                }
            });
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> SendAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationTurnOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().SendAsync(
            person,
            conversationId,
            new NhAssistantConversationMessage("Search the projects."),
            options,
            cancellationToken);
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> WaitAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantTurnWait wait,
        CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>()
            .WaitAsync(person, conversationId, wait, cancellationToken);
    }

    private static async Task<Guid> AddConversationAsync(AssistantWebApplication app, string owner)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = owner,
            AgentId = "project-assistant",
            AgentVersion = 1,
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().AddConversationAsync(conversation, CancellationToken.None);
        return conversation.Id;
    }

    /// <summary>
    /// Sets the conversation as a host would that runs its turn, so this host sees no event of it.
    /// </summary>
    private static async Task SetStatusAsync(AssistantWebApplication app, Guid conversationId, string status, Guid? turnId)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var conversation = await context.Conversations.FindAsync(conversationId);
        conversation!.Status = status;
        conversation.ActiveTurnId = turnId;
        await context.SaveChangesAsync();
    }

    private static NhAssistantDecidingPerson Person(string user)
    {
        return new NhAssistantDecidingPerson(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user), new Claim("permission", "assistant")], ChannelCode)),
            user,
            null);
    }

    private static NhAssistantDecidingPerson PersonWithoutAccess(string user)
    {
        return new NhAssistantDecidingPerson(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], ChannelCode)),
            user,
            null);
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static string TextOf(NhAssistantMessageDto message)
    {
        return string.Concat(message.Parts.OfType<NhAssistantTextPartDto>().Select(part => part.Text));
    }

    private sealed class CollectedProgress : IProgress<NhAssistantTurnProgress>
    {
        private readonly ConcurrentQueue<NhAssistantTurnProgress> _steps = new();

        public IReadOnlyList<NhAssistantTurnProgress> Steps => _steps.ToArray();

        public void Report(NhAssistantTurnProgress value)
        {
            _steps.Enqueue(value);
        }
    }

    private sealed class FailingProgress : IProgress<NhAssistantTurnProgress>
    {
        public int Calls { get; private set; }

        public void Report(NhAssistantTurnProgress value)
        {
            Calls++;
            throw new InvalidOperationException("The channel went away.");
        }
    }

    /// <summary>
    /// Holds the first model call of a turn until the test releases it; calls after a tool pass at once.
    /// </summary>
    private sealed class GatedChatClient(IChatClient inner) : IChatClient
    {
        private readonly SemaphoreSlim _gate = new(0);
        private readonly SemaphoreSlim _started = new(0);

        public Task WaitForTurnAsync()
        {
            return _started.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public void Release()
        {
            _gate.Release();
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            if (list.Count > 0 && list[^1].Role == ChatRole.User)
            {
                _started.Release();
                await _gate.WaitAsync(cancellationToken);
            }

            await foreach (var update in inner.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return inner.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            _gate.Dispose();
            _started.Dispose();
        }
    }
}

internal static class NhAssistantTurnWatchersTestExtensions
{
    /// <summary>
    /// Waits until a caller watches the conversation, so what the test does next reaches it.
    /// </summary>
    public static async Task WaitForWatchAsync(this NhAssistantTurnWatchers watchers, Guid conversationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!watchers.IsWatching(conversationId))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Nobody started watching the conversation.");
            await Task.Yield();
        }
    }
}
