using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.Common.Actors;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-281: a partner portal that has its own EF context and its own users, without NewHeap
/// Identity, hosts background operations and notifications in its own schemas, tells the platform
/// who its users are, and runs an agent on them from a domain event to an executed proposal.
/// </summary>
public sealed class PartnerPortalSamplesTests(PartnerPortalSampleHost host) : IClassFixture<PartnerPortalSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_portal_without_NewHeap_Identity_runs_an_agent_on_its_own_context()
    {
        // SPM-281
        var project = Guid.NewGuid();
        var withdrawn = Guid.NewGuid();
        await host.AddProjectAsync(project, "PARTNER-1");
        await host.AddProjectAsync(withdrawn, "PARTNER-2");

        // The submission and its event share the portal's transaction. Inside it the trigger is only
        // a background operation of the portal: a rolled-back submission leaves nothing behind, in
        // the portal or in the assistant, and a committed one starts the agent after the commit.
        var rolledBack = Assert.Single(await host.SubmitAsync(withdrawn, commit: false));
        Assert.Equal(NhAssistantTriggerOutcomes.Deferred, rolledBack.Outcome);
        var receipt = Assert.Single(await host.SubmitAsync(project, commit: true));
        Assert.Equal(NhAssistantTriggerOutcomes.Deferred, receipt.Outcome);
        Assert.Null(receipt.ConversationId);
        using var approver = host.CreateClient(host.ApproverId.ToString());
        var conversationId = await WaitForThreadAsync(approver, project);

        var proposal = await WaitForProposalAsync(approver, conversationId, NhAssistantApprovalStatuses.Pending);
        Assert.Equal(ProjectAiTools.ChangeStatusToolId, proposal.ToolId);
        Assert.Equal(PartnerPortalSampleHost.ApprovalPolicyId, proposal.PolicyId);

        // The agent's run is a background operation in the portal's own context, owned by the agent.
        var runOperation = await host.WaitForRunOperationAsync(project, NhBackgroundOperationStatus.Succeeded);
        Assert.Equal(NhBackgroundOperationOwnerKind.Agent, runOperation.OwnerKind);
        Assert.Equal(1, await host.CountRunOperationsAsync(project));
        Assert.Equal(0, await host.CountRunOperationsAsync(withdrawn));
        var withdrawnThread = await approver.GetFromJsonAsync<NhAssistantThreadLookupDto>(
            $"/api/assistant/threads?agentId={PartnerPortalSampleHost.AgentId}&subjectType={PartnerProjects.Type}&subjectId={withdrawn:D}",
            Json);
        Assert.Null(withdrawnThread!.Thread);

        // The approver hears about it in the portal's own inbox, in the language the portal knows for them.
        var inbox = await host.WaitForNotificationAsync(host.ApproverId);
        Assert.Equal("assistant-agent", inbox.Category);
        Assert.Contains(inbox.Messages, message => message.Message == "Een voorstel wacht op een besluit.");

        var decision = await approver.PostAsJsonAsync(
            $"/api/assistant/approvals/{proposal.ApprovalId}/decisions",
            new NhAssistantProposalDecisionRequest("approve", proposal.ProposalHash, "Partner review"),
            Json);
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        await WaitForProposalAsync(approver, conversationId, NhAssistantApprovalStatuses.Executed);
        Assert.Contains(host.Projects.StatusChanges, change => change.ProjectId == project && change.Status == ProjectStatus.Active);

        // NewHeap Identity is not part of this host.
        Assert.Null(host.Services.GetService<INhIdentityDbContext>());
    }

    /// <summary>
    /// The thread the agent works in once the submission committed and its trigger fired.
    /// </summary>
    private static async Task<Guid> WaitForThreadAsync(HttpClient client, Guid projectId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var lookup = await client.GetFromJsonAsync<NhAssistantThreadLookupDto>(
                $"/api/assistant/threads?agentId={PartnerPortalSampleHost.AgentId}&subjectType={PartnerProjects.Type}&subjectId={projectId:D}",
                Json);
            if (lookup?.Thread is { } thread)
            {
                return thread.ConversationId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"No thread was created for partner project {projectId}.");
    }

    private static async Task<NhAssistantProposalDto> WaitForProposalAsync(
        HttpClient client,
        Guid conversationId,
        string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantProposalDto[] proposals = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/assistant/threads/{conversationId}/proposals");
            if (response.IsSuccessStatusCode)
            {
                proposals = await response.Content.ReadFromJsonAsync<NhAssistantProposalDto[]>(Json) ?? [];
                if (proposals.FirstOrDefault(item => item.Status == status) is { } match)
                {
                    return match;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"No proposal reached '{status}'; seen: {string.Join(", ", proposals.Select(item => item.Status))}.");
    }
}

/// <summary>
/// The assistant sample host as a partner portal: its own EF context with its own users and
/// project memberships, background operations and notifications registered without the NewHeap
/// platform builder, and an agent that reviews submitted partner projects.
/// </summary>
public sealed class PartnerPortalSampleHost : AssistantSampleHost
{
    public const string AgentId = "partner-project-reviewer";
    public const string TriggerId = "partner-project-submitted";
    public const string ApprovalPolicyId = "partner-project-review";

