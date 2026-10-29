using System.Collections.Concurrent;
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
/// FB50: with <see cref="INhAssistantApproverCandidates"/> the application names the people outside the
/// members, per tenant and for the whole application; the directory checks each one as it checks a member
/// and calls the list complete when the application did and no limit applied, on both providers.
/// </summary>
public sealed class AssistantApproverDirectoryTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string ManagePolicy = "app.project.manage";
    private const string AuditPolicy = "app.project.audit";
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

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Application_candidates_let_a_policy_rule_say_who_approves_and_whether_that_is_everyone(AssistantTestProvider provider)
    {
        // FB50: the auditors pass the audit policy and are no members of the project.
        var book = new CandidateBook();
        await using var host = await CreateHostAsync(provider, book);
        await using var scope = host.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();

        // One candidate who passes the rule: exactly one person approves, and the list says so.
        book.Name(TenantA, complete: true, "auditor-1");
        var one = (await directory.ListAsync("auditors", ProjectA, TenantA)).Data!;
        Assert.Equal(new NhAssistantApprover("auditor-1", NhAiSubjectRole.None) { DisplayName = "Named auditor-1" }, Assert.Single(one.Approvers));
        Assert.True(one.Complete);
        Assert.Equal(new NhAssistantApproverCount(1, true), (await directory.CountAsync("auditors", ProjectA, TenantA)).Data);
        var query = book.Queries.Last();
        Assert.Equal(
            ("auditors", ProjectA, (string?)TenantA, (Guid?)null, false, NhAssistantApproverList.MaxCandidates),
            (query.PolicyId, query.Subject, query.TenantId, query.DivisionId, query.SubjectRoleHolders, query.MaxCandidates));
        Assert.Equal([AuditPolicy], query.PolicyNames);

        // Two who pass, in the order the application names them; leaving one out counts the other.
        book.Name(TenantA, complete: true, "auditor-1", "auditor-2");
        var two = (await directory.ListAsync("auditors", ProjectA, TenantA)).Data!;
        Assert.Equal(["auditor-1", "auditor-2"], Ids(two));
        Assert.True(two.Complete);
        Assert.Equal(new NhAssistantApproverCount(1, true), (await directory.CountAsync("auditors", ProjectA, TenantA, excludeActorId: "auditor-1")).Data);

        // Candidates who fail the rule are not listed: a member without the permission, a person who may no
        // longer sign in, an agent, a blank id; someone named twice is listed once.
        book.Name(TenantA, complete: true, "member-1", "locked-1", "assistant-agent:helper", " ", "outsider-1", "auditor-1", "auditor-1");
        var checkedList = (await directory.ListAsync("auditors", ProjectA, TenantA)).Data!;
        Assert.Equal(["auditor-1"], Ids(checkedList));
        Assert.True(checkedList.Complete);

        // A list the application declares incomplete stays incomplete; so does one it cannot give.
        book.Name(TenantA, complete: false, "auditor-1");
        Assert.Equal(new NhAssistantApproverCount(1, false), (await directory.CountAsync("auditors", ProjectA, TenantA)).Data);
        book.Throws = true;
        Assert.Equal(new NhAssistantApproverCount(0, false), (await directory.CountAsync("auditors", ProjectA, TenantA)).Data);
        book.Throws = false;

        // The limit: the second auditor is left unchecked, unless the limit leaves nobody out.
        book.Name(TenantA, complete: true, "auditor-1", "auditor-2");
        var first = (await directory.ListAsync("auditors", ProjectA, TenantA, limit: 1)).Data!;
        Assert.Equal(["auditor-1"], Ids(first));
        Assert.False(first.Complete);
        Assert.Equal(new NhAssistantApproverCount(2, true), (await directory.CountAsync("auditors", ProjectA, TenantA, limit: 2)).Data);

        // At most MaxCandidates named people are checked: one more is never complete.
        var many = Enumerable.Range(1, NhAssistantApproverList.MaxCandidates).Select(index => "outsider-" + index).ToArray();
        book.Name(TenantA, complete: true, many);
        Assert.Equal(new NhAssistantApproverCount(0, true), (await directory.CountAsync("auditors", ProjectA, TenantA)).Data);
        book.Name(TenantA, complete: true, [.. many, "auditor-1"]);
        Assert.Equal(new NhAssistantApproverCount(0, false), (await directory.CountAsync("auditors", ProjectA, TenantA)).Data);

        // Members come first, then the candidates. The members filling the limit leave the application
        // unasked; a policy without a rule outside the members never asks it.
        book.Name(TenantA, complete: true, "auditor-1", "approver-2");
        var mixed = (await directory.ListAsync("approvers-or-auditors", ProjectA, TenantA)).Data!;
        Assert.Equal(["approver-1", "approver-2", "locked-1", "auditor-1"], Ids(mixed));
        Assert.True(mixed.Complete);
        var asked = book.Queries.Count;
        Assert.Equal(new NhAssistantApproverCount(2, false), (await directory.CountAsync("approvers-or-auditors", ProjectA, TenantA, limit: 2)).Data);
        Assert.Equal(new NhAssistantApproverCount(3, true), (await directory.CountAsync("approvers", ProjectA, TenantA)).Data);
        Assert.Equal(asked, book.Queries.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Application_candidates_are_asked_for_the_subjects_tenant_and_complete_the_whole_application(AssistantTestProvider provider)
    {
        var book = new CandidateBook();
        book.Name(TenantA, complete: true, "auditor-1");
        book.Name(TenantB, complete: true, "auditor-b");
        await using var host = await CreateHostAsync(provider, book);
        await using var scope = host.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();

        // Each tenant's subject is asked with its own tenant and lists that tenant's people only.
        Assert.Equal(["auditor-1"], Ids((await directory.ListAsync("auditors", ProjectA, TenantA)).Data!));
        Assert.Equal(["auditor-b"], Ids((await directory.ListAsync("auditors", ProjectB, TenantB)).Data!));
        Assert.Equal([TenantA, TenantB], book.Queries.Select(item => item.TenantId).ToArray());
        // Another tenant's subject is unknown before the application is asked.
        Assert.Equal(NhAssistantErrorCodes.SubjectNotFound, CodeOf(await directory.ListAsync("auditors", ProjectB, TenantA)));
        Assert.Equal(2, book.Queries.Count);

        // The whole application lists no members: its approvers are whom its approve policy admits, so the
        // application names the people who hold a role there. Those who fail the approve policy are not
        // listed, and the list the application declares complete is complete.
        book.Name(null, complete: true, "auditor-1", "member-1", "locked-1");
        var global = (await directory.ListAsync("approvers", NhAiSystemSubjects.Global, tenantId: null)).Data!;
        Assert.Equal(new NhAssistantApprover("auditor-1", NhAiSubjectRole.Approver) { DisplayName = "Named auditor-1" }, Assert.Single(global.Approvers));
        Assert.True(global.Complete);
        var query = book.Queries.Last();
        Assert.Equal((NhAiSystemSubjects.Global, (string?)null, true), (query.Subject, query.TenantId, query.SubjectRoleHolders));
        Assert.Empty(query.PolicyNames);
        // A policy rule there is named as well, and the initiator rule alone needs nobody.
        Assert.Equal(new NhAssistantApproverCount(1, true), (await directory.CountAsync("auditors", NhAiSystemSubjects.Global, tenantId: null)).Data);
        var asked = book.Queries.Count;
        Assert.Equal(new NhAssistantApproverCount(0, true), (await directory.CountAsync("initiator-only", NhAiSystemSubjects.Global, tenantId: null)).Data);
        Assert.Equal(asked, book.Queries.Count);
        book.Name(null, complete: false, "auditor-1");
        Assert.Equal(new NhAssistantApproverCount(1, false), (await directory.CountAsync("approvers", NhAiSystemSubjects.Global, tenantId: null)).Data);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_under_a_policy_rule_lists_the_candidates_its_decision_accepts(AssistantTestProvider provider)
    {
        var book = new CandidateBook();
        book.Name(TenantA, complete: true, "auditor-1", "auditor-2", "member-1");
        await using var host = await CreateHostAsync(provider, book);
        var thread = await ThreadAsync(host, ProjectA);
        var fourEyes = await SeedAsync(host, thread, Policy("auditors", fourEyes: true, subjectRole: false, policyRule: AuditPolicy), initiator: "auditor-1");
        await using var scope = host.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantApproverDirectory>();

        // Four eyes leave the initiator out, also when the application names them: one person approves,
        // and that is everyone.
        var listed = (await directory.ListForProposalAsync(fourEyes.Id, TenantA)).Data!;
        Assert.Equal(["auditor-2"], Ids(listed));
        Assert.True(listed.Complete);
        Assert.Equal("auditors", book.Queries.Last().PolicyId);

        // Everyone listed passes the decision's check; everyone else who may sign in is refused by it.
        var check = scope.ServiceProvider.GetRequiredService<NhAssistantApproverCheck>();
        foreach (var actorId in new[] { "auditor-1", "auditor-2", "member-1", "approver-1", "manager-1", "outsider-1" })
        {
            var decision = await check.CheckAsync(fourEyes, thread, actorId, DirectoryPrincipals.For(actorId), CancellationToken.None);
            Assert.Equal(listed.Approvers.Any(item => item.ActorId == actorId), decision.Success);
        }
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return CreateHostAsync(provider, candidates: null);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, CandidateBook? candidates)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, DirectoryPrincipals>());
                services.AddAuthorization(options => options.AddPolicy(AuditPolicy, policy => policy.RequireClaim("permission", AuditPolicy)));
                if (candidates is not null)
                {
                    services.AddSingleton(candidates);
                }
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<DirectorySubjectResolver>()
                    .AddApprovalPolicy("approvers", policy => policy.Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                    .AddApprovalPolicy("leads", policy => policy.Approvers(NhAiApprovers.SubjectRole("lead")))
                    .AddApprovalPolicy("managers", policy => policy.Approvers(NhAiApprovers.Policy(ManagePolicy)))
                    .AddApprovalPolicy("auditors", policy => policy.Approvers(NhAiApprovers.Policy(AuditPolicy)))
                    .AddApprovalPolicy("approvers-or-auditors", policy => policy.Approvers(
                        NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver),
                        NhAiApprovers.Policy(AuditPolicy)))
                    .AddApprovalPolicy("initiator-only", policy => policy.Approvers(NhAiApprovers.Initiator))
                    .ConfigureAgent(AgentId, agent => agent.Grant("projects.*").UseApprovalPolicy("approvers"));
                if (candidates is not null)
                {
                    // FB50: the auditors approve the whole application too, and the application names them.
                    assistant
                        .UseSystemSubject(system => system.ApprovePolicy(AuditPolicy))
                        .UseApproverCandidates<BookCandidates>();
                }
            });
    }

    private static NhAiApprovalPolicy Policy(
        string id,
        bool fourEyes = false,
        bool initiator = false,
        bool subjectRole = true,
        string? policyRule = null)
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
        if (policyRule is not null)
        {
            approvers.Add(NhAiApprovers.Policy(policyRule));
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
    /// People as headless work rebuilds them: the manager passes the manage policy, the auditors, who are
    /// no members of any project, pass the audit policy, and the locked account may no longer sign in.
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
            if (actorId.StartsWith("auditor-", StringComparison.Ordinal))
            {
                claims.Add(new Claim("permission", AuditPolicy));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(actorId == "locked-1" ? null : For(actorId));
        }
    }

    /// <summary>
    /// FB50: the people the application names per tenant (the whole application under no tenant), whether
    /// it declares them complete, and the questions the directory asked.
    /// </summary>
    private sealed class CandidateBook
    {
        private readonly ConcurrentDictionary<string, (string[] ActorIds, bool Complete)> _named = new(StringComparer.Ordinal);

        public ConcurrentQueue<NhAssistantApproverCandidateQuery> Queries { get; } = new();

        public bool Throws { get; set; }

        public void Name(string? tenantId, bool complete, params string[] actorIds)
        {
            _named[tenantId ?? string.Empty] = (actorIds, complete);
        }

        public NhAssistantApproverCandidateList List(NhAssistantApproverCandidateQuery query)
        {
            Queries.Enqueue(query);
            if (Throws)
            {
                throw new InvalidOperationException("The people directory is unavailable.");
            }
            if (!_named.TryGetValue(query.TenantId ?? string.Empty, out var named))
            {
                return NhAssistantApproverCandidateList.Unknown;
            }

            var candidates = named.ActorIds
                .Select(actorId => new NhAssistantApproverCandidate(actorId) { DisplayName = "Named " + actorId })
                .ToArray();
            return new NhAssistantApproverCandidateList(candidates, named.Complete);
        }
    }

    private sealed class BookCandidates(CandidateBook book) : INhAssistantApproverCandidates
    {
        public ValueTask<NhAssistantApproverCandidateList> ListAsync(
            NhAssistantApproverCandidateQuery query,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(book.List(query));
        }
    }
}
