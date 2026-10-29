using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB21: <see cref="INhAssistantProposals.ProposeForAsync"/> records a deferred proposal of an agent for a
/// person without a run, through the governed path: the start rule for the person, the tool's contract and
/// the person's own rights, the policy the application names or code resolves, the application's selector
/// and the policy copy, the guards and their keys, duplicates and the rejection cooldown, presentations the
/// application prepared (refused, never dropped, over the limits) and an idempotency key. The proposal waits
/// in the agent's thread at the subject, four eyes keep the person out, the audit records a kind of its own,
/// and it executes with the person's rights only. Agent work being off on the host does not refuse recording
/// it, while its execution waits until agents work again. Both relational providers, because the proposals
/// persist.
/// </summary>
public sealed class AssistantProposalForPersonTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusTool = "projects.change-status";
    private const string SearchTool = "projects.search";
    private const string ReviewPolicy = "project-review";
    private const string FourEyesPolicy = "project-review-four-eyes";
    private const string CooldownPolicy = "project-review-cooldown";
    private const string Proposer = "approver-1";
    private const string SecondApprover = "approver-2";
    private const string Member = "member-1";
    private const string Outsider = "outsider-1";
    private const string ManagePermission = "projects.manage";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_records_a_deferred_proposal_that_waits_in_the_agents_thread_and_executes_with_their_rights(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider);
        var selections = host.Services.GetRequiredService<TestPolicySelections>();
        selections.Choose = (_, _) => ValueTask.FromResult<string?>(FourEyesPolicy);
        var english = Presentation("Change project status", "Put the project on hold.");
        var dutch = Presentation("Projectstatus wijzigen", "Zet het project in de wacht.");

        var proposed = await ProposeAsync(host, Request(Proposer, projectId, "OnHold") with
        {
            Presentations = new Dictionary<string, NhAssistantApprovalPresentation>
            {
                ["en-US"] = english,
                ["nl-NL"] = dutch
            },
            IdempotencyKey = "request-1"
        });

        Assert.True(proposed.Success, CodeOf(proposed));
        var result = proposed.Data!;
        Assert.Equal((NhAssistantProposeOutcome.Created, NhAssistantApprovalStatuses.Pending, FourEyesPolicy, true, false),
            (result.Outcome, result.Status, result.PolicyId, result.FourEyes, result.Repeated));
        var approval = await FindAsync(host, result.ApprovalId!.Value);
        var thread = (await host.Services.GetRequiredService<NhAssistantRunStore>().FindThreadByConversationAsync(approval.ConversationId, CancellationToken.None))!;
        // The proposal waits in the agent's thread at the project, created for it, without a run.
        Assert.Equal((AgentId, "project", projectId.ToString(), false), (thread.AgentId, thread.SubjectType, thread.SubjectId, thread.Shadow));
        Assert.Equal(result.ConversationId, thread.ConversationId);
        Assert.Equal(
            (NhAssistantApprovalModes.Deferred, (Guid?)null, (Guid?)null, Proposer, NhAiGrantSource.Delegated, result.ProposalHash),
            (approval.Mode, approval.RunId, approval.ConsultationId, approval.InitiatorActorId, approval.GrantSource, approval.ProposalHash));
        Assert.True(NhAssistantPersonProposals.IsPersonProposal(approval));
        // The selector chose four eyes; the proposal keeps the choice in its policy copy.
        Assert.True(NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson!).FourEyes);
        var selection = Assert.Single(selections.Seen);
        Assert.Equal(
            (AgentId, ChangeStatusTool, new NhAiSubjectRef("project", projectId.ToString()), ReviewPolicy, NhAssistantApprovalPolicySource.Application, Proposer),
            (selection.AgentId, selection.ToolId, selection.Subject, selection.ResolvedPolicyId, selection.Source, selection.InitiatorActorId));
        Assert.Equal("OnHold", selection.Arguments.GetProperty("status").GetString());
        // Approvers read the application's presentation in their own language.
        Assert.Equal((english.ToolDisplayName, english.Summary), TextOf(NhAssistantTestPresentations.Pick(approval, "en-US")));
        Assert.Equal((dutch.ToolDisplayName, dutch.Summary), TextOf(NhAssistantTestPresentations.Pick(approval, "nl-NL")));
        // The call waits outside the thread's messages, as the application passed it.
        var call = await CallAsync(host, approval.ToolInvocationId);
        Assert.Equal(
            (thread.ConversationId, Guid.Empty, NhAssistantToolCallStatuses.AwaitingApproval, approval.Id, english.ToolDisplayName),
            (call.ConversationId, call.MessageId, call.Status, call.ApprovalId, call.DisplayName));
        Assert.Contains("OnHold", call.ArgumentsJson);
        Assert.Empty(host.Tools.StatusChanges);

        // The content-free audit records the proposal and that the application recorded it for the person.
        var requested = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested);
        var recorded = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalRecordedForPerson);
        foreach (var evt in new[] { requested, recorded })
        {
            Assert.Equal(
                (Proposer, Proposer, approval.Id, thread.ConversationId, "project", projectId.ToString(), FourEyesPolicy),
                (evt.ActorId, evt.InitiatorActorId, evt.ApprovalId, evt.ConversationId, evt.SubjectType, evt.SubjectId, evt.ApprovalPolicyId));
        }
        Assert.Equal(("request-1", NhAssistantToolCallInterceptorCodes.ProposalRecorded), (recorded.IdempotencyKey, recorded.ResultCode));

        // A repeat with the same key returns the proposal it recorded and audits nothing again.
        var eventCount = host.Business.Events.Count;
        var repeated = await ProposeAsync(host, Request(Proposer, projectId, "OnHold") with { IdempotencyKey = "request-1" });
        Assert.True(repeated.Success);
        Assert.Equal((approval.Id, true, NhAssistantApprovalStatuses.Pending), (repeated.Data!.ApprovalId, repeated.Data.Repeated, repeated.Data.Status));
        Assert.Equal(eventCount, host.Business.Events.Count);
        // The key with other arguments is a conflict.
        Assert.Equal(
            NhAssistantErrorCodes.ProposalIdempotencyConflict,
            CodeOf(await ProposeAsync(host, Request(Proposer, projectId, "Active") with { IdempotencyKey = "request-1" })));

        // Four eyes keep the person out; the second approver decides.
        Assert.Equal(NhAssistantErrorCodes.FourEyesRequired, CodeOf(await DecideAsync(host, approval, Proposer)));
        var decided = await DecideAsync(host, approval, SecondApprover);
        Assert.True(decided.Success, CodeOf(decided));
        Assert.Contains(approval.Id, host.Services.GetRequiredService<RecordingOperations>().Executions);

        // It executes with the person's rights: the change happens once, as the person delegated it.
        await ExecuteAsync(host, approval.Id);
        Assert.Equal(NhAssistantApprovalStatuses.Executed, (await FindAsync(host, approval.Id)).Status);
        var change = Assert.Single(host.Tools.StatusChanges);
        Assert.Equal((projectId, "OnHold"), (change.ProjectId, change.Status));
        Assert.Equal(NhAiGrantSource.Delegated, host.Tools.Contexts.Last().GrantSource);
        // After the decision the key still names the proposal, as it is now.
        var afterwards = (await ProposeAsync(host, Request(Proposer, projectId, "OnHold") with { IdempotencyKey = "request-1" })).Data!;
        Assert.Equal((approval.Id, true, NhAssistantApprovalStatuses.Executed), (afterwards.ApprovalId, afterwards.Repeated, afterwards.Status));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_approved_proposal_never_borrows_the_agents_grant_when_the_person_lost_the_right(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider);
        var proposed = await ProposeAsync(host, Request(Proposer, projectId, "Active"));
        Assert.True(proposed.Success, CodeOf(proposed));
        var approval = await FindAsync(host, proposed.Data!.ApprovalId!.Value);
        var decided = await DecideAsync(host, approval, SecondApprover);
        Assert.True(decided.Success, CodeOf(decided));

        // The agent's grant would allow the call; the person's own rights decide alone.
        host.Services.GetRequiredService<PersonRights>().Managers.TryRemove(Proposer, out _);
        await ExecuteAsync(host, approval.Id);

        var executed = await FindAsync(host, approval.Id);
        Assert.Equal((NhAssistantApprovalStatuses.ExecutionFailed, NhAiAgentFailureCodes.NotGranted), (executed.Status, executed.ExecutionResultCode));
        Assert.Empty(host.Tools.StatusChanges);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Every_refusal_records_nothing(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider);
        var selections = host.Services.GetRequiredService<TestPolicySelections>();

        // The person the application names must be the one its resolver resolves.
        var someoneElse = Request(Proposer, projectId, "Active") with
        {
            Person = new NhAssistantDecidingPerson(Principal(Member), Proposer, null)
        };
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(await ProposeAsync(host, someoneElse)));
        // Someone outside the project may not ask the agent to work on it.
        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await ProposeAsync(host, Request(Outsider, projectId, "Active"))));
        // A participant may ask, but only with their own rights, never the agent's grant.
        Assert.Equal(NhAiAgentFailureCodes.NotGranted, CodeOf(await ProposeAsync(host, Request(Member, projectId, "Active"))));
        // The tool's contract binds the arguments.
        Assert.Equal(
            NhAiToolFailureCodes.InputInvalid,
            CodeOf(await ProposeAsync(host, new NhAssistantProposalRequest(Person(Proposer), AgentId, ChangeStatusTool, new { projectId = "not-a-guid" }, Subject(projectId)))));
        // Only a change the agent proposes with: no read, no tool outside its selectors.
        Assert.Equal(NhAssistantErrorCodes.ProposalRefused, CodeOf(await ProposeAsync(host, Request(Proposer, projectId, "Active") with { ToolId = SearchTool })));
        Assert.Equal(NhAssistantErrorCodes.ProposalRefused, CodeOf(await ProposeAsync(host, Request(Proposer, projectId, "Active") with { ToolId = "tasks.change-status" })));
        Assert.Equal(NhAssistantErrorCodes.AgentNotConfigured, CodeOf(await ProposeAsync(host, Request(Proposer, projectId, "Active") with { AgentId = "unknown-agent" })));
        // A policy the application did not register, and a selector that chooses one.
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyUnknown, CodeOf(await ProposeAsync(host, Request(Proposer, projectId, "Active") with { PolicyId = "not-registered" })));
        selections.Choose = (_, _) => ValueTask.FromResult<string?>("not-registered");
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyUnknown, CodeOf(await ProposeAsync(host, Request(Proposer, projectId, "Active"))));
        selections.Choose = (_, _) => ValueTask.FromResult<string?>(null);
        // An idempotency key the audit cannot record.
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, CodeOf(await ProposeAsync(host, Request(Proposer, projectId, "Active") with { IdempotencyKey = "a key with spaces" })));

        // A presentation over the limits is refused with the culture and the rule, never dropped.
        var tooLong = await ProposeAsync(host, Request(Proposer, projectId, "Active") with
        {
            Presentations = new Dictionary<string, NhAssistantApprovalPresentation>
            {
                ["en-US"] = Presentation("Change project status", new string('x', NhAssistantPresentationLimits.MaxSummaryLength + 1)),
                ["xx-not-a-culture"] = Presentation("Status", "Status.")
            }
        });
        Assert.Equal(NhAssistantErrorCodes.ProposalPresentationInvalid, CodeOf(tooLong));
        Assert.Equal(
            [NhAssistantPresentationRules.SummaryTooLong],
            FieldErrors(tooLong, "presentations.en-US"));
        Assert.Equal([NhAssistantPresentationRules.CultureInvalid], FieldErrors(tooLong, "presentations"));

        Assert.Empty(await ApprovalsAsync(host));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind is NhAssistantAuditEventKind.ApprovalRequested or NhAssistantAuditEventKind.AgentProposalRecordedForPerson);
        Assert.Empty(host.Tools.StatusChanges);
        await Assert.ThrowsAsync<ArgumentException>(() => ProposeAsync(host, new NhAssistantProposalRequest(Person(Proposer), AgentId, ChangeStatusTool, "OnHold", Subject(projectId))));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Guards_duplicates_and_the_rejection_cooldown_apply_as_for_a_run(AssistantTestProvider provider)
    {
        Guid[] projects = [Guid.NewGuid(), Guid.NewGuid()];
        await using var host = await CreateHostAsync(provider, guard: true);
        var states = host.Services.GetRequiredService<TestProjectStates>();

        // A guard that checks at proposal time finds the work done: nothing is proposed.
        states.Statuses[projects[0]] = "Active";
        var done = await ProposeAsync(host, Request(Proposer, projects[0], "Active"));
        Assert.True(done.Success);
        Assert.Equal((NhAssistantProposeOutcome.NotNeeded, (Guid?)null, ProjectStatusGuard.ReasonCode, NhAiStaleKind.AlreadyDone),
            (done.Data!.Outcome, done.Data.ApprovalId, done.Data.NotNeededReason, done.Data.NotNeededKind));
        Assert.Contains("Already set to Active by Henk.", done.Data.Explanations.Values);

        // The same proposal open: nothing new. A newer one for the same project replaces the pending one.
        var first = (await ProposeAsync(host, Request(Proposer, projects[1], "OnHold"))).Data!;
        var again = (await ProposeAsync(host, Request(Proposer, projects[1], "OnHold"))).Data!;
        Assert.Equal((NhAssistantProposeOutcome.AlreadyOpen, first.ApprovalId), (again.Outcome, again.ApprovalId));
        var newer = (await ProposeAsync(host, Request(Proposer, projects[1], "Active"))).Data!;
        Assert.Equal(NhAssistantProposeOutcome.Created, newer.Outcome);
        Assert.Equal([first.ApprovalId!.Value], newer.Superseded);
        Assert.Equal(NhAssistantApprovalStatuses.Withdrawn, (await FindAsync(host, first.ApprovalId.Value)).Status);

        // People rejected it under a policy with a cooldown: it is not proposed again within it.
        var underCooldown = (await ProposeAsync(host, Request(Proposer, projects[1], "Closed") with { PolicyId = CooldownPolicy })).Data!;
        Assert.True((await DecideAsync(host, await FindAsync(host, underCooldown.ApprovalId!.Value), SecondApprover, approve: false)).Success);
        var rejected = (await ProposeAsync(host, Request(Proposer, projects[1], "Closed") with { PolicyId = CooldownPolicy })).Data!;
        Assert.Equal((NhAssistantProposeOutcome.RecentlyRejected, underCooldown.ApprovalId), (rejected.Outcome, rejected.ApprovalId));
        Assert.NotNull(rejected.RejectedAt);
        Assert.Equal(2, host.Business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalDeduplicated && evt.ActorId == Proposer));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Two_calls_with_the_same_key_record_one_proposal(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        await using var host = await CreateHostAsync(provider);
        var request = Request(Proposer, projectId, "OnHold") with { IdempotencyKey = "submission-7" };

        var results = await Task.WhenAll(ProposeAsync(host, request), ProposeAsync(host, request));

        Assert.All(results, result => Assert.True(result.Success, CodeOf(result)));
        Assert.Single(results.Select(result => result.Data!.ApprovalId).Distinct());
        Assert.Single(results, result => !result.Data!.Repeated);
        Assert.Single(await ApprovalsAsync(host));
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal(1, await context.ToolInvocations.CountAsync(item => item.ToolId == ChangeStatusTool));
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalRecordedForPerson);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_proposal_recorded_while_agent_work_is_off_is_held_after_approval_and_executes_with_the_persons_rights_once_it_is_on(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var clock = new AssistantTestClock(DateTimeOffset.UtcNow);
        await using var host = await CreateHostAsync(provider, clock: clock);
        var work = host.Services.GetRequiredService<AgentWorkSwitch>();
        var operations = host.Services.GetRequiredService<RecordingOperations>();

        // Recording the proposal is the application acting for the person, as in their chat: agent work
        // being off does not refuse it.
        work.AgentWork = false;
        var proposed = await ProposeAsync(host, Request(Proposer, projectId, "OnHold"));
        Assert.True(proposed.Success, CodeOf(proposed));
        Assert.Equal(
            (NhAssistantProposeOutcome.Created, NhAssistantApprovalStatuses.Pending),
            (proposed.Data!.Outcome, proposed.Data.Status));
        var approval = await FindAsync(host, proposed.Data.ApprovalId!.Value);
        Assert.True(NhAssistantPersonProposals.IsPersonProposal(approval));
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalRecordedForPerson);

        // Its execution is agent work: people decide, the approval queues nothing, and an execution that
        // runs anyway holds the proposal, as for any approved proposal of an agent's thread. The
        // application's clock moves past the proposal's contract, which was generated on the system clock.
        clock.Advance(TimeSpan.FromMinutes(1));
        var decided = await DecideAsync(host, approval, SecondApprover);
        Assert.True(decided.Success, CodeOf(decided));
        Assert.DoesNotContain(approval.Id, operations.Executions);
        await ExecuteAsync(host, approval.Id);
        var held = await FindAsync(host, approval.Id);
        Assert.Equal((NhAssistantApprovalStatuses.Approved, (Guid?)null), (held.Status, held.ExecutionOperationId));
        Assert.Empty(host.Tools.StatusChanges);

        // The maintenance job queues nothing while agent work is off, and the held proposal once it is on.
        clock.Advance(TimeSpan.FromMinutes(2));
        var maintenance = host.Services.GetServices<IHostedService>().OfType<NhAssistantRunMaintenance>().Single();
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(approval.Id, operations.Executions);

        work.AgentWork = true;
        await maintenance.RunOnceAsync(CancellationToken.None);
        Assert.Contains(approval.Id, operations.Executions);

        // It executes with the person's rights only, once.
        await ExecuteAsync(host, approval.Id);
        var executed = await FindAsync(host, approval.Id);
        Assert.Equal((NhAssistantApprovalStatuses.Executed, (string?)null), (executed.Status, executed.ExecutionResultCode));
        var change = Assert.Single(host.Tools.StatusChanges);
        Assert.Equal((projectId, "OnHold"), (change.ProjectId, change.Status));
        Assert.Equal(NhAiGrantSource.Delegated, host.Tools.Contexts.Last().GrantSource);
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, bool guard = false, AssistantTestClock? clock = null)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            new NewHeap.Platform.AI.Test.NhAiScriptedChatClient(),
            configure: services =>
            {
                // Agent work can be switched off at runtime, as a reloaded configuration does; it is on by default.
                services.AddSingleton<AgentWorkSwitch>();
                services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<NhAssistantOptions>>(provider =>
                    provider.GetRequiredService<AgentWorkSwitch>()));
                if (clock is not null)
                {
                    services.AddSingleton<TimeProvider>(clock);
                }
                var subjects = new TestSubjects();
                subjects.Roles[SecondApprover] = NhAiSubjectRole.Approver;
                services.AddSingleton(subjects);
                services.AddSingleton<TestPolicySelections>();
                services.AddSingleton<TestProjectStates>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                var rights = new PersonRights();
                rights.Managers[Proposer] = true;
                services.AddSingleton(rights);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, PersonRightsPrincipalFactory>());
                // Proposals are made in English, and approvers read them in English or Dutch.
                services.Configure<RequestLocalizationOptions>(options =>
                {
                    CultureInfo[] cultures = [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("nl-NL")];
                    options.DefaultRequestCulture = new RequestCulture(cultures[0]);
                    options.SupportedCultures = cultures;
                    options.SupportedUICultures = cultures;
                });
                // Changing a project status needs the manage permission, which only some people hold.
                services.Configure<AuthorizationOptions>(options => options.AddPolicy(
                    TestProjectToolCatalog.ManagePolicy,
                    policy => policy.RequireClaim("permission", ManagePermission)));
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .AddApprovalPolicy(ReviewPolicy, policy => policy
                        .Lifetime(TimeSpan.FromDays(3))
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .Mode(NhAiApprovalMode.Inline)
                        .BindTo(NhAiApprovalBinding.ToolContract))
                    .AddApprovalPolicy(FourEyesPolicy, policy => policy
                        .Lifetime(TimeSpan.FromDays(3))
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .FourEyes()
                        .BindTo(NhAiApprovalBinding.ToolContract))
                    .AddApprovalPolicy(CooldownPolicy, policy => policy
                        .Lifetime(TimeSpan.FromDays(3))
                        .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                        .RejectionCooldown(TimeSpan.FromDays(7))
                        .BindTo(NhAiApprovalBinding.ToolContract))
                    // The agent's grant allows the tool in its runs; a proposal for a person never uses it.
                    .ConfigureAgent(AgentId, agent => agent
                        .Grant("projects.*")
                        .UseApprovalPolicy(ReviewPolicy))
                    .UseApprovalPolicySelector<TestPolicySelector>();
                if (guard)
                {
                    assistant.AddProposalGuard<ProjectStatusGuard>(
                        [ChangeStatusTool],
                        NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal);
                }
            });
    }

    private static NhAssistantProposalRequest Request(string actorId, Guid projectId, string status)
    {
        return new NhAssistantProposalRequest(
            Person(actorId),
            AgentId,
            ChangeStatusTool,
            new ProjectStatusInput(projectId, status),
            Subject(projectId));
    }

    private static NhAiSubjectRef Subject(Guid projectId)
    {
        return new NhAiSubjectRef("project", projectId.ToString());
    }

    private static (string? DisplayName, string? Summary) TextOf(NhAssistantApprovalPresentation? presentation)
    {
        return (presentation?.ToolDisplayName, presentation?.Summary);
    }

    private static NhAssistantApprovalPresentation Presentation(string displayName, string summary)
    {
        return new NhAssistantApprovalPresentation(displayName, summary, [new NhAssistantPresentationField("Status", "OnHold")]);
    }

    /// <summary>
    /// The person as the application authenticated them: their own sign-in's claims.
    /// </summary>
    private static NhAssistantDecidingPerson Person(string actorId)
    {
        return new NhAssistantDecidingPerson(Principal(actorId), actorId, null);
    }

    private static ClaimsPrincipal Principal(string actorId)
    {
        // Approvers of a project change hold the right to make it themselves, as the tool's approve policy asks.
        return PersonRightsPrincipalFactory.PrincipalOf(actorId, actorId is Proposer or SecondApprover);
    }

    private static async Task<TaskResult<NhAssistantProposalResult>> ProposeAsync(AssistantTestHost host, NhAssistantProposalRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantProposals>().ProposeForAsync(request, CancellationToken.None);
    }

    private static async Task<TaskResult<AssistantApproval>> DecideAsync(AssistantTestHost host, AssistantApproval approval, string actorId, bool approve = true)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantProposalDecisions>().DecideAsync(
            approval.Id,
            actorId,
            Principal(actorId),
            approve,
            approval.ProposalHash,
            reason: null,
            CancellationToken.None);
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = null;
        var result = await scope.ServiceProvider.GetRequiredService<NhAssistantProposalExecution>().ExecuteAsync(
            approvalId,
            Substitute.For<INhBackgroundOperationContext>(),
            fenced: false,
            CancellationToken.None);
        Assert.True(result.Success);
    }

    private static async Task<AssistantApproval> FindAsync(AssistantTestHost host, Guid approvalId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, CancellationToken.None))!;
    }

    private static async Task<AssistantToolInvocation> CallAsync(AssistantTestHost host, Guid invocationId)
    {
        return (await host.Services.GetRequiredService<INhAssistantStore>().FindToolInvocationAsync(invocationId, CancellationToken.None))!;
    }

    private static async Task<List<AssistantApproval>> ApprovalsAsync(AssistantTestHost host)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Approvals.AsNoTracking().ToListAsync();
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.Success
            ? null
            : result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static string[] FieldErrors(TaskResult result, string field)
    {
        return result.GetResultItems()
            .Where(item => item.Name == NhAssistantValidationErrors.FieldPrefix + field)
            .SelectMany(item => item.ErrorMessages.Select(message => message.ToString()))
            .ToArray();
    }
}