    private readonly InMemoryStorage _hangfireStorage = new();

    public Guid ApproverId { get; } = Guid.NewGuid();

    public Guid SubmitterId { get; } = Guid.NewGuid();

    public override async ValueTask InitializeAsync()
    {
        Model.Use(new SampleAssistantChatClient());
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _hangfireStorage.Dispose();
    }

    protected override async Task OnDatabaseStartedAsync()
    {
        // The portal creates its schema itself; in an application this is its own migration.
        var options = new DbContextOptionsBuilder<PartnerPortalDbContext>()
            .UseNewHeapPostgreSql(ConnectionString)
            .Options;
        await using var dbContext = new PartnerPortalDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.Partners.AddRange(
            new PartnerUser(ApproverId, "Anna Approver", "anna@partner.localhost", "nl-NL"),
            new PartnerUser(SubmitterId, "Sam Submitter", "sam@partner.localhost", "en-US"));
        await dbContext.SaveChangesAsync();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddDbContext<PartnerPortalDbContext>(options => options.UseNewHeapPostgreSql(ConnectionString));

        // The platform modules on the portal's context, without the NewHeap platform builder.
        services.AddNewHeapNotifications<PartnerPortalDbContext>();
        services.AddNewHeapHangfireProcessing(
            hangfire => hangfire.UseStorage(_hangfireStorage),
            backgroundJobServerOptions: server =>
            {
                server.ServerName = "partner-portal-" + Guid.NewGuid().ToString("N");
                server.WorkerCount = 2;
                server.SchedulePollingInterval = TimeSpan.FromMilliseconds(100);
            });
        services.AddNewHeapBackgroundOperations<PartnerPortalDbContext>(operations =>
        {
            operations.Options.DispatchInterval = TimeSpan.FromMilliseconds(200);
            operations.Options.LiveUpdatesEnabled = false;
            operations.Options.CleanupEnabled = false;
            operations.AddAssistantAgentRuns();
        });

        // The portal tells the platform who its users are; it has no divisions.
        services.AddNewHeapActorDirectory<PartnerDirectory>();
        services.AddNewHeapAuthenticationForwarding(SampleTestAuthenticationHandler.Scheme);
    }

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-intake-reviewer.name",
                DescriptionKey: "nh-assistant.agents.sample-project-intake-reviewer.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectIntakeReviewerInstructions,
                ToolSelectors: [ProjectAiTools.ChangeStatusToolId],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .AddSubjectType<PartnerProjectResolver>()
            .AddApprovalPolicy(ApprovalPolicyId, policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(3))
                .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                .BindTo(NhAiApprovalBinding.ToolContract))
            .ConfigureAgent(AgentId, agent => agent
                .Grant(ProjectAiTools.ChangeStatusToolId)
                .TriggerOnly()
                .UseApprovalPolicy(ApprovalPolicyId))
            .AddTrigger<PartnerProjectSubmitted>(TriggerId, trigger => trigger
                .StartAgent(AgentId)
                .Subject(evt => PartnerProjects.For(evt.ProjectId))
                .Message(evt => $"Partner project {evt.ProjectId:D} was submitted: propose status active.")
                .Dedupe(evt => evt.SubmissionId.ToString("N"))
                .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero))
            // Agent notifications also reach the portal's own inbox through the notifications module.
            .UseInAppAgentNotifications());
    }

    public async Task AddProjectAsync(Guid projectId, string key)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PartnerPortalDbContext>();
        dbContext.Projects.Add(new PartnerProject(projectId, key, DivisionId));
        dbContext.Memberships.AddRange(
            new PartnerMembership(Guid.NewGuid(), projectId, ApproverId, NhAiSubjectRole.Approver),
            new PartnerMembership(Guid.NewGuid(), projectId, SubmitterId, NhAiSubjectRole.Participant));
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Records a submission and publishes its event in the same transaction, as an application
    /// does when it publishes from its unit of work.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantTriggerReceipt>> SubmitAsync(Guid projectId, bool commit)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PartnerPortalDbContext>();
        var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var submission = new PartnerSubmission(Guid.NewGuid(), projectId, SubmitterId);
        dbContext.Submissions.Add(submission);
        await dbContext.SaveChangesAsync();
        var receipts = await triggers.PublishAsync(new PartnerProjectSubmitted(submission.Id, projectId));

        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return receipts;
    }

    public async Task<NhBackgroundOperation> WaitForRunOperationAsync(Guid projectId, NhBackgroundOperationStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        NhBackgroundOperation? operation = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<PartnerPortalDbContext>();
                operation = await RunOperations(dbContext, projectId).AsNoTracking().FirstOrDefaultAsync();
            }

            if (operation?.Status == status)
            {
                return operation;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException($"The run of project {projectId} stayed {operation?.Status} instead of {status}.");
    }

    public async Task<int> CountRunOperationsAsync(Guid projectId)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PartnerPortalDbContext>();
        return await RunOperations(dbContext, projectId).CountAsync();
    }

    public async Task<NhUserNotification> WaitForNotificationAsync(Guid userId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<PartnerPortalDbContext>();
                var notification = await dbContext.Set<NhUserNotification>()
                    .AsNoTracking()
                    .Include(item => item.Messages)
                    .FirstOrDefaultAsync(item => item.UserId == userId);
                if (notification is not null)
                {
                    return notification;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException($"User {userId} received no notification.");
    }

    private static IQueryable<NhBackgroundOperation> RunOperations(PartnerPortalDbContext dbContext, Guid projectId)
    {
        return dbContext.Set<NhBackgroundOperation>()
            .Where(item => item.OperationType == NhAssistantAgentRunOperationTypes.Run
                && item.DomainObjectType == PartnerProjects.Type
                && item.DomainObjectId == projectId.ToString("D"));
    }
}

