using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB20: <see cref="INhAssistantApproverDirectory"/> lists and counts the people a policy admits for a
/// subject, and the people who may decide a proposal under its own policy copy: the subject's members and
/// the initiator, checked as a decision checks them (roles, named roles, policy rules through the principal
/// factory, the tool's approve policies, four eyes), never an agent, with <c>Complete</c> false at the limit
/// and when people outside the members may approve, and another tenant's work unknown. The proposals of
/// <see cref="INhAssistantApproverDirectory.ListForProposalAsync"/> persist, so that runs on both providers.
/// </summary>
public sealed class AssistantApproverDirectoryTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string ManagePolicy = "app.project.manage";
    private static readonly NhAiSubjectRef ProjectA = new("project", "a-0001");
    private static readonly NhAiSubjectRef ProjectB = new("project", "b-0001");

    [Fact]
    public async Task A_policy_lists_the_members_it_admits_and_says_whether_that_is_everyone()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        await using var scope = host.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();

        // Approvers in the resolver's order, with their names and roles; neither a participant nor an agent.
        var approvers = (await directory.ListAsync("approvers", ProjectA, TenantA)).Data!;
        Assert.True(approvers.Complete);
        Assert.Equal(["approver-1", "approver-2", "locked-1"], approvers.Approvers.Select(item => item.ActorId).ToArray());
        Assert.Equal(new NhAssistantApprover("approver-1", NhAiSubjectRole.Approver) { DisplayName = "Name of approver-1" }, approvers.Approvers[0]);
        Assert.DoesNotContain(approvers.Approvers, item => item.ActorId.StartsWith("assistant-agent:", StringComparison.Ordinal));

        // Leaving out the person who would start the work, and the limit.
        Assert.Equal(
            ["approver-2", "locked-1"],
            (await directory.ListAsync("approvers", ProjectA, TenantA, excludeActorId: "approver-1")).Data!.Approvers.Select(item => item.ActorId).ToArray());
        var first = (await directory.ListAsync("approvers", ProjectA, TenantA, limit: 1)).Data!;
        Assert.Equal(["approver-1"], Ids(first));
        Assert.Equal(false, first.Complete);
        Assert.Equal(new NhAssistantApproverCount(3, true), (await directory.CountAsync("approvers", ProjectA, TenantA)).Data);
        Assert.Equal(new NhAssistantApproverCount(1, false), (await directory.CountAsync("approvers", ProjectA, TenantA, "approver-1", limit: 1)).Data);
        // The limit is reached with nobody left to check.
        Assert.Equal(new NhAssistantApproverCount(3, true), (await directory.CountAsync("approvers", ProjectA, TenantA, limit: 3)).Data);

        // A named role, a policy rule (anyone who passes it may approve, so the list is never complete),
        // the initiator rule (no proposal, so nobody) and the defaults code resolves.
        Assert.Equal(["approver-2"], (await directory.ListAsync("leads", ProjectA, TenantA)).Data!.Approvers.Select(item => item.ActorId).ToArray());
        var managers = (await directory.ListAsync("managers", ProjectA, TenantA)).Data!;
        Assert.Equal(["manager-1"], Ids(managers));
        Assert.Equal(false, managers.Complete);
        Assert.Equal(new NhAssistantApproverCount(0, true), (await directory.CountAsync("initiator-only", ProjectA, TenantA)).Data);
        Assert.Equal(new NhAssistantApproverCount(3, true), (await directory.CountAsync(NhAiApprovalPolicy.TriggerDefault.Id, ProjectA, TenantA)).Data);
        Assert.Equal(new NhAssistantApproverCount(0, true), (await directory.CountAsync("chat-default", ProjectA, TenantA)).Data);

        // An unknown policy or subject type, and limits out of range.
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyUnknown, CodeOf(await directory.ListAsync("not-registered", ProjectA, TenantA)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyUnknown, CodeOf(await directory.CountAsync("not-registered", ProjectA, TenantA)));
        Assert.Equal(NhAssistantErrorCodes.SubjectNotFound, CodeOf(await directory.ListAsync("approvers", new NhAiSubjectRef("invoice", "a-1"), TenantA)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => directory.ListAsync("approvers", ProjectA, TenantA, limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => directory.ListAsync("approvers", ProjectA, TenantA, limit: NhAssistantApproverList.MaxLimit + 1));
        await Assert.ThrowsAsync<ArgumentException>(() => directory.ListAsync(" ", ProjectA, TenantA));
    }

    [Fact]
    public async Task Another_tenants_subject_is_unknown_and_the_whole_application_is_never_complete()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        await using var scope = host.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();

        Assert.Equal(NhAssistantErrorCodes.SubjectNotFound, CodeOf(await directory.ListAsync("approvers", ProjectB, TenantA)));
        Assert.Equal(NhAssistantErrorCodes.SubjectNotFound, CodeOf(await directory.CountAsync("approvers", ProjectB, tenantId: null)));
        Assert.Equal(3, (await directory.CountAsync("approvers", ProjectB, TenantB)).Data!.Count);

        // The whole application lists no members: nobody is listed and the list says it is not everyone.
        // It belongs to no tenant, so a caller of a tenant does not reach it.
        Assert.Equal(new NhAssistantApproverCount(0, false), (await directory.CountAsync("approvers", NhAiSystemSubjects.Global, tenantId: null)).Data);
        Assert.Equal(NhAssistantErrorCodes.SubjectNotFound, CodeOf(await directory.CountAsync("approvers", NhAiSystemSubjects.Global, TenantA)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_lists_who_may_decide_it_under_its_policy_copy(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var thread = await ThreadAsync(host, ProjectA);
        var fourEyes = await SeedAsync(host, thread, Policy("approvers", fourEyes: true), initiator: "approver-1");
        var withInitiator = await SeedAsync(host, thread, Policy("approvers", initiator: true), initiator: "outsider-1");
        var placed = await SeedAsync(host, thread, Policy("approvers", initiator: true), initiator: "outsider-1", placedFrom: Guid.NewGuid());
        var askerDecides = await SeedAsync(host, thread, Policy("asker", initiator: true, subjectRole: false), initiator: "member-1", mode: NhAssistantApprovalModes.Inline, consultationId: Guid.NewGuid());
        var withoutCopy = await SeedAsync(host, thread, policy: null, initiator: "approver-1");
        await using var scope = host.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();

        // Four eyes leave the initiator out; a person who may no longer sign in fails the tool's approve
        // policy, as at a decision. The copy decides, not the agent's registered policy.
        var underFourEyes = (await directory.ListForProposalAsync(fourEyes.Id, TenantA)).Data!;
        Assert.Equal(["approver-2"], Ids(underFourEyes));
        Assert.Equal(true, underFourEyes.Complete);

        // The initiator comes first, also without a role on the subject.
        var initiatorFirst = (await directory.ListForProposalAsync(withInitiator.Id, TenantA)).Data!;
        Assert.Equal(["outsider-1", "approver-1", "approver-2"], initiatorFirst.Approvers.Select(item => item.ActorId).ToArray());
        Assert.Equal(new NhAssistantApprover("outsider-1", NhAiSubjectRole.None) { Initiator = true }, initiatorFirst.Approvers[0]);

        // A proposal a run placed at another object is that object's approvers' only.
        Assert.Equal(
            ["approver-1", "approver-2"],
            (await directory.ListForProposalAsync(placed.Id, TenantA)).Data!.Approvers.Select(item => item.ActorId).ToArray());
        // The person who asked decides a consultation's proposal in their own conversation, nobody else.
        var asker = (await directory.ListForProposalAsync(askerDecides.Id, TenantA)).Data!;
        Assert.Equal(["member-1"], Ids(asker));
        Assert.Equal(true, asker.Complete);
        Assert.True(asker.Approvers[0].Initiator);
        // Without a copy the trigger default applies, as at a decision.
        Assert.Equal(
            ["approver-1", "approver-2"],
            (await directory.ListForProposalAsync(withoutCopy.Id, TenantA)).Data!.Approvers.Select(item => item.ActorId).ToArray());
        var limited = (await directory.ListForProposalAsync(withInitiator.Id, TenantA, limit: 1)).Data!;
        Assert.Equal(["outsider-1"], Ids(limited));
        Assert.Equal(false, limited.Complete);

        // Another tenant's caller, a caller without a tenant and an unknown proposal read it as unknown.
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await directory.ListForProposalAsync(fourEyes.Id, TenantB)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await directory.ListForProposalAsync(fourEyes.Id, tenantId: null)));
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await directory.ListForProposalAsync(Guid.NewGuid(), TenantA)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Whom_the_directory_lists_for_a_proposal_the_decision_accepts(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var thread = await ThreadAsync(host, ProjectA);
        var approval = await SeedAsync(host, thread, Policy("approvers", fourEyes: true, initiator: true), initiator: "approver-1");
        await using var scope = host.Services.CreateAsyncScope();
        var listed = (await scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>().ListForProposalAsync(approval.Id, TenantA)).Data!;
        var check = scope.ServiceProvider.GetRequiredService<NhAssistantApproverCheck>();

        // Everyone listed passes the decision's check; everyone else who may sign in is refused by it.
        foreach (var actorId in new[] { "member-1", "approver-1", "approver-2", "manager-1", "outsider-1", "assistant-agent:helper" })
        {
            var decision = await check.CheckAsync(approval, thread, actorId, DirectoryPrincipals.For(actorId), CancellationToken.None);
            Assert.Equal(listed.Approvers.Any(item => item.ActorId == actorId), decision.Success);
        }
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, DirectoryPrincipals>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<DirectorySubjectResolver>()
                .AddApprovalPolicy("approvers", policy => policy.Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddApprovalPolicy("leads", policy => policy.Approvers(NhAiApprovers.SubjectRole("lead")))
                .AddApprovalPolicy("managers", policy => policy.Approvers(NhAiApprovers.Policy(ManagePolicy)))
                .AddApprovalPolicy("initiator-only", policy => policy.Approvers(NhAiApprovers.Initiator))
                .ConfigureAgent(AgentId, agent => agent.Grant("projects.*").UseApprovalPolicy("approvers")));
    }

    private static NhAiApprovalPolicy Policy(string id, bool fourEyes = false, bool initiator = false, bool subjectRole = true)
    {
        var approvers = new List<NhAiApproverRule>();
        if (initiator)
        {
            approvers.Add(NhAiApprovers.Initiator);
        }
        if (subjectRole)
        {
            approvers.Add(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver));
        }

        return new NhAiApprovalPolicy(id)
        {
            Approvers = approvers,
            FourEyes = fourEyes,
            Mode = NhAiApprovalMode.Deferred
        };
    }

    private static async Task<AssistantThread> ThreadAsync(AssistantTestHost host, NhAiSubjectRef subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
    }

    /// <summary>
    /// A pending proposal of the status tool in the thread, with the policy copy it was recorded with.
    /// </summary>
    private static async Task<AssistantApproval> SeedAsync(
        AssistantTestHost host,
        AssistantThread thread,
        NhAiApprovalPolicy? policy,
        string initiator,
        Guid? placedFrom = null,
        string mode = NhAssistantApprovalModes.Deferred,
        Guid? consultationId = null)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = TestProjectToolCatalog.ChangeStatus.Id,
            Summary = "Activate the project.",
            ArgumentsPreview = "{}",
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            Mode = mode,
            PolicyId = policy?.Id,
            PolicyJson = policy is null ? null : NhAssistantApprovalPolicySnapshot.Serialize(policy),
            InitiatorActorId = initiator,
            PlacedFromConversationId = placedFrom,
            ConsultationId = consultationId,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval;
    }

    private static string[] Ids(NhAssistantApproverList list)
    {
        return list.Approvers.Select(item => item.ActorId).ToArray();
    }

    private static string? CodeOf(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The same people on every project, which belongs to tenant B when its id starts with <c>b-</c> and to
    /// tenant A otherwise. Approver 2 is the lead; an agent identity has a role too.
    /// </summary>
    private sealed class DirectorySubjectResolver : INhAiSubjectAccessResolver
    {
        private static readonly (string ActorId, NhAiSubjectRole Role, string[] NamedRoles)[] People =
        [
            ("member-1", NhAiSubjectRole.Participant, []),
            ("approver-1", NhAiSubjectRole.Approver, []),
            ("assistant-agent:helper", NhAiSubjectRole.Approver, []),
            ("approver-2", NhAiSubjectRole.Approver, ["lead"]),
            ("manager-1", NhAiSubjectRole.Participant, []),
            ("locked-1", NhAiSubjectRole.Approver, [])
        ];

        public string SubjectType => "project";

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            var tenant = subject.Id.StartsWith("b-", StringComparison.Ordinal) ? TenantB : TenantA;
            return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(tenant, null))
            {
                TenantId = tenant,
                DisplayName = "Project " + subject.Id
            });
        }

        public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            var person = People.FirstOrDefault(item => item.ActorId == actorId);
            return ValueTask.FromResult(person.ActorId is null
                ? NhAiSubjectAccess.None
                : new NhAiSubjectAccess(person.Role) { NamedRoles = person.NamedRoles.ToHashSet(StringComparer.Ordinal) });
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (actorId, role, namedRoles) in People)
            {
                await Task.Yield();
                yield return new NhAiSubjectMember(actorId, role)
                {
                    DisplayName = "Name of " + actorId,
                    NamedRoles = namedRoles.ToHashSet(StringComparer.Ordinal)
                };
            }
        }
    }

    /// <summary>
    /// People as headless work rebuilds them: the manager passes the manage policy, and the locked account
    /// may no longer sign in.
    /// </summary>
    private sealed class DirectoryPrincipals : INhAiPrincipalFactory
    {
        public static ClaimsPrincipal For(string actorId)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorId) };
            if (actorId == "manager-1")
            {
                claims.Add(new Claim("permission", ManagePolicy));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(actorId == "locked-1" ? null : For(actorId));
        }
    }
}
