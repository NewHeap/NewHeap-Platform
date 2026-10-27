using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Agent work stays in the tenant of the person who reaches it. In a host that projects a tenant per
/// sign-in, one person keeps one actor id in every tenant, and the application's resolver gives that
/// actor id the approver role on every project. Signed in for the project's tenant the person reaches
/// its thread, runs, proposals, inbox entries, consultations and requests; signed in for another
/// tenant the person reaches none of them, before any participant row, decision, thread, run or count
/// is made. Forgetting a deleted subject in its scope leaves an equal id of another tenant alone. On
/// both relational providers.
/// </summary>
public sealed class AssistantTenantBindingTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    internal const string Issuer = "https://idp.tenant-binding.test";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string AgentId = "project-assistant";
    private const string OpsAgent = "ops-agent";

    /// <summary>The person the resolver makes an approver of every project, by actor id only.</summary>
    private const string Person = "person-1";

    /// <summary>A person without a role who started a run whose proposal waits.</summary>
    private const string Initiator = "person-2";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task By_id_routes_and_decisions_refuse_a_thread_of_another_tenant_before_writing_anything(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        var project = AddProject(app, TenantA);
        var thread = await CreateThreadAsync(app, project);
        var queued = await AddRunAsync(app, thread, NhAssistantRunStatuses.Queued);
        var failed = await AddRunAsync(app, thread, NhAssistantRunStatuses.Failed, Guid.NewGuid());
        var pending = await AddProposalAsync(app, thread, queued.Id, NhAssistantApprovalStatuses.Pending);
        var bulk = await AddProposalAsync(app, thread, queued.Id, NhAssistantApprovalStatuses.Pending);
        var uncertain = await AddProposalAsync(app, thread, queued.Id, NhAssistantApprovalStatuses.ExecutionUnknown);
        var rejected = await AddProposalAsync(app, thread, queued.Id, NhAssistantApprovalStatuses.Rejected);
        using var other = CreateClient(app, Person, TenantB);
        using var own = CreateClient(app, Person, TenantA);
        var id = thread.ConversationId;

        // The same person signed in for another tenant finds the thread, its runs and proposals unknown.
        foreach (var path in new[]
        {
            $"/api/assistant/threads/{id}/runs",
            $"/api/assistant/threads/{id}/consultations",
            $"/api/assistant/threads/{id}/proposals",
            $"/api/assistant/threads/{id}/memory",
            $"/api/assistant/threads/{id}/artifacts",
            $"/api/assistant/threads/{id}/artifacts/{Guid.NewGuid()}/content",
            $"/api/assistant/conversations/{id}"
        })
        {
            await AssertErrorAsync(await other.GetAsync(path), HttpStatusCode.NotFound, NhAssistantErrorCodes.ConversationNotFound);
        }
        await AssertErrorAsync(await other.PostAsync($"/api/assistant/runs/{queued.Id}/cancel", null), HttpStatusCode.NotFound, NhAssistantErrorCodes.ConversationNotFound);
        await AssertErrorAsync(await other.PostAsJsonAsync($"/api/assistant/runs/{failed.Id}/retry", new { mode = "resume" }), HttpStatusCode.NotFound, NhAssistantErrorCodes.ConversationNotFound);
        await AssertErrorAsync(await other.PostAsJsonAsync($"/api/assistant/runs/{failed.Id}/retry", new { mode = "restart" }), HttpStatusCode.NotFound, NhAssistantErrorCodes.ConversationNotFound);
        foreach (var decision in new[] { "approve", "reject" })
        {
            await AssertErrorAsync(
                await other.PostAsJsonAsync($"/api/assistant/approvals/{pending.Id}/decisions", new { decision, reason = "Another tenant." }),
                HttpStatusCode.NotFound,
                NhAssistantErrorCodes.ApprovalNotFound);
        }
        var many = await ReadJsonAsync(await other.PostAsJsonAsync("/api/assistant/approvals/decisions", new { approvalIds = new[] { bulk.Id }, decision = "reject" }));
        var outcome = Assert.Single(many.GetProperty("results").EnumerateArray());
        Assert.False(outcome.GetProperty("succeeded").GetBoolean());
        Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, outcome.GetProperty("code").GetString());
        await AssertErrorAsync(
            await other.PostAsJsonAsync($"/api/assistant/approvals/{uncertain.Id}/resolve", new { action = "mark-resolved" }),
            HttpStatusCode.NotFound,
            NhAssistantErrorCodes.ApprovalNotFound);
        await AssertErrorAsync(
            await other.PutAsJsonAsync($"/api/assistant/approvals/{rejected.Id}/feedback", new { category = NhAssistantProposalFeedbackCategories.Incorrect }),
            HttpStatusCode.NotFound,
            NhAssistantErrorCodes.ApprovalNotFound);

        // The application's own channel applies the same rule with the person's tenant.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<INhAssistantProposalDecisionService>();
            var bound = new NhAssistantDecidingPerson(Principal(Person), ActorOf(Person), TenantB);
            Assert.Equal(NhAssistantErrorCodes.ApprovalNotFound, CodeOf(await service.GetDecisionFactsAsync(bound, pending.Id, CancellationToken.None)));
            Assert.Equal(
                NhAssistantErrorCodes.ApprovalNotFound,
                CodeOf(await service.DecideAsync(bound, pending.Id, pending.ProposalHash, NhAssistantProposalDecision.Reject, "messaging-reply", "reply-1", CancellationToken.None)));
        }

        // Nothing changed and nothing joined the person to the thread.
        Assert.DoesNotContain(await ParticipantsAsync(app, id), participant => participant.ActorId == ActorOf(Person));
        await using (var context = Context(app))
        {
            Assert.Equal(NhAssistantRunStatuses.Queued, (await context.Runs.SingleAsync(run => run.Id == queued.Id)).Status);
            Assert.Equal(NhAssistantRunStatuses.Failed, (await context.Runs.SingleAsync(run => run.Id == failed.Id)).Status);
            Assert.Equal(2, await context.Runs.CountAsync(run => run.ConversationId == id));
            Assert.Equal(NhAssistantApprovalStatuses.Pending, (await context.Approvals.SingleAsync(approval => approval.Id == pending.Id)).Status);
            Assert.Equal(NhAssistantApprovalStatuses.Pending, (await context.Approvals.SingleAsync(approval => approval.Id == bulk.Id)).Status);
            Assert.Equal(NhAssistantApprovalStatuses.ExecutionUnknown, (await context.Approvals.SingleAsync(approval => approval.Id == uncertain.Id)).Status);
            Assert.False(await context.ProposalFeedback.AnyAsync());
        }
        var operations = app.Services.GetRequiredService<RecordingOperations>();
        Assert.Empty(operations.Enqueued);
        Assert.Empty(operations.Executions);

        // Signed in for the project's tenant, the same person reads, joins the thread, decides and cancels.
        Assert.Equal(HttpStatusCode.OK, (await own.GetAsync($"/api/assistant/threads/{id}/memory")).StatusCode);
        var runs = await ReadJsonAsync(await own.GetAsync($"/api/assistant/threads/{id}/runs"));
        Assert.Equal(2, runs.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await own.GetAsync($"/api/assistant/conversations/{id}")).StatusCode);
        await AssertErrorAsync(await own.GetAsync($"/api/assistant/threads/{id}/artifacts/{Guid.NewGuid()}/content"), HttpStatusCode.NotFound, NhAssistantErrorCodes.ArtifactNotFound);
        Assert.Contains(await ParticipantsAsync(app, id), participant => participant.ActorId == ActorOf(Person));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var facts = await scope.ServiceProvider.GetRequiredService<INhAssistantProposalDecisionService>()
                .GetDecisionFactsAsync(new NhAssistantDecidingPerson(Principal(Person), ActorOf(Person), TenantA), pending.Id, CancellationToken.None);
            Assert.True(facts.Success);
        }
        var decided = await ReadJsonAsync(await own.PostAsJsonAsync($"/api/assistant/approvals/{pending.Id}/decisions", new { decision = "reject", reason = "Not now." }));
        Assert.Equal(NhAssistantApprovalStatuses.Rejected, decided.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await own.PostAsync($"/api/assistant/runs/{queued.Id}/cancel", null)).StatusCode);
        await using (var context = Context(app))
        {
            Assert.Equal(NhAssistantRunStatuses.Cancelled, (await context.Runs.SingleAsync(run => run.Id == queued.Id)).Status);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Subject_routes_read_a_subject_of_another_tenant_as_unknown(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        var project = AddProject(app, TenantA);
        var quiet = AddProject(app, TenantA);
        var thread = await CreateThreadAsync(app, project);
        var run = await AddRunAsync(app, thread, NhAssistantRunStatuses.Completed);
        await AddProposalAsync(app, thread, run.Id, NhAssistantApprovalStatuses.Pending);
        await RefreshAsync(app, project);
        using var other = CreateClient(app, Person, TenantB);
        using var own = CreateClient(app, Person, TenantA);
        var lookup = $"/api/assistant/threads?agentId={AgentId}&subjectType=project&subjectId={project}";
        var summary = new { subjectIds = new[] { project } };

        // Signed in for another tenant, the project reads as unknown and counts nothing.
        await AssertErrorAsync(await other.GetAsync(lookup), HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        await AssertErrorAsync(await other.GetAsync($"/api/assistant/subjects/project/{project}/work"), HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        await AssertErrorAsync(await other.GetAsync($"/api/assistant/subjects/project/{project}/agents"), HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        var counted = Assert.Single((await ReadJsonAsync(await other.PostAsJsonAsync("/api/assistant/subjects/project/summary", summary))).GetProperty("items").EnumerateArray());
        Assert.Equal((0, 0, false), (counted.GetProperty("openProposals").GetInt32(), counted.GetProperty("proposalsToDecide").GetInt32(), counted.GetProperty("runActive").GetBoolean()));
        var open = new { agentId = AgentId, subjectType = "project", subjectId = quiet };
        await AssertErrorAsync(await other.PostAsJsonAsync("/api/assistant/threads/open", open), HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        var start = new { agentId = AgentId, subjectType = "project", subjectId = project, message = "Review it." };
        await AssertErrorAsync(await other.PostAsJsonAsync("/api/assistant/threads/start", start), HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        Assert.DoesNotContain(await ParticipantsAsync(app, thread.ConversationId), participant => participant.ActorId == ActorOf(Person));
        await using (var context = Context(app))
        {
            Assert.False(await context.Threads.AnyAsync(stored => stored.SubjectId == quiet));
            Assert.Equal(1, await context.Runs.CountAsync(stored => stored.ConversationId == thread.ConversationId));
        }
        Assert.Empty(app.Services.GetRequiredService<RecordingOperations>().Enqueued);

        // Signed in for the project's tenant, the same person sees its work, opens a thread and starts it.
        var found = await ReadJsonAsync(await own.GetAsync(lookup));
        Assert.Equal(thread.ConversationId, found.GetProperty("thread").GetProperty("conversationId").GetGuid());
        var work = await ReadJsonAsync(await own.GetAsync($"/api/assistant/subjects/project/{project}/work"));
        Assert.Single(work.GetProperty("openProposals").EnumerateArray());
        var agents = await ReadJsonAsync(await own.GetAsync($"/api/assistant/subjects/project/{project}/agents"));
        Assert.Contains(agents.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("agentId").GetString() == AgentId);
        var ownCount = Assert.Single((await ReadJsonAsync(await own.PostAsJsonAsync("/api/assistant/subjects/project/summary", summary))).GetProperty("items").EnumerateArray());
        Assert.Equal((1, 1), (ownCount.GetProperty("openProposals").GetInt32(), ownCount.GetProperty("proposalsToDecide").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await own.PostAsJsonAsync("/api/assistant/threads/open", open)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await own.PostAsJsonAsync("/api/assistant/threads/start", start)).StatusCode);
        Assert.Single(app.Services.GetRequiredService<RecordingOperations>().Enqueued);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_inbox_and_the_counts_list_only_the_proposals_of_the_callers_tenant(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        var projectA = AddProject(app, TenantA);
        var projectB = AddProject(app, TenantB);
        var threadA = await CreateThreadAsync(app, projectA);
        var threadB = await CreateThreadAsync(app, projectB);
        var runA = await AddRunAsync(app, threadA, NhAssistantRunStatuses.Completed);
        var runB = await AddRunAsync(app, threadB, NhAssistantRunStatuses.Completed);
        var pendingA = await AddProposalAsync(app, threadA, runA.Id, NhAssistantApprovalStatuses.Pending);
        var pendingB = await AddProposalAsync(app, threadB, runB.Id, NhAssistantApprovalStatuses.Pending);
        var uncertainA = await AddProposalAsync(app, threadA, runA.Id, NhAssistantApprovalStatuses.ExecutionUnknown, ActorOf(Initiator));
        await RefreshAsync(app, projectA);
        await RefreshAsync(app, projectB);
        using var personB = CreateClient(app, Person, TenantB);
        using var personA = CreateClient(app, Person, TenantA);
        using var initiatorB = CreateClient(app, Initiator, TenantB);
        using var initiatorA = CreateClient(app, Initiator, TenantA);

        // The approver of both projects sees in each tenant only that tenant's proposals, flat and grouped.
        Assert.Equal([pendingB.Id], await InboxAsync(personB, ""));
        Assert.Equal([pendingA.Id], await InboxAsync(personA, ""));
        var grouped = await ReadJsonAsync(await personB.GetAsync("/api/assistant/approvals/inbox?grouped=true"));
        var group = Assert.Single(grouped.GetProperty("groups").EnumerateArray());
        Assert.Equal(projectB, group.GetProperty("subjectId").GetString());
        Assert.Empty(await InboxAsync(personB, "?status=execution-unknown"));
        Assert.Equal([uncertainA.Id], await InboxAsync(personA, "?status=execution-unknown"));

        // The person who started the run sees its uncertain proposal only in its tenant.
        Assert.Empty(await InboxAsync(initiatorB, "?status=execution-unknown"));
        Assert.Equal([uncertainA.Id], await InboxAsync(initiatorA, "?status=execution-unknown"));

        // A digest counts what the inbox lists, in the person's tenant.
        await using var scope = app.Services.CreateAsyncScope();
        var counts = scope.ServiceProvider.GetRequiredService<INhAssistantProposalCounts>();
        string[] statuses = [NhAssistantInboxStatuses.Pending, NhAssistantInboxStatuses.ExecutionUnknown];
        Assert.Equal((1, 0), Pair(await counts.CountAsync(new NhAssistantDecidingPerson(Principal(Person), ActorOf(Person), TenantB), statuses)));
        Assert.Equal((1, 1), Pair(await counts.CountAsync(new NhAssistantDecidingPerson(Principal(Person), ActorOf(Person), TenantA), statuses)));
        Assert.Equal((0, 0), Pair(await counts.CountAsync(new NhAssistantDecidingPerson(Principal(Initiator), ActorOf(Initiator), TenantB), statuses)));
        Assert.Equal((0, 1), Pair(await counts.CountAsync(new NhAssistantDecidingPerson(Principal(Initiator), ActorOf(Initiator), TenantA), statuses)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Consult_and_request_tools_refuse_another_tenants_subject_and_the_whole_application(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("The project is on track.")
            .RespondWithText("Nothing waits for the whole application.");
        await using var app = await StartAsync(provider, model);
        var project = AddProject(app, TenantA);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var handler = services.GetRequiredService<NhAssistantConsultationToolHandler>();
        var runOptions = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns;
        var aboutProjects = runOptions[AgentId].Consultations.Single();
        var aboutApplication = runOptions[OpsAgent].Consultations.Single();
        var question = Input(new { subjectId = project, question = "How does the project do?" });
        var global = Input(new { subjectId = NhAiSystemSubjects.GlobalId, question = "What waits?" });
        var request = Input(new { subjectId = project, message = "Review the project." });

        // An asker of another tenant consults nothing and starts nothing: not the project, not the whole
        // application; neither reaches the consulted model.
        var otherProject = await handler.ConsultAsync(aboutProjects, question, Asker(TenantB), CancellationToken.None);
        var otherApplication = await handler.ConsultAsync(aboutApplication, global, Asker(TenantA), CancellationToken.None);
        var otherRequest = await handler.RequestAsync(aboutProjects, request, Asker(TenantB), CancellationToken.None);
        Assert.Equal(NhAssistantErrorCodes.ConsultSubjectUnavailable, CodeOf(otherProject));
        Assert.Equal(NhAssistantErrorCodes.ConsultSubjectUnavailable, CodeOf(otherApplication));
        Assert.Equal(NhAssistantErrorCodes.ConsultSubjectUnavailable, CodeOf(otherRequest));
        Assert.Empty(model.Requests);
        await using (var context = Context(app))
        {
            Assert.False(await context.Consultations.AnyAsync());
            Assert.False(await context.Runs.AnyAsync());
        }
        Assert.Empty(app.Services.GetRequiredService<RecordingOperations>().Enqueued);

        // The project's own tenant consults and requests; an operator without a tenant consults the
        // whole application.
        var answered = await handler.ConsultAsync(aboutProjects, question, Asker(TenantA), CancellationToken.None);
        Assert.True(answered.Success, CodeOf(answered));
        Assert.Equal("The project is on track.", answered.Data!.Answer);
        var requested = await handler.RequestAsync(aboutProjects, request, Asker(TenantA), CancellationToken.None);
        Assert.True(requested.Success, CodeOf(requested));
        Assert.Single(app.Services.GetRequiredService<RecordingOperations>().Enqueued);
        var applicationAnswer = await handler.ConsultAsync(aboutApplication, global, Asker(null), CancellationToken.None);
        Assert.True(applicationAnswer.Success, CodeOf(applicationAnswer));
        await using (var context = Context(app))
        {
            Assert.Equal(2, await context.Consultations.CountAsync());
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Forgetting_a_subject_in_its_scope_leaves_an_equal_id_of_another_tenant_alone(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        var subject = new NhAiSubjectRef("project", "42");
        var threadA = await CreateThreadAsync(app, Described(subject, TenantA));
        var threadB = await CreateThreadAsync(app, Described(subject, TenantB));
        var runA = await AddRunAsync(app, threadA, NhAssistantRunStatuses.Queued);
        var runB = await AddRunAsync(app, threadB, NhAssistantRunStatuses.Queued);
        var proposalB = await AddProposalAsync(app, threadB, runB.Id, NhAssistantApprovalStatuses.Pending);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var runs = scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>();
            foreach (var thread in new[] { threadA, threadB })
            {
                await runs.ReplaceMembersAsync(thread.ScopeKey, subject, [new NhAiSubjectMember(ActorOf(Person), NhAiSubjectRole.Approver)], DateTimeOffset.UtcNow, CancellationToken.None);
            }
        }

        // Tenant A deleted its project 42: only tenant A's thread and run go.
        int forgotten;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            forgotten = (await scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>()
                .ForgetSubjectAsync(subject, threadA.ScopeKey)).Data;
        }

        Assert.Equal(1, forgotten);
        await using (var context = Context(app))
        {
            Assert.False(await context.Threads.AnyAsync(thread => thread.ConversationId == threadA.ConversationId));
            Assert.False(await context.Runs.AnyAsync(run => run.Id == runA.Id));
            Assert.Equal(NhAssistantRunStatuses.Queued, (await context.Runs.SingleAsync(run => run.Id == runB.Id)).Status);
            Assert.Equal(NhAssistantApprovalStatuses.Pending, (await context.Approvals.SingleAsync(approval => approval.Id == proposalB.Id)).Status);
            Assert.False(await context.SubjectMembers.AnyAsync(member => member.ScopeKey == threadA.ScopeKey));
            Assert.True(await context.SubjectMembers.AnyAsync(member => member.ScopeKey == threadB.ScopeKey));
        }

        // The overload without a scope still forgets the id in every scope, for ids unique across them.
        await using (var scope = app.Services.CreateAsyncScope())
        {
#pragma warning disable CS0618 // The unscoped overload is kept for compatibility; this proves what it still does.
            forgotten = (await scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>().ForgetSubjectAsync(subject)).Data;
#pragma warning restore CS0618
        }

        Assert.Equal(1, forgotten);
        await using (var context = Context(app))
        {
            Assert.False(await context.Threads.AnyAsync(thread => thread.SubjectId == subject.Id));
        }
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            configure: services =>
            {
                // People sign in at one issuer whose tokens name their tenant, so one person keeps one
                // actor id in every tenant.
                services.AddAuthentication(TenantBindingAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TenantBindingAuthentication>(TenantBindingAuthentication.SchemeName, _ => { });
                services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaims(Issuer));
                services.AddSingleton(new TwoTenantProjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TenantBindingPrincipals>());
                // Runs are recorded, not executed: the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TwoTenantProjectResolver>()
                .AddAgent(AssistantTestData.Agent(OpsAgent))
                .ConfigureAgent(AgentId, run => run
                    .WorksOn("project")
                    .AllowConsultation("project", consult => consult
                        .Description("Knows the agent work on a project.")
                        .AllowRequests()))
                .ConfigureAgent(OpsAgent, run => run
                    .WorksOnApplication()
                    .AllowConsultation(NhAiSubjectRef.SystemType, consult => consult
                        .Description("Knows the agent work on the whole application.")
                        .Proposals(false))),
            provider: provider);
    }

    /// <summary>
    /// A project of <paramref name="tenantId"/> that the resolver describes from now on.
    /// </summary>
    private static string AddProject(AssistantWebApplication app, string tenantId)
    {
        var id = Guid.NewGuid().ToString("D");
        app.Services.GetRequiredService<TwoTenantProjects>().Tenants[id] = tenantId;
        return id;
    }

    private static NhAiSubjectInfo Described(NhAiSubjectRef subject, string tenantId)
    {
        return new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(tenantId, null))
        {
            TenantId = tenantId,
            Version = "1",
            DisplayName = "Project " + subject.Id
        };
    }

    private static async Task<AssistantThread> CreateThreadAsync(AssistantWebApplication app, string projectId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var info = await scope.ServiceProvider.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId), CancellationToken.None);
        return await CreateThreadAsync(app, info!);
    }

    private static async Task<AssistantThread> CreateThreadAsync(AssistantWebApplication app, NhAiSubjectInfo info)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: false, CancellationToken.None))!;
        return (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
    }

    private static async Task<AssistantRun> AddRunAsync(AssistantWebApplication app, AssistantThread thread, string status, Guid? operationId = null)
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = NhAssistantTriggerIds.Manual,
            InitiatorActorId = ActorOf(Initiator),
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = status,
            AgentVersion = 1,
            BackgroundOperationId = operationId,
            CreatedAt = createdAt,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        return run;
    }

    /// <summary>
    /// A deferred proposal of the run in <paramref name="status"/>; a decided one was decided by
    /// <see cref="Person"/>.
    /// </summary>
    private static async Task<AssistantApproval> AddProposalAsync(
        AssistantWebApplication app,
        AssistantThread thread,
        Guid runId,
        string status,
        string? initiatorActorId = null)
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-4);
        var decided = status != NhAssistantApprovalStatuses.Pending;
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = Guid.NewGuid(),
            ProposalHash = "sha256:" + Guid.NewGuid().ToString("N"),
            ProposalJson = "{}",
            ToolId = "projects.change-status",
            Summary = "Change the project status.",
            ArgumentsPreview = "{}",
            Status = status,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            RunId = runId,
            InitiatorActorId = initiatorActorId,
            DecidedByActorId = decided ? ActorOf(Person) : null,
            DecidedAt = decided ? createdAt.AddMinutes(1) : null,
            Mode = NhAssistantApprovalModes.Deferred,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var context = Context(app);
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval;
    }

    private static async Task RefreshAsync(AssistantWebApplication app, string projectId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<INhAiSubjectAccess>().RefreshAsync(new NhAiSubjectRef("project", projectId))).Success);
    }

    private static async Task<IReadOnlyList<AssistantConversationParticipant>> ParticipantsAsync(AssistantWebApplication app, Guid conversationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().GetParticipantsAsync(conversationId, CancellationToken.None);
    }

    private static NhAssistantDbContext Context(AssistantWebApplication app)
    {
        return app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
    }

    private static HttpClient CreateClient(AssistantWebApplication app, string person, string tenantId)
    {
        var client = app.CreateClient(person);
        client.DefaultRequestHeaders.Add(TenantBindingAuthentication.TenantHeader, tenantId);
        return client;
    }

    /// <summary>
    /// <see cref="Person"/> as a tool call outside a turn sees them, as an MCP client calls the tools,
    /// bound to <paramref name="tenantId"/>.
    /// </summary>
    private static NhAiInvocationContext Asker(string? tenantId)
    {
        return new NhAiInvocationContext(ActorOf(Person), "project-assistance", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Human,
            TenantId = tenantId
        };
    }

    private static async Task<IReadOnlyList<Guid>> InboxAsync(HttpClient client, string query)
    {
        var inbox = await ReadJsonAsync(await client.GetAsync("/api/assistant/approvals/inbox" + query));
        return inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("approvalId").GetGuid()).ToArray();
    }

    private static (int Pending, int ExecutionUnknown) Pair(IReadOnlyDictionary<string, int> counts)
    {
        return (counts[NhAssistantInboxStatuses.Pending], counts[NhAssistantInboxStatuses.ExecutionUnknown]);
    }

    /// <summary>
    /// The platform's actor id of a person of the issuer, as the AI ASP.NET integration makes it: the
    /// same in every tenant.
    /// </summary>
    internal static string ActorOf(string subject)
    {
        return "oidc:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Issuer + "\n" + subject)));
    }

    private static ClaimsPrincipal Principal(string subject)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", subject), new Claim("iss", Issuer), new Claim("permission", "assistant")],
            "test"));
    }

    private static JsonElement Input(object value)
    {
        return JsonSerializer.SerializeToElement(value, Json);
    }

    private static string? CodeOf(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"{response.RequestMessage?.RequestUri} answered {response.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri} answered {response.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }
}

