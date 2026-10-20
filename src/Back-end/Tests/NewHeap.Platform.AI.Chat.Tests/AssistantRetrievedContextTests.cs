using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Retrieved context in agent turns: once per turn through the resolver, within its bounds and the
/// turn's scopes and classification, as the last turn data block, with a content-free trace.
/// </summary>
public sealed class AssistantRetrievedContextTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string SourceId = "test-documents";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_persons_turn_retrieves_once_within_its_scopes_and_classification_after_the_situation(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("The roadmap is late.");
        await using var host = await CreateHostAsync(provider, model);
        var documents = host.Services.GetRequiredService<TestDocuments>();

        var conversation = await host.CreateConversationAsync();
        await host.SendAsync(conversation.Id, "What does the roadmap say?");

        var request = Assert.Single(documents.Requests);
        Assert.Equal("What does the roadmap say?", request.Query);
        Assert.Equal(NhAiDataClassification.Internal, request.MaximumClassification);
        Assert.Equal(8, request.MaxItems);
        var instructions = model.Requests[0].Options!.Instructions!;
        var retrieved = instructions[instructions.IndexOf("# Retrieved context", StringComparison.Ordinal)..];
        Assert.True(instructions.IndexOf("# Situation", StringComparison.Ordinal) < instructions.IndexOf("# Retrieved context", StringComparison.Ordinal));
        Assert.Contains("untrusted, may be outdated or wrong", retrieved);
        Assert.Contains("The roadmap moved to May.", retrieved);
        Assert.Contains("effectiveAt", retrieved);
        Assert.DoesNotContain("Another division's plan.", retrieved);
        Assert.DoesNotContain("A confidential plan.", retrieved);
        Assert.DoesNotContain("<b>", retrieved);
        // The tool round reuses the turn's context.
        Assert.Equal(instructions, model.Requests[1].Options!.Instructions);
        var invoked = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked);
        Assert.NotNull(invoked.RetrievedContextHash);
        var trace = Assert.Single(host.Logs.Messages, message => message.Contains("Context for invocation", StringComparison.Ordinal));
        Assert.Contains(SourceId + ":source-items-filtered:1", trace);
        Assert.DoesNotContain("roadmap", trace, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_headless_run_retrieves_with_its_inputs_and_subject_as_the_agent(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Checked the documents.");
        await using var host = await CreateHostAsync(provider, model);
        var documents = host.Services.GetRequiredService<TestDocuments>();
        var project = Guid.NewGuid();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var receipt = Assert.Single(await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
                .PublishAsync(new ProjectChanged(Guid.NewGuid(), project, "budget changed")));
            var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
            var context = Substitute.For<INhBackgroundOperationContext>();
            context.AttemptNumber.Returns(1);
            await operation.ExecuteAsync(new NhAssistantAgentRunRequest(receipt.RunId!.Value), context, CancellationToken.None);
        }

        var request = Assert.Single(documents.Requests);
        Assert.StartsWith("budget changed Project ", request.Query, StringComparison.Ordinal);
        Assert.Equal(NhAiActorKind.Agent, request.InvocationContext.ActorKind);
        Assert.Contains(request.InvocationContext.ExecutionScopes, scope => scope.Type == "project" && scope.Id == project.ToString("N"));
        Assert.Contains("A note about this project.", model.Requests[0].Options!.Instructions);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_slow_resolver_leaves_the_turn_without_retrieved_context(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Without documents.");
        await using var host = await CreateHostAsync(provider, model, limits => limits.RetrievalTimeout = TimeSpan.FromMilliseconds(200));
        host.Services.GetRequiredService<TestDocuments>().Delay = TimeSpan.FromSeconds(5);

        var conversation = await host.CreateConversationAsync();
        var turn = await host.SendAsync(conversation.Id, "What does the roadmap say?");

        Assert.Null(turn.StartCode);
        Assert.DoesNotContain("# Retrieved context", model.Requests[0].Options!.Instructions);
        Assert.Contains(host.Logs.Messages, message => message.Contains("resolver failed with timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_source_fails_at_startup()
    {
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            assistantBuilder: assistant => assistant
                .ConfigureAgentContext(AgentId, context => context.UseRetrievedContext(retrieval => retrieval.Sources("missing-documents"))));

        var validator = host.Services.GetServices<IHostedService>().OfType<NhAssistantStartupValidator>().Single();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("missing-documents", failure.Message);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        IChatClient model,
        Action<NhAssistantLimits>? limits = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            limits,
            configure: services =>
            {
                services.AddSingleton<TestDocuments>();
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddNewHeapPlatformAI(ai => ai
                    .AddContextSource<TestDocumentSource>()
                    .AddContextContributor<TestDivisionContributor>()
                    .UseContextAuthorizationPolicy<TestDocumentPolicy>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .ConfigureAgent(AgentId, agent => agent.UseApprovalPolicy("project-review"))
                .ConfigureAgentContext(AgentId, context => context.UseRetrievedContext(retrieval => retrieval.Sources(SourceId)))
                .AddTrigger<ProjectChanged>("budget-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)));
    }
}

/// <summary>
/// The requests the test document source received, and a delay it waits before answering.
/// </summary>
public sealed class TestDocuments
{
    public ConcurrentQueue<NhAiContextRequest> Requests { get; } = new();

    public TimeSpan Delay { get; set; }
}

/// <summary>
/// Documents of the person's division, of another division and above the turn's classification, and
/// in a run a note scoped to the run's subject.
/// </summary>
public sealed class TestDocumentSource(TestDocuments documents) : INhAiContextSource
{
    public NhAiContextSourceDescriptor Descriptor { get; } = new(
        "test-documents",
        "Documents of the test application.",
        NhAiDataClassification.Confidential,
        10,
        2_000);

    public async ValueTask<IReadOnlyList<NhAiContextItem>> RetrieveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default)
    {
        documents.Requests.Enqueue(request);
        if (documents.Delay > TimeSpan.Zero)
        {
            await Task.Delay(documents.Delay, cancellationToken);
        }

        var created = DateTimeOffset.UtcNow.AddDays(-1);
        if (request.InvocationContext.ActorKind == NhAiActorKind.Agent)
        {
            var subject = request.InvocationContext.ExecutionScopes[0];
            return
            [
                new NhAiContextItem("note-1", "note-1", Descriptor.Id, "text/plain", "A note about this project.", NhAiDataClassification.Internal, NhAiContextTrust.UntrustedRetrieved, [subject], created)
            ];
        }

        var division = new NhAiExecutionScopeEntry("division", "division-a");
        return
        [
            new NhAiContextItem("plan-1", "plan-1", Descriptor.Id, "text/plain", "The roadmap moved to May. <b>", NhAiDataClassification.Internal, NhAiContextTrust.UntrustedRetrieved, [division], created)
            {
                EffectiveAt = created
            },
            new NhAiContextItem("plan-2", "plan-2", Descriptor.Id, "text/plain", "Another division's plan.", NhAiDataClassification.Internal, NhAiContextTrust.UntrustedRetrieved, [new NhAiExecutionScopeEntry("division", "division-b")], created),
            new NhAiContextItem("plan-3", "plan-3", Descriptor.Id, "text/plain", "A confidential plan.", NhAiDataClassification.Confidential, NhAiContextTrust.UntrustedRetrieved, [division], created)
        ];
    }
}

public sealed class TestDocumentPolicy : INhAiContextAuthorizationPolicy
{
    public ValueTask<bool> CanAccessSourceAsync(
        NhAiContextSourceDescriptor source,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(source.Id == "test-documents");
    }
}

/// <summary>
/// Puts the person's request into division A, as an application's active division would.
/// </summary>
public sealed class TestDivisionContributor : INhAiInvocationContextContributor
{
    public int Order => 100;

    public ValueTask ContributeAsync(NhAiInvocationContextBuilder builder, CancellationToken cancellationToken = default)
    {
        builder.AddExecutionScope("division", "division-a");
        return ValueTask.CompletedTask;
    }
}
