using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// LS15: the application lists the people of the whole application, app-wide and per division
/// (<c>UseSystemSubject(s => s.Members&lt;T&gt;())</c>), so a proposal in a thread of the whole
/// application that nobody opened still reaches its approvers. Roles keep coming from the read and
/// approve policies: a listed reader never hears what is meant for approvers, and a listed person who
/// may not work in a division hears nothing of it. On both relational providers.
/// </summary>
public sealed class AssistantSystemMembersTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AppWide = "results-follow-up";
    private const string PerDivision = "regional-review";
    private const string ReadPolicy = "members.system.read";
    private const string ApprovePolicy = "members.system.approve";
    private const string Admin = "admin-1";
    private const string Reader = "reader-1";
    private const string Viewer = "viewer-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_of_the_whole_application_reaches_the_listed_approvers_and_never_its_readers(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, members: true);
        // Nobody opened the thread: it has no participants and nobody decided in it.
        var global = await ThreadAsync(host, AppWide, NhAiSystemSubjects.Global);

        await NotifyAsync(host, global, await ProposeAsync(host, global));

        var delivery = Assert.Single(Channel(host).Deliveries);
        Assert.Equal(NhAssistantAgentNotificationKind.RunProposalsWaiting, delivery.Context.Kind);
        Assert.Equal([Admin], delivery.Recipients);
        // The policy saw the listed people with the roles the policies give them now; a person without a
        // role, an account that may not sign in and an unusable id are no members.
        Assert.Equal(
            new[] { (Admin, NhAiSubjectRole.Approver), (Reader, NhAiSubjectRole.Reader) },
            delivery.Context.Members.Select(member => (member.ActorId, member.Role)).OrderBy(member => member.ActorId, StringComparer.Ordinal).ToArray());
        Assert.Equal(new Guid?[] { null }, host.Services.GetRequiredService<ListedSystemMembers>().Asked.ToArray());

        // In a division the list of that division counts, and only for people who may work there.
        var north = await ThreadAsync(host, PerDivision, NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.North));
        await NotifyAsync(host, north, await ProposeAsync(host, north));
        var northern = Channel(host).Deliveries.Last();
        Assert.Equal([Admin], northern.Recipients);
        Assert.Equal([Admin, Reader], northern.Context.Members.Select(member => member.ActorId).Order(StringComparer.Ordinal).ToArray());

        // Only the first people of a list are read: an approver listed after them hears nothing.
        var south = await ThreadAsync(host, PerDivision, NhAiSystemSubjects.ForDivision(AssistantSystemSubjectTests.South));
        await NotifyAsync(host, south, await ProposeAsync(host, south));
        Assert.Equal(2, Channel(host).Deliveries.Count);
        Assert.Equal(
            new Guid?[] { null, AssistantSystemSubjectTests.North, AssistantSystemSubjectTests.South },
            host.Services.GetRequiredService<ListedSystemMembers>().Asked.ToArray());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_a_list_a_thread_of_the_whole_application_reaches_only_its_own_people(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, members: false);
        var global = await ThreadAsync(host, AppWide, NhAiSystemSubjects.Global);

        await NotifyAsync(host, global, await ProposeAsync(host, global));

        Assert.Empty(Channel(host).Deliveries);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Null(scope.ServiceProvider.GetService<INhAssistantSystemMembers>());
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, bool members)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingChannel>();
                services.AddSingleton<ListedSystemMembers>();
                services.AddSingleton<SystemSubjectPrincipals>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(provider => provider.GetRequiredService<SystemSubjectPrincipals>()));
                services.AddNewHeapDivisionDirectory<TestDivisionDirectory>();
                services.AddNewHeapDivisionAccess<TestDivisionAccess>();
                services.AddAuthorization(options =>
                {
                    options.AddPolicy(ReadPolicy, policy => policy.RequireClaim("permission", "assistant"));
                    options.AddPolicy(ApprovePolicy, policy => policy.RequireClaim("permission", "app.project.manage"));
                });
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgentNotificationChannel<RecordingChannelAdapter>()
                .UseSystemSubject(system =>
                {
                    system.ReadPolicy(ReadPolicy).ApprovePolicy(ApprovePolicy);
                    if (members)
                    {
                        system.Members<TestSystemMembers>();
                    }
                })
                .AddAgent(AssistantTestData.Agent(AppWide))
                .AddAgent(AssistantTestData.Agent(PerDivision))
                .ConfigureAgent(AppWide, run => run.WorksOnApplication())
                .ConfigureAgent(PerDivision, run => run.WorksOnApplication().PerDivision()));
    }

    private static async Task<AssistantThread> ThreadAsync(AssistantTestHost host, string agentId, NhAiSubjectRef subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
    }

    private static async Task<AssistantApproval> ProposeAsync(AssistantTestHost host, AssistantThread thread)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.complete",
            Summary = "Close the quarter.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            RunId = Guid.NewGuid(),
            Mode = NhAssistantApprovalModes.Deferred,
            ConcurrencyStamp = Guid.NewGuid()
        };
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval;
    }

    private static async Task NotifyAsync(AssistantTestHost host, AssistantThread thread, AssistantApproval approval)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantAgentNotifier>().NotifyAsync(
            NhAssistantAgentNotificationKind.RunEndedWithProposals,
            thread,
            approval.RunId,
            approval,
            null,
            shadow: false,
            CancellationToken.None,
            proposalCount: 1);
    }

    private static RecordingChannel Channel(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<RecordingChannel>();
    }

    /// <summary>
    /// Which lists the notifier asked for, by division.
    /// </summary>
    public sealed class ListedSystemMembers
    {
        public System.Collections.Concurrent.ConcurrentQueue<Guid?> Asked { get; } = new();
    }

    /// <summary>
    /// The whole application app-wide: a reader, a viewer without a role, an account that may not sign
    /// in, an unusable id and the administrator. North: the reader and the administrator. South:
    /// more people than one notification reads, before the administrator.
    /// </summary>
    public sealed class TestSystemMembers(ListedSystemMembers asked) : INhAssistantSystemMembers
    {
        public async IAsyncEnumerable<string> ListAsync(
            Guid? divisionId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            asked.Asked.Enqueue(divisionId);
            await Task.CompletedTask;
            if (divisionId is null)
            {
                foreach (var actorId in new[] { Reader, Viewer, "stranger-1", " ", new string('x', 257), Admin })
                {
                    yield return actorId;
                }

                yield break;
            }

            if (divisionId == AssistantSystemSubjectTests.North)
            {
                yield return Reader;
                yield return Admin;
                yield break;
            }

            for (var index = 0; index < INhAssistantSystemMembers.MaxMembers; index++)
            {
                yield return "former-" + index;
            }

            yield return Admin;
        }
    }
}
