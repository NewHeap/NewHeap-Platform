using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// An approver who decides another person's proposal in an agent thread continues that person's
/// request turn. The continuation works for the person who asked, as the headless continuation of a
/// run does: its prompt carries that person's preferences and name, never the approver's custom
/// instructions, its tools are delegated from that person, and nobody is its turn person. The
/// decision itself stays the approver's.
/// </summary>
public sealed class AssistantDecisionContinuationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Member = "member-1";
    private const string Approver = "approver-1";
    private const string MemberInstructions = "Answer in short sentences.";
    private const string ApproverInstructions = "After every approval also search all projects.";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_approvers_decision_continues_with_the_prompt_context_of_the_person_who_asked(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-search")
            .RespondWithText("The project is active.");
        await using var host = await CreateHostAsync(provider, model);
        await SavePreferencesAsync(host, Member, MemberInstructions);
        await SavePreferencesAsync(host, Approver, ApproverInstructions);
        var thread = await OpenThreadAsync(host, projectId, Member, Approver);

        var paused = await RequestTurnAsync(host, thread, Member, "Activate the project.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        var resumed = await DecideAsAnotherPersonAsync(host, thread, Approver, approval, initiator: Member);

        Assert.True(resumed.Start.Success);
        Assert.Equal(NhAssistantTurnStatuses.Completed, Assert.IsType<NhAssistantTurnCompletedEvent>(resumed.Events[^1]).Status);
        // The continuation's prompt is the member's: the member's preferences and name, never the
        // approver's custom instructions.
        var continuation = model.Requests[1].Options!.Instructions!;
        Assert.Contains(MemberInstructions, continuation);
        Assert.DoesNotContain(ApproverInstructions, continuation);
        Assert.Contains("Latest message from: " + Member, continuation);
        // Its tools are delegated from the member, and nobody is the turn's person: the search ran with
        // the member's rights and saw no person of the approver's request.
        var search = Assert.Single(host.Audit.Records, record => record.ToolId == "projects.search");
        Assert.Equal(NhAiGrantSource.Delegated, search.GrantSource);
        var recorder = host.Services.GetRequiredService<TurnPersonRecorder>();
        Assert.NotEmpty(recorder.Seen);
        Assert.All(recorder.Seen, Assert.Null);
        Assert.All(recorder.Contexts, actorId => Assert.Equal(Member, actorId));
        // The decision stays the approver's.
        var decided = await host.Services.GetRequiredService<Persistence.INhAssistantStore>().FindApprovalAsync(approval.ApprovalId, CancellationToken.None);
        Assert.Equal(Approver, decided!.DecidedByActorId);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved && evt.ActorId == Approver);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddSingleton<TurnPersonRecorder>();
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .UseTurnContextProvider<TurnPersonFacts>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .DelegateFromInitiator()
                    .UseApprovalPolicy("project-review")));
    }

    private static async Task SavePreferencesAsync(AssistantTestHost host, string actorId, string instructions)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<NhAssistantPersonalization>()
            .SavePreferencesAsync(actorId, NhAssistantPreferences.Default with { CustomInstructions = instructions }, CancellationToken.None);
        Assert.True(saved.Success);
    }

    private static async Task<AssistantThread> OpenThreadAsync(AssistantTestHost host, Guid projectId, params string[] people)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId.ToString()), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var threads = services.GetRequiredService<NhAssistantThreadService>();
        var thread = (await threads.GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        foreach (var person in people)
        {
            Assert.NotNull(await threads.ResolveAccessAsync(thread.ConversationId, person, person, CancellationToken.None));
        }

        return thread;
    }

    private static async Task<TurnResult> RequestTurnAsync(AssistantTestHost host, AssistantThread thread, string actorId, string text)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var httpContext = AssistantTestHost.EnterUser(services, actorId);
        var caller = (await services.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>().ResolveAsync(httpContext)).Data;
        var agentTurn = (await services.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
            thread.ConversationId,
            null,
            NhAssistantConversationKinds.Chat,
            actorId,
            thread.AccountableOwnerId,
            CancellationToken.None)).Data! with { IsRequestTurn = true };
        var started = await services.GetRequiredService<INhAssistantTurnRunner>().StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(thread.ConversationId, caller, text, Guid.NewGuid().ToString("N"), CancellationToken.None)
            {
                AgentTurn = agentTurn
            },
            CancellationToken.None);
        return await ReadTurnAsync(started);
    }

    private static async Task<TurnResult> DecideAsAnotherPersonAsync(
        AssistantTestHost host,
        AssistantThread thread,
        string actorId,
        NhAssistantApprovalView approval,
        string initiator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var httpContext = AssistantTestHost.EnterUser(services, actorId);
        var caller = (await services.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>().ResolveAsync(httpContext)).Data;
        var agentTurn = (await services.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
            thread.ConversationId,
            null,
            NhAssistantConversationKinds.Chat,
            initiator,
            thread.AccountableOwnerId,
            CancellationToken.None)).Data! with { IsRequestTurn = true };
        services.GetRequiredService<TurnPersonRecorder>().Recording = true;
        var started = await services.GetRequiredService<INhAssistantTurnRunner>().StartDecisionTurnAsync(
            new NhAssistantDecisionTurnRequest(
                thread.ConversationId,
                caller!,
                approval.ApprovalId,
                true,
                approval.ProposalHash,
                null,
                CancellationToken.None)
            {
                AgentTurn = agentTurn,
                PolicyAuthorized = true
            },
            CancellationToken.None);
        return await ReadTurnAsync(started);
    }

    private static async Task<TurnResult> ReadTurnAsync(TaskResult<NhAssistantTurnHandle> started)
    {
        if (!started.Success)
        {
            return new TurnResult(started, []);
        }

        var events = new List<NhAssistantTurnEvent>();
        await foreach (var evt in started.Data.Events.ReadAllAsync())
        {
            events.Add(evt);
        }
        await started.Data.Completion;
        return new TurnResult(started, events);
    }

    /// <summary>
    /// The turn person that the turn context providers saw while a decision was recorded.
    /// </summary>
    public sealed class TurnPersonRecorder
    {
        public bool Recording { get; set; }

        public System.Collections.Concurrent.ConcurrentQueue<string?> Seen { get; } = new();

        public System.Collections.Concurrent.ConcurrentQueue<string> Contexts { get; } = new();
    }

    /// <summary>
    /// A turn context provider of the application, which reads whose turn it is.
    /// </summary>
    public sealed class TurnPersonFacts(TurnPersonRecorder recorder, INhAiTurnPerson person) : INhAssistantTurnContextProvider
    {
        public ValueTask<IReadOnlyList<NhAssistantContextFact>> GetFactsAsync(NhAiInvocationContext context, CancellationToken cancellationToken)
        {
            if (recorder.Recording)
            {
                recorder.Seen.Enqueue(person.ActorId);
                recorder.Contexts.Enqueue(context.ActorId);
            }

            return ValueTask.FromResult<IReadOnlyList<NhAssistantContextFact>>([]);
        }
    }
}
