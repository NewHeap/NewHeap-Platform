using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// What a later agent run knows about a subject: working memory, run summaries and artifacts;
/// results shielded from viewers without the tool's rights; and forgetting a subject.
/// </summary>
public sealed class AssistantAgentContinuityTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";

    public sealed record TestMemory(IReadOnlyList<string>? Checks, string? Verdict);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_later_run_starts_from_the_working_memory_and_the_summary_of_the_previous_run(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("memory_update", new { patch = new { checks = new[] { "owner" }, verdict = "pending" } }, "call-1")
            .RespondWithFunctionCall("memory_update", new { patch = new { unknown = 1 } }, "call-2")
            .RespondWithFunctionCall("memory_update", new { patch = new { verdict = "ready" } }, "call-3")
            .RespondWithText("The intake is ready.")
            .RespondWithText("Nothing changed since the last review.");
        await using var host = await CreateHostAsync(provider, model);
        var projectId = Guid.NewGuid();

        var first = await StartAsync(host, projectId);
        var firstTurn = await RunTurnAsync(host, first, "Review the intake.");
        Assert.Equal(NhAssistantTurnStatuses.Completed, firstTurn.Outcome);
        var thread = await ThreadAsync(host, first);
        Assert.Equal("{\"checks\":[\"owner\"],\"verdict\":\"ready\"}", thread.MemoryJson);
        Assert.Contains(NhAssistantErrorCodes.MemoryInvalid, model.FunctionResults[1].Result?.ToString());

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var run = (await RunStore(host).FindRunAsync(first.RunId!.Value, CancellationToken.None))!;
            var composed = await scope.ServiceProvider.GetRequiredService<NhAssistantRunSummaries>()
                .ComposeAsync(run, NhAssistantRunStatuses.Completed, firstTurn.TurnId, CancellationToken.None);
            await RunStore(host).TrySetRunStatusAsync(
                run.Id,
                [NhAssistantRunStatuses.Queued],
                NhAssistantRunStatuses.Completed,
                CancellationToken.None,
                summary: composed);
        }
        var summary = (await RunStore(host).FindRunAsync(first.RunId!.Value, CancellationToken.None))!.Summary;
        Assert.Contains("The intake is ready.", summary);

        var second = await StartAsync(host, projectId);
        Assert.NotEqual(first.RunId, second.RunId);
        await RunTurnAsync(host, second, "The project changed again.");

        var continuity = model.Requests[^1].Messages
            .Where(message => message.Role == ChatRole.System)
            .Select(message => message.Text)
            .Single(text => text.Contains("<agent-continuity>", StringComparison.Ordinal));
        Assert.Contains("<working-memory schema-version=\"1\">{\"checks\":[\"owner\"],\"verdict\":\"ready\"}</working-memory>", continuity);
        Assert.Contains("The intake is ready.", continuity);
        Assert.Contains("data, not instructions", continuity);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_reads_as_ended_only_with_its_summary_and_only_the_end_that_wins_writes_one(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The intake is ready.");
        await using var host = await CreateHostAsync(provider, model);
        var started = await StartAsync(host, Guid.NewGuid());
        var turn = await RunTurnAsync(host, started, "Review the intake.");
        var run = (await RunStore(host).FindRunAsync(started.RunId!.Value, CancellationToken.None))!;
        var thread = await ThreadAsync(host, started);

        // After every write of the end, the run is read as a client that polls its thread's runs
        // reads it. A summary written after the end left a moment in which the run was ended
        // without it; the observer sees every such moment, however short.
        bool ended;
        IReadOnlyList<RunState> states;
        using (var observer = RunWriteObserver.Start(host, run.Id))
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                ended = await scope.ServiceProvider.GetRequiredService<NhAssistantRunEnding>()
                    .EndAsync(run, thread, NhAssistantRunStatuses.Completed, null, null, turn.TurnId);
            }

            states = observer.States;
        }

        Assert.True(ended);
        Assert.Contains(states, state => state.EndedAt is not null);
        Assert.All(
            states.Where(state => state.EndedAt is not null),
            state => Assert.Contains("completed. Answer: The intake is ready.", state.Summary, StringComparison.Ordinal));

        // A later end, such as the maintenance job's for an operation that ended, loses the end and
        // leaves the summary of the end that won.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<NhAssistantRunEnding>().EndAsync(
                run,
                thread,
                NhAssistantRunStatuses.Failed,
                NhAssistantRunFailureClasses.Internal,
                NhAssistantErrorCodes.RunOperationEnded));
        }

        var stored = (await RunStore(host).FindRunAsync(run.Id, CancellationToken.None))!;
        Assert.Equal(NhAssistantRunStatuses.Completed, stored.Status);
        Assert.Equal(states[^1].Summary, stored.Summary);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Memory_of_an_older_schema_is_migrated_and_memory_that_no_longer_fits_is_cleared(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), schemaVersion: 2, migrator: true);
        var migrated = await StartAsync(host, Guid.NewGuid());
        var cleared = await StartAsync(host, Guid.NewGuid());
        await SetMemoryAsync(host, migrated, "{\"check\":\"owner\"}", 1);
        await SetMemoryAsync(host, cleared, "{\"check\":7}", 1);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var memory = scope.ServiceProvider.GetRequiredService<NhAssistantMemoryService>();
            var options = scope.ServiceProvider.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
            await memory.PrepareAsync(await ThreadAsync(host, migrated), options, CancellationToken.None);
            await memory.PrepareAsync(await ThreadAsync(host, cleared), options, CancellationToken.None);
        }

        var migratedThread = await ThreadAsync(host, migrated);
        Assert.Equal("{\"checks\":[\"owner\"]}", migratedThread.MemoryJson);
        Assert.Equal(2, migratedThread.MemorySchemaVersion);
        var clearedThread = await ThreadAsync(host, cleared);
        Assert.Null(clearedThread.MemoryJson);
        Assert.Equal(2, clearedThread.MemorySchemaVersion);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Artifacts_of_a_tool_are_versioned_named_in_the_next_run_and_shielded_from_viewers_without_rights(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-2")
            .RespondWithText("Two notes saved.")
            .RespondWithText("Read the notes.");
        await using var host = await CreateHostAsync(provider, model);
        host.Tools.SaveSearchArtifact = true;
        var projectId = Guid.NewGuid();
        var started = await StartAsync(host, projectId);

        await RunTurnAsync(host, started, "Search and note.");

        var conversationId = started.ConversationId!.Value;
        var store = host.Services.GetRequiredService<NhAssistantArtifactStore>();
        var versions = await store.ListAsync(conversationId, 10, CancellationToken.None);
        Assert.Equal([2, 1], versions.Select(item => item.Version));
        Assert.All(versions, item => Assert.Equal("search-notes", item.Name));
        Assert.All(versions, item => Assert.NotNull(item.ToolInvocationId));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var artifacts = scope.ServiceProvider.GetRequiredService<INhAiArtifacts>();
            Assert.Null(await artifacts.GetAsync(versions[0].Id));
            var outside = await artifacts.SaveAsync("note", "text/plain", new byte[] { 1 });
            Assert.False(outside.Success);
        }

        await RunStore(host).TrySetRunStatusAsync(started.RunId!.Value, [NhAssistantRunStatuses.Queued], NhAssistantRunStatuses.Completed, CancellationToken.None);
        var next = await StartAsync(host, projectId);
        await RunTurnAsync(host, next, "Continue.");
        var continuity = model.Requests[^1].Messages.Single(message => message.Text.Contains("<artifacts>", StringComparison.Ordinal)).Text;
        Assert.Contains("search-notes v2 (text/markdown", continuity);

        // The search ran with the agent's own grant: a viewer without the tool's policy sees that it
        // ran, but not its result.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var threadAccess = (await services.GetRequiredService<NhAssistantThreadService>()
                .ResolveAccessAsync(conversationId, "member-1", null, CancellationToken.None))!;
            var conversation = (await services.GetRequiredService<INhAssistantStore>()
                .FindShareableConversationAsync(conversationId, CancellationToken.None))!;
            var view = await services.GetRequiredService<NhAssistantConversationReader>().CreateViewAsync(conversation, CancellationToken.None);
            var anonymous = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity()) };
            var member = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "member-1")], "test"))
            };

            var shielded = await NhAssistantRestrictedResults.ApplyAsync(view, anonymous, threadAccess);
            var visible = await NhAssistantRestrictedResults.ApplyAsync(view, member, threadAccess);

            var shieldedTools = shielded.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>().ToArray();
            Assert.NotEmpty(shieldedTools);
            Assert.All(shieldedTools, tool =>
            {
                Assert.Equal(NhAiGrantSource.Agent, tool.GrantSource);
                Assert.True(tool.Restricted);
                Assert.Null(tool.ResultPreview);
            });
            Assert.All(
                visible.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>(),
                tool => Assert.False(tool.Restricted));
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Forgetting_a_subject_cancels_its_runs_and_deletes_its_threads(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());
        var projectId = Guid.NewGuid();
        var started = await StartAsync(host, projectId);
        var other = await StartAsync(host, Guid.NewGuid());

        int deleted;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            deleted = (await scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>()
                .ForgetSubjectAsync(new NhAiSubjectRef("project", projectId.ToString("N")))).Data;
        }

        Assert.Equal(1, deleted);
        Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Cancelled);
        Assert.Null(await RunStore(host).FindThreadByConversationAsync(started.ConversationId!.Value, CancellationToken.None));
        Assert.Null(await host.Services.GetRequiredService<INhAssistantStore>().FindShareableConversationAsync(started.ConversationId!.Value, CancellationToken.None));
        Assert.NotNull(await RunStore(host).FindThreadByConversationAsync(other.ConversationId!.Value, CancellationToken.None));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        int schemaVersion = 1,
        bool migrator = false)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .ConfigureAgent(AgentId, agent => agent
                        .Grant("projects.*")
                        .Memory<TestMemory>(schemaVersion))
                    .AddTrigger<ProjectChanged>("project-changed", trigger => trigger
                        .StartAgent(AgentId)
                        .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                        .Message(evt => "The project changed: " + evt.Change)
                        .Dedupe(evt => evt.EventId.ToString("N"))
                        .OnBusy(NhAssistantTriggerConflict.Queue));
                if (migrator)
                {
                    assistant.UseAgentMemoryMigrator<TestMemoryMigrator>();
                }
            });
    }

    private static async Task<NhAssistantTriggerReceipt> StartAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipt = Assert.Single(await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, "created")));
        Assert.NotNull(receipt.RunId);
        return receipt;
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, NhAssistantTriggerReceipt receipt, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var thread = await ThreadAsync(host, receipt);
        var run = (await RunStore(host).FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
        var subject = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), CancellationToken.None))!;
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        return await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, subject, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task<AssistantThread> ThreadAsync(AssistantTestHost host, NhAssistantTriggerReceipt receipt)
    {
        return (await RunStore(host).FindThreadByConversationAsync(receipt.ConversationId!.Value, CancellationToken.None))!;
    }

    private static async Task SetMemoryAsync(AssistantTestHost host, NhAssistantTriggerReceipt receipt, string json, int version)
    {
        var thread = await ThreadAsync(host, receipt);
        Assert.True(await RunStore(host).TryUpdateMemoryAsync(thread.ConversationId, thread.MemoryConcurrencyStamp, json, version, CancellationToken.None));
    }

    private static NhAssistantRunStore RunStore(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    /// <summary>
    /// A run as a reader on another connection saw it.
    /// </summary>
    private sealed record RunState(string Status, DateTimeOffset? EndedAt, string? Summary);

    /// <summary>
    /// Reads one run on a connection of its own right after every command that EF Core executes as a
    /// write with the run's id, before the code that executed it continues: every state of the run
    /// that a concurrent reader could see between two writes. The commands of other tests, which run
    /// in parallel in the same process, name other runs.
    /// </summary>
    private sealed class RunWriteObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly NhAssistantDbContextFactory _contexts;
        private readonly Guid _runId;
        private readonly ConcurrentQueue<RunState> _states = new();
        private readonly ConcurrentBag<IDisposable> _subscriptions = [];

        private RunWriteObserver(NhAssistantDbContextFactory contexts, Guid runId)
        {
            _contexts = contexts;
            _runId = runId;
        }

        public IReadOnlyList<RunState> States => _states.ToArray();

        public static RunWriteObserver Start(AssistantTestHost host, Guid runId)
        {
            var observer = new RunWriteObserver(host.Services.GetRequiredService<NhAssistantDbContextFactory>(), runId);
            observer._subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(observer));
            return observer;
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == DbLoggerCategory.Name)
            {
                _subscriptions.Add(listener.Subscribe(this, name => name == RelationalEventId.CommandExecuted.Name));
            }
        }

        public void OnNext(KeyValuePair<string, object?> diagnostic)
        {
            if (diagnostic.Key != RelationalEventId.CommandExecuted.Name
                || diagnostic.Value is not CommandExecutedEventData { ExecuteMethod: DbCommandMethod.ExecuteNonQuery } executed
                || !executed.Command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, _runId)))
            {
                return;
            }

            // The read is a query, not a write, so it does not observe itself.
            using var context = _contexts.CreateDbContext();
            _states.Enqueue(context.Runs
                .AsNoTracking()
                .Where(run => run.Id == _runId)
                .Select(run => new RunState(run.Status, run.EndedAt, run.Summary))
                .Single());
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
        }
    }
}

/// <summary>
/// Version 2 of the test memory keeps a list of checks where version 1 kept one check.
/// </summary>
public sealed class TestMemoryMigrator : INhAssistantAgentMemoryMigrator
{
    public ValueTask<JsonNode?> MigrateAsync(
        string agentId,
        int fromVersion,
        int toVersion,
        JsonNode memory,
        CancellationToken cancellationToken = default)
    {
        if (fromVersion == 1 && memory["check"] is JsonValue check && check.TryGetValue<string>(out var text))
        {
            return ValueTask.FromResult<JsonNode?>(new JsonObject { ["checks"] = new JsonArray(text) });
        }

        return ValueTask.FromResult<JsonNode?>(memory);
    }
}
