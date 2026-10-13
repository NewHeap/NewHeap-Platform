using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// How the work of an agent thread reaches people: proposals presented in each viewer's language,
/// tool calls that stop waiting once their proposal closed, live updates for everyone on the
/// subject, and names instead of ids, on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantAgentWorkTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Approver = "approver-1";
    private const string Member = "member-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_reads_in_each_viewers_language_and_its_tool_call_ends_when_it_closes(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Active" } }, "call-active")
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId, status = "Closed" } }, "call-closed")
            .RespondWithText("I proposed two changes.");
        await using var host = await CreateHostAsync(provider, model);
        var live = host.Services.GetRequiredService<RecordedLiveUpdates>();

        // The headless run works in the application's default culture.
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("nl-NL");
        var run = await StartRunAsync(host, projectId);
        await RunTurnAsync(host, run, "Project PRJ-1 was created.");
        var approvals = await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None);
        Assert.Equal(2, approvals.Count);
        var (rejected, expired) = (approvals[0], approvals[1]);

        // Every supported UI culture has its own presentation; a viewer reads their own.
        Assert.Equal("Projectstatus wijzigen", NhAssistantToolPresentationResolver.Deserialize(rejected.PresentationJson)!.ToolDisplayName);
        Assert.Equal(
            "Change project status",
            NhAssistantToolPresentationResolver.Pick(rejected.PresentationJson, rejected.PresentationsJson, CultureInfo.GetCultureInfo("en-GB"))!.ToolDisplayName);
        Assert.Equal(
            "Projectstatus wijzigen",
            NhAssistantToolPresentationResolver.Pick(rejected.PresentationJson, rejected.PresentationsJson, CultureInfo.GetCultureInfo("nl-BE"))!.ToolDisplayName);
        Assert.Equal(
            "Projectstatus wijzigen",
            NhAssistantToolPresentationResolver.Pick(rejected.PresentationJson, rejected.PresentationsJson, CultureInfo.GetCultureInfo("de-DE"))!.ToolDisplayName);

        // The people on the subject learn that the proposals changed, also before they opened the thread.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<INhAiSubjectAccess>().RefreshAsync(Subject(projectId))).Success);
        }
        live.Clear();
        await RejectAsync(host, rejected.Id);
        Assert.True(await RunStore(host).TryTransitionApprovalAsync(
            expired.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Expired,
            NhAssistantApprovalTransition.None,
            CancellationToken.None));

        var update = Assert.Single(live.Sent, sent => sent.Event is NhAssistantLiveAgentWorkChanged);
        var work = (NhAssistantLiveAgentWorkChanged)update.Event;
        Assert.Equal(run.Thread.ConversationId, work.ConversationId);
        Assert.Equal(NhAssistantAgentWorkReasons.Proposal, work.Reason);
        Assert.Equal(rejected.Id, work.ApprovalId);
        Assert.Equal(("project", projectId.ToString()), (work.SubjectType, work.SubjectId));
        Assert.Contains(Approver, update.ActorIds);
        Assert.Contains(Member, update.ActorIds);

        // The thread no longer shows the calls as waiting, and reads in the viewer's language.
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var conversation = (await services.GetRequiredService<INhAssistantStore>()
                .FindShareableConversationAsync(run.Thread.ConversationId, CancellationToken.None))!;
            var view = await services.GetRequiredService<NhAssistantConversationReader>().CreateViewAsync(conversation, CancellationToken.None);
            var parts = view.Messages.SelectMany(message => message.Parts).ToArray();
            var calls = parts.OfType<NhAssistantToolCallPartView>().ToArray();
            Assert.Equal(
                [NhAssistantToolCallStatuses.Rejected, NhAssistantToolCallStatuses.NotExecuted],
                calls.Select(call => call.Status));
            Assert.All(calls, call => Assert.Equal("Change project status", call.DisplayName));
            Assert.All(parts.OfType<NhAssistantApprovalView>(), approval => Assert.Equal("Change project status", approval.Presentation!.ToolDisplayName));
        }

        // Names come from the subject's member snapshot; unknown people keep their id.
        var names = await RunStore(host).GetDisplayNamesAsync([Approver, Member, "stranger", null], CancellationToken.None);
        Assert.Equal("Approver One", names[Approver]);
        Assert.Equal("Member One", names[Member]);
        Assert.False(names.ContainsKey("stranger"));
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
                services.AddSingleton<RecordedLiveUpdates>();
                services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport>(provider => provider.GetRequiredService<RecordedLiveUpdates>()));
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.Configure<RequestLocalizationOptions>(options =>
                {
                    options.SupportedUICultures = [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("nl-NL")];
                    options.SupportedCultures = options.SupportedUICultures;
                });
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<NamedSubjectResolver>()
                .AddToolPresenter<CulturePresenter>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")));
    }

    private sealed record StartedRun(AssistantThread Thread, AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(Subject(projectId), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false);
        return new StartedRun(thread, run, agentTurn);
    }

    private static async Task RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(run.AgentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task RejectAsync(AssistantTestHost host, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Approver)], "test"));
        var decided = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>()
            .DecideAsync(approvalId, Approver, user, approve: false, null, null, CancellationToken.None);
        Assert.True(decided.Success);
    }

    private static NhAssistantRunStore RunStore(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static NhAiSubjectRef Subject(Guid projectId)
    {
        return new NhAiSubjectRef("project", projectId.ToString());
    }

    /// <summary>
    /// The test subjects with readable member names.
    /// </summary>
    private sealed class NamedSubjectResolver(TestSubjects subjects) : INhAiSubjectAccessResolver
    {
        private readonly TestSubjectResolver _inner = new(subjects);

        public string SubjectType => _inner.SubjectType;

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            return _inner.DescribeAsync(subject, cancellationToken);
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            return _inner.ResolveAsync(subject, actorId, cancellationToken);
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var member in _inner.ListMembersAsync(subject, cancellationToken))
            {
                yield return member with
                {
                    DisplayName = member.ActorId == Approver ? "Approver One" : "Member One"
                };
            }
        }
    }

    /// <summary>
    /// Presents the status change in Dutch or English, depending on the culture it is asked for.
    /// </summary>
    private sealed class CulturePresenter : INhAssistantToolPresenter
    {
        public ValueTask<string?> GetDisplayNameAsync(NhAssistantToolDisplayNameContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(Name(context.Culture));
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            if (context.Arguments is not ProjectStatusInput input)
            {
                return ValueTask.FromResult<NhAssistantApprovalPresentation?>(null);
            }

            var dutch = context.Culture.TwoLetterISOLanguageName == "nl"
                && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl";
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(new NhAssistantApprovalPresentation(
                Name(context.Culture),
                dutch ? $"Wijzig de status naar {input.Status}." : $"Change the status to {input.Status}.",
                []));
        }

        private static string Name(CultureInfo culture)
        {
            return culture.TwoLetterISOLanguageName == "nl" ? "Projectstatus wijzigen" : "Change project status";
        }
    }
}

/// <summary>
/// Records every live update and its recipients instead of sending it.
/// </summary>
internal sealed class RecordedLiveUpdates : INhAssistantLiveUpdateTransport
{
    private readonly ConcurrentQueue<(IReadOnlyCollection<string> ActorIds, NhAssistantLiveEvent Event)> _sent = new();

    public IReadOnlyList<(IReadOnlyCollection<string> ActorIds, NhAssistantLiveEvent Event)> Sent => _sent.ToArray();

    public void Clear()
    {
        _sent.Clear();
    }

    public Task SendAsync(IReadOnlyCollection<string> actorIds, NhAssistantLiveEvent evt, CancellationToken cancellationToken)
    {
        _sent.Enqueue((actorIds.ToArray(), evt));
        return Task.CompletedTask;
    }
}