/// <summary>
/// The tenant of each test project, and every person's role on every project by actor id only, as an
/// application whose roles do not depend on the tenant of the sign-in gives them.
/// </summary>
internal sealed class TwoTenantProjects
{
    public ConcurrentDictionary<string, string> Tenants { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, NhAiSubjectRole> Roles { get; } = new(new Dictionary<string, NhAiSubjectRole>
    {
        [AssistantTenantBindingTests.ActorOf("person-1")] = NhAiSubjectRole.Approver
    });
}

internal sealed class TwoTenantProjectResolver(TwoTenantProjects projects) : INhAiSubjectAccessResolver
{
    public string SubjectType => "project";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        if (!projects.Tenants.TryGetValue(subject.Id, out var tenantId))
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(null);
        }

        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(tenantId, null))
        {
            TenantId = tenantId,
            Version = "1",
            DisplayName = "Project " + subject.Id[..8]
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(projects.Roles.TryGetValue(actorId, out var role)
            ? new NhAiSubjectAccess(role)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, role) in projects.Roles)
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
        }
    }
}

/// <summary>
/// Rebuilds every person with the assistant's access, so the whole application's read policy admits
/// them and only the tenant rule keeps them out.
/// </summary>
internal sealed class TenantBindingPrincipals : INhAiPrincipalFactory
{
    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId), new Claim("permission", "assistant")],
            "test")));
    }
}

/// <summary>
/// Signs the test client's person in at <see cref="AssistantTenantBindingTests.Issuer"/> with the tenant
/// of the <see cref="TenantHeader"/> header in the <c>tenant_id</c> claim.
/// </summary>
internal sealed class TenantBindingAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "tenant-binding-test";
    public const string TenantHeader = "X-Test-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[AssistantWebApplication.UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("iss", AssistantTenantBindingTests.Issuer),
            new("sub", user),
            new(ClaimTypes.NameIdentifier, user)
        };
        var tenant = Request.Headers[TenantHeader].ToString();
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            claims.Add(new Claim("tenant_id", tenant));
        }
        if (Request.Headers.ContainsKey(AssistantWebApplication.AccessHeader))
        {
            claims.Add(new Claim("permission", "assistant"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