/// <summary>
/// Who holds the manage permission for projects, as the user store would rebuild it.
/// </summary>
public sealed class PersonRights
{
    public ConcurrentDictionary<string, bool> Managers { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Rebuilds a person's principal with the manage permission while <see cref="PersonRights"/> grants it.
/// </summary>
public sealed class PersonRightsPrincipalFactory(PersonRights rights) : INhAiPrincipalFactory
{
    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(PrincipalOf(actorId, rights.Managers.ContainsKey(actorId)));
    }

    public static ClaimsPrincipal PrincipalOf(string actorId, bool manager)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorId) };
        if (manager)
        {
            claims.Add(new Claim("permission", "projects.manage"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}

/// <summary>
/// The presentation an approver in a culture reads, as the proposal reads show it.
/// </summary>
internal static class NhAssistantTestPresentations
{
    public static NhAssistantApprovalPresentation? Pick(AssistantApproval approval, string culture)
    {
        return Runtime.NhAssistantToolPresentationResolver.Pick(
            approval.PresentationJson,
            approval.PresentationsJson,
            System.Globalization.CultureInfo.GetCultureInfo(culture));
    }
}

/// <summary>
/// The result codes of the assistant's tool calls that the tests compare with.
/// </summary>
internal static class NhAssistantToolCallInterceptorCodes
{
    public const string ProposalRecorded = Runtime.NhAssistantToolCallInterceptor.ProposalRecordedCode;
}