/// <summary>The portal's own context: partners, their projects and memberships, and the platform modules.</summary>
public sealed class PartnerPortalDbContext(DbContextOptions<PartnerPortalDbContext> options) : DbContext(options)
{
    public DbSet<PartnerUser> Partners => Set<PartnerUser>();

    public DbSet<PartnerProject> Projects => Set<PartnerProject>();

    public DbSet<PartnerMembership> Memberships => Set<PartnerMembership>();

    public DbSet<PartnerSubmission> Submissions => Set<PartnerSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("partner");
        modelBuilder.Entity<PartnerUser>().ToTable("Partners");
        modelBuilder.Entity<PartnerProject>().ToTable("Projects");
        modelBuilder.Entity<PartnerMembership>().ToTable("Memberships");
        modelBuilder.Entity<PartnerSubmission>().ToTable("Submissions");

        // Background operations land in schema nhops and notifications in nhnotify.
        modelBuilder.AddNewHeapBackgroundOperations();
        modelBuilder.AddNewHeapNotifications();
    }
}

public sealed record PartnerUser(Guid Id, string Name, string Email, string Culture);

public sealed record PartnerProject(Guid Id, string Key, Guid TenantId);

public sealed record PartnerMembership(Guid Id, Guid ProjectId, Guid UserId, NhAiSubjectRole Role);

public sealed record PartnerSubmission(Guid Id, Guid ProjectId, Guid SubmittedBy);

public sealed record PartnerProjectSubmitted(Guid SubmissionId, Guid ProjectId);

public static class PartnerProjects
{
    public const string Type = "partner-project";

    public static NhAiSubjectRef For(Guid projectId)
    {
        return new NhAiSubjectRef(Type, projectId.ToString("D"));
    }
}

/// <summary>The portal's partners as the platform's actor directory, with their own language.</summary>
public sealed class PartnerDirectory(PartnerPortalDbContext dbContext) : INhActorDirectory
{
    public async Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
        IReadOnlyCollection<NhActorRef> actors,
        CancellationToken cancellationToken = default)
    {
        var ids = actors
            .Select(actor => actor.TryGetUserId(out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        var partners = await dbContext.Partners
            .AsNoTracking()
            .Where(partner => ids.Contains(partner.Id))
            .ToListAsync(cancellationToken);
        return partners.ToDictionary(
            partner => NhActorRef.ForUser(partner.Id),
            partner => new NhActorInfo(NhActorRef.ForUser(partner.Id), partner.Name)
            {
                Email = partner.Email,
                Culture = partner.Culture
            });
    }
}

/// <summary>Project memberships of the portal decide who sees and decides the agent's work.</summary>
public sealed class PartnerProjectResolver(PartnerPortalDbContext dbContext) : INhAiSubjectAccessResolver
{
    public string SubjectType => PartnerProjects.Type;

    public async ValueTask<NhAiSubjectInfo?> DescribeAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subject.Id, out var projectId) || subject.Id != projectId.ToString("D"))
        {
            return null;
        }

        var project = await dbContext.Projects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        return project is null
            ? null
            : new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, project.TenantId))
            {
                DivisionId = project.TenantId,
                DisplayName = project.Key
            };
    }

    public async ValueTask<NhAiSubjectAccess> ResolveAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subject.Id, out var projectId) || !Guid.TryParse(actorId, out var userId))
        {
            return NhAiSubjectAccess.None;
        }

        var membership = await dbContext.Memberships
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.UserId == userId, cancellationToken);
        return membership is null ? NhAiSubjectAccess.None : new NhAiSubjectAccess(membership.Role);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subject.Id, out var projectId))
        {
            yield break;
        }

        var members = await dbContext.Memberships
            .AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Join(dbContext.Partners, membership => membership.UserId, partner => partner.Id,
                (membership, partner) => new { membership.UserId, membership.Role, partner.Name })
            .ToListAsync(cancellationToken);
        foreach (var member in members)
        {
            yield return new NhAiSubjectMember(member.UserId.ToString("D"), member.Role) { DisplayName = member.Name };
        }
    }
}
