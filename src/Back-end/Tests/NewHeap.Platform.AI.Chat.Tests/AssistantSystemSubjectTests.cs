using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The whole application as a scope (W3 of the plan for agents working on application objects): the
/// platform's <c>nh-system</c> subject, its roles, schedules without subjects app-wide and per division,
/// the division directory, and the startup checks around them, on both relational providers.
/// </summary>
public sealed class AssistantSystemSubjectTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AppWide = "weekly-statistics";
    private const string PerDivisionAgent = "division-statistics";
    private const string FirstDivisionOnly = "first-division-statistics";
    private const string ObjectsOnly = "project-assistant";
    private const string WeeklyTick = "weekly-statistics-tick";
    private const string DivisionTick = "division-statistics-tick";
    private const string FirstDivisionTick = "first-division-statistics-tick";
    private const string ManagePolicy = "app.project.manage";
    private const string SystemReadPolicy = "app.system.read";
    private const string Admin = "admin-1";
    private const string Reader = "reader-1";
    private const string Stranger = "stranger-1";
    private const string Viewer = "viewer-1";
    private const string Follower = "work-follower";
    private const string Narrow = "task-follower";

    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_schedule_without_subjects_runs_once_per_tick_in_the_agents_thread_of_the_whole_application(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, divisions: false);

        var receipt = Assert.Single(await TickAsync(host, WeeklyTick, Monday));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        var thread = (await Runs(host).FindThreadAsync("global", AppWide, NhAiSystemSubjects.Global, CancellationToken.None))!;
        Assert.Equal(receipt.ConversationId, thread.ConversationId);
        Assert.Equal((NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, "The whole application", (Guid?)null),
            (thread.SubjectType, thread.SubjectId, thread.SubjectDisplayName, thread.DivisionId));
        var operation = Assert.Single(host.Services.GetRequiredService<RecordingOperations>().Enqueued);
        Assert.Equal((NhAiSubjectRef.SystemType, NhAiSystemSubjects.GlobalId, (Guid?)null),
            (operation.Options.DomainObjectType, operation.Options.DomainObjectId, operation.Options.DivisionId));

        // A repeated job in the same minute finds the run it started; the thread stays the one of the agent.
        var repeated = Assert.Single(await TickAsync(host, WeeklyTick, Monday.AddSeconds(20)));
        Assert.Equal((NhAssistantTriggerOutcomes.Deduplicated, thread.ConversationId), (repeated.Outcome, repeated.ConversationId));

        // An app-wide agent works on the whole application app-wide, not in a division, and an agent
        // that does not work on the application never gets its thread.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
            var inDivision = await triggers.StartAsync(AppWide, NhAiSystemSubjects.ForDivision(Guid.NewGuid()), Admin, "Count the projects.");
            Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (inDivision.Outcome, inDivision.ReasonCode));
            var objectsOnly = await triggers.StartAsync(ObjectsOnly, NhAiSystemSubjects.Global, Admin, "Count the projects.");
            Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (objectsOnly.Outcome, objectsOnly.ReasonCode));
        }
        Assert.Null(await Runs(host).FindThreadAsync("global", ObjectsOnly, NhAiSystemSubjects.Global, CancellationToken.None));

        // Without a division directory the whole application exists app-wide only.
        Assert.False(host.Services.GetRequiredService<NhAssistantWorksOnRule>().DivisionsListed);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var subjects = scope.ServiceProvider.GetRequiredService<NhAssistantSubjectAccessService>();
            Assert.Null(await subjects.DescribeAsync(NhAiSystemSubjects.ForDivision(North), CancellationToken.None));
            var type = subjects.FindType(NhAiSubjectRef.SystemType)!;
            Assert.True(type.IsSystem);
            Assert.Equal("De hele applicatie", type.NameIn(CultureInfo.GetCultureInfo("nl-NL")));
            Assert.Equal("The whole application", type.NameIn(CultureInfo.GetCultureInfo("fr-FR")));

            // The run configuration names what the schedule in code runs for and how the agent runs,
            // for the administration's flow view.
            var configured = (await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().GetAsync(AppWide))!;
            var schedule = Assert.Single(configured.CodeTriggers);
            Assert.Equal((WeeklyTick, NhAssistantDataTriggerSources.Schedule, NhAiSubjectRef.SystemType), (schedule.TriggerId, schedule.Source, schedule.SubjectType));
            Assert.Equal(
                new NhAssistantCodeRunSettingsDto(NhAssistantAccountabilities.Agent, false, null, false)
                {
                    RetryCount = 3,
                    MaxConcurrentRuns = 4,
                    MaxRunsPerSubjectPerDay = 10
                },
                configured.CodeRunSettings);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_per_division_agent_runs_once_per_listed_division_in_a_thread_per_division(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, divisions: true);
        Assert.True(host.Services.GetRequiredService<NhAssistantWorksOnRule>().DivisionsListed);

        var receipts = await TickAsync(host, DivisionTick, Monday);
        Assert.Equal([NhAssistantTriggerOutcomes.Started, NhAssistantTriggerOutcomes.Started], receipts.Select(receipt => receipt.Outcome));
        foreach (var (divisionId, name) in new[] { (North, "North"), (South, "South") })
        {
            var subject = NhAiSystemSubjects.ForDivision(divisionId);
            var thread = (await Runs(host).FindThreadAsync(NhAiSubjectInfo.CreateScopeKey(null, divisionId), PerDivisionAgent, subject, CancellationToken.None))!;
            Assert.Equal((name, (Guid?)divisionId, $"division:{divisionId:N}"), (thread.SubjectDisplayName, thread.DivisionId, thread.ScopeKey));
        }
        // The runs work in their division, as the background operations record.
        Assert.Equal(
            [North, South],
            host.Services.GetRequiredService<RecordingOperations>().Enqueued.Select(item => item.Options.DivisionId!.Value).Order());

        // A tick takes at most its limit of divisions, the first by name.
        var limited = Assert.Single(await TickAsync(host, FirstDivisionTick, Monday));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, limited.Outcome);
        Assert.NotNull(await Runs(host).FindThreadAsync(NhAiSubjectInfo.CreateScopeKey(null, North), FirstDivisionOnly, NhAiSystemSubjects.ForDivision(North), CancellationToken.None));
        Assert.Contains(host.Logs.Messages, message => message.Contains(FirstDivisionTick, StringComparison.Ordinal) && message.Contains("limit", StringComparison.Ordinal));

        // A per-division agent never works on the whole application app-wide.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
            var global = await triggers.StartAsync(PerDivisionAgent, NhAiSystemSubjects.Global, Admin, "Count the projects.");
            Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (global.Outcome, global.ReasonCode));
        }

        // An unknown division and an id that is not the canonical one describe nothing.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var subjects = scope.ServiceProvider.GetRequiredService<NhAssistantSubjectAccessService>();
            Assert.Null(await subjects.DescribeAsync(NhAiSystemSubjects.ForDivision(Guid.NewGuid()), CancellationToken.None));
            Assert.Null(await subjects.DescribeAsync(new NhAiSubjectRef(NhAiSubjectRef.SystemType, "division-" + North.ToString("N").ToUpperInvariant()), CancellationToken.None));
            Assert.Null(await subjects.DescribeAsync(new NhAiSubjectRef(NhAiSubjectRef.SystemType, "everything"), CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_read_the_whole_application_with_the_access_policy_and_decide_with_the_system_policy(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, divisions: true);
        await using var scope = host.Services.CreateAsyncScope();
        var subjects = scope.ServiceProvider.GetRequiredService<NhAssistantSubjectAccessService>();

        // This host reads with its own read policy instead of the access policy, and approves with the
        // project manage policy instead of the administration policy.
        Assert.Equal(NhAiSubjectRole.Approver, (await subjects.ResolveAsync(NhAiSystemSubjects.Global, Admin, CancellationToken.None)).Role);
        Assert.Equal(NhAiSubjectRole.Reader, (await subjects.ResolveAsync(NhAiSystemSubjects.Global, Reader, CancellationToken.None)).Role);
        // Signed in, which the access policy of this host asks, but without the read policy.
        Assert.Equal(NhAiSubjectRole.None, (await subjects.ResolveAsync(NhAiSystemSubjects.Global, Viewer, CancellationToken.None)).Role);
        // Without a principal, as for an account that may no longer sign in or an agent, there is no role.
        Assert.Equal(NhAiSubjectRole.None, (await subjects.ResolveAsync(NhAiSystemSubjects.Global, Stranger, CancellationToken.None)).Role);
        Assert.Equal(NhAiSubjectRole.None, (await subjects.ResolveAsync(NhAiSystemSubjects.Global, "agent:" + AppWide, CancellationToken.None)).Role);

        // In a division the person needs access to the division as well.
        var north = NhAiSystemSubjects.ForDivision(North);
        var south = NhAiSystemSubjects.ForDivision(South);
        Assert.Equal(NhAiSubjectRole.Approver, (await subjects.ResolveAsync(north, Admin, CancellationToken.None)).Role);
        Assert.Equal(NhAiSubjectRole.Reader, (await subjects.ResolveAsync(north, Reader, CancellationToken.None)).Role);
        Assert.Equal(NhAiSubjectRole.None, (await subjects.ResolveAsync(south, Reader, CancellationToken.None)).Role);
        Assert.Equal(NhAiSubjectRole.None, (await subjects.ResolveAsync(NhAiSystemSubjects.ForDivision(Guid.NewGuid()), Admin, CancellationToken.None)).Role);

        // The policies were evaluated against the subject with its division, never against the request.
        var principals = host.Services.GetRequiredService<SystemSubjectPrincipals>();
        Assert.Contains(principals.Requests, request => request.ActorId == Reader && request.Subject.DivisionId == North);

        // The whole application lists no members; listings that follow the snapshot stay empty.
        var resolver = scope.ServiceProvider.GetServices<INhAiSubjectAccessResolver>().Single(item => item.SubjectType == NhAiSubjectRef.SystemType);
        await foreach (var _ in resolver.ListMembersAsync(NhAiSystemSubjects.Global))
        {
            Assert.Fail("The whole application lists no members.");
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_schedule_without_a_subject_set_runs_for_the_whole_application_of_an_agent_that_works_on_it(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, divisions: true);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
            Assert.True((await administration.CreateAsync(Input(Follower, Works(types: ["project"], application: true)), Admin, CancellationToken.None)).Success);
            Assert.True((await administration.CreateAsync(Input(Narrow, Works(types: ["project"])), Admin, CancellationToken.None)).Success);
        }
        foreach (var agentId in new[] { Follower, Narrow })
        {
            Assert.True((await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
                agentId,
                new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.ServiceAccount, SystemSubjectPrincipals.ServiceAccount),
                Admin))).Success);
        }

        // An agent that does not work on the application needs a subject set.
        var refused = await SaveScheduleAsync(host, Narrow, "narrow-weekly");
        Assert.Equal(("subjectSet", NhAssistantFieldErrors.Required), FieldOf(refused));

        // One that does gets a weekly run for the whole application, live once an administrator says so.
        var saved = await SaveScheduleAsync(host, Follower, "follower-weekly");
        Assert.True(saved.Success);
        Assert.Null(saved.Data!.SubjectSet);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(Follower, "follower-weekly", false, Admin))).Success);
        var receipt = Assert.Single(await TickAsync(host, "follower-weekly", Monday));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, receipt.Outcome);
        Assert.NotNull(await Runs(host).FindThreadAsync("global", Follower, NhAiSystemSubjects.Global, CancellationToken.None));

        // With the division directory an agent created in the administration may work per division,
        // and its schedule then runs once per division.
        Assert.True((await UpdateAsync(host, Follower, Works(types: ["project"], application: true, perDivision: true), expectedVersion: 1)).Success);
        var perDivision = await TickAsync(host, "follower-weekly", Monday.AddDays(7));
        Assert.Equal(2, perDivision.Count);
        Assert.All(perDivision, item => Assert.Equal(NhAssistantTriggerOutcomes.Started, item.Outcome));

        // Narrowed to projects only, the schedule stays stored but is out of effect.
        Assert.True((await UpdateAsync(host, Follower, Works(types: ["project"]), expectedVersion: 2)).Success);
        var configured = (await ConfigureAsync(host, configuration => configuration.GetAsync(Follower)))!;
        var problem = Assert.Single(configured.Problems);
        Assert.Equal((NhAssistantRunConfigurationProblems.TriggerKind, "follower-weekly", NhAssistantRunConfigurationProblems.NotWorkedOn), (problem.Kind, problem.Id, problem.Code));
        Assert.Empty(await TickAsync(host, "follower-weekly", Monday.AddDays(14)));
    }

    [Fact]
    public async Task The_catalog_lists_the_whole_application_and_the_divisions_and_people_see_its_threads_by_their_roles()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<SystemSubjectPrincipals>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(provider => provider.GetRequiredService<SystemSubjectPrincipals>()));
                services.AddNewHeapDivisionDirectory<TestDivisionDirectory>();
                // This host runs no agents; the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>(type => type.Named(new() { ["en"] = "Project", ["nl"] = "Project" }, fallbackLanguage: "en"))
                .ConfigureAgent(AssistantTestData.Agent().Id, agent => agent.WorksOn("project").WorksOnApplication()));
        using var admin = app.CreateClient(Admin, admin: true);
        using var user = app.CreateClient(Reader);

        using var dutch = new HttpRequestMessage(HttpMethod.Get, "/api/assistant/admin/run-catalog");
        dutch.Headers.AcceptLanguage.ParseAdd("nl-NL");
        var catalog = await ReadJsonAsync(await admin.SendAsync(dutch));
        Assert.True(catalog.GetProperty("divisions").GetBoolean());
        var types = catalog.GetProperty("subjectTypes").EnumerateArray().ToArray();
        Assert.Equal(["nh-system", "project"], types.Select(type => type.GetProperty("id").GetString()));
        // With listed divisions the whole application is searched for its divisions (W4).
        Assert.Equal(("De hele applicatie", true, true, JsonValueKind.Null),
            (types[0].GetProperty("name").GetString(), types[0].GetProperty("isSystem").GetBoolean(), types[0].GetProperty("searchable").GetBoolean(), types[0].GetProperty("link").ValueKind));

        // The default system policy is the administration policy: administrators may start the agent
        // for the whole application, readers see that they cannot, outsiders do not see it at all.
        var lookup = $"/api/assistant/threads?agentId={AssistantTestData.Agent().Id}&subjectType=nh-system&subjectId=global";
        var asAdmin = await ReadJsonAsync(await admin.GetAsync(lookup));
        Assert.Equal(("approver", true), (asAdmin.GetProperty("role").GetString(), asAdmin.GetProperty("canStart").GetBoolean()));
        var asReader = await ReadJsonAsync(await user.GetAsync(lookup));
        Assert.Equal(("reader", false), (asReader.GetProperty("role").GetString(), asReader.GetProperty("canStart").GetBoolean()));
        using var outsider = app.CreateClient(Stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(lookup)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(lookup.Replace("global", "division-" + Guid.NewGuid().ToString("N"), StringComparison.Ordinal))).StatusCode);
    }

    [Fact]
    public async Task Startup_lets_the_platforms_own_type_through_and_refuses_what_does_not_fit()
    {
        // The platform's own resolver passes the reserved prefix; an application's nh- type does not.
        var reserved = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            assistant => assistant.AddSubjectType<ApplicationSystemResolver>()));
        Assert.Contains("reserved", reserved.Message);
        Assert.Contains(nameof(ApplicationSystemResolver), reserved.Message);

        // Per division needs listed divisions.
        var perDivision = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            assistant => assistant.ConfigureAgent(AssistantTestData.Agent().Id, run => run.WorksOnApplication().PerDivision())));
        Assert.Contains("lists no divisions", perDivision.Message);
        // With a division directory the works-on checks pass and startup goes on to the background operations.
        var listed = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            assistant => assistant.ConfigureAgent(AssistantTestData.Agent().Id, run => run.WorksOnApplication().PerDivision()),
            services => services.AddNewHeapDivisionDirectory<TestDivisionDirectory>()));
        Assert.Contains("background operations", listed.Message);

        // A schedule without subjects needs an agent that works on the application.
        var schedule = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(AssistantTestData.Agent().Id, run => run.WorksOn("project"))
            .AddScheduledTrigger("weekly", trigger => trigger
                .StartAgent(AssistantTestData.Agent().Id)
                .Cron("0 7 * * 1")
                .Message(_ => "Count the projects."))));
        Assert.Contains("whole application", schedule.Message);
        Assert.Contains("WorksOnApplication", schedule.Message);
        Validate(assistant => assistant
            .ConfigureAgent(AssistantTestData.Agent().Id, run => run.WorksOn("project").WorksOnApplication())
            .AddScheduledTrigger("weekly", trigger => trigger
                .StartAgent(AssistantTestData.Agent().Id)
                .Cron("0 7 * * 1")
                .Message(_ => "Count the projects.")));

        // The policies of the whole application must be registered.
        var unknownPolicy = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            assistant => assistant.UseSystemSubject(system => system.ApprovePolicy("app.unknown"))));
        Assert.Contains("approve policy 'app.unknown'", unknownPolicy.Message);
        var unknownRead = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            assistant => assistant.UseSystemSubject(system => system.ReadPolicy("app.unknown-read"))));
        Assert.Contains("read policy 'app.unknown-read'", unknownRead.Message);
        Assert.Throws<ArgumentException>(() => new NhAssistantSystemSubjectBuilder(new NhAssistantRegistrationState(), new ServiceCollection()).ApprovePolicy(" app.padded"));
    }

    [Fact]
    public void System_subjects_have_one_canonical_id_per_scope()
    {
        Assert.Equal("nh-system/global", NhAiSystemSubjects.Global.ToString());
        var north = NhAiSystemSubjects.ForDivision(North);
        Assert.Equal("division-" + North.ToString("N"), north.Id);
        Assert.True(NhAiSystemSubjects.TryGetDivision(north, out var divisionId));
        Assert.Equal(North, divisionId);
        Assert.False(NhAiSystemSubjects.TryGetDivision(NhAiSystemSubjects.Global, out _));
        Assert.False(NhAiSystemSubjects.TryGetDivision(new NhAiSubjectRef(NhAiSubjectRef.SystemType, "division-" + North.ToString("D")), out _));
        Assert.False(NhAiSystemSubjects.TryGetDivision(new NhAiSubjectRef("project", north.Id), out _));
        Assert.False(NhAiSystemSubjects.IsGlobal(new NhAiSubjectRef("project", NhAiSystemSubjects.GlobalId)));

        var appWide = NhAssistantAgentWorksOn.Undeclared with { Declared = true, AllSubjects = false, Application = true };
        Assert.True(appWide.Covers(NhAiSystemSubjects.Global));
        Assert.False(appWide.Covers(north));
        var perDivision = appWide with { PerDivision = true };
        Assert.True(perDivision.Covers(north));
        Assert.False(perDivision.Covers(NhAiSystemSubjects.Global));
        Assert.False(NhAssistantAgentWorksOn.Undeclared.Covers(NhAiSystemSubjects.Global));
        Assert.True(NhAssistantAgentWorksOn.Undeclared.Covers(new NhAiSubjectRef("project", "1")));
    }

    internal static readonly Guid North = Guid.Parse("a1000000-0000-0000-0000-0000000000a1");
    internal static readonly Guid South = Guid.Parse("b2000000-0000-0000-0000-0000000000b2");

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, bool divisions)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.AddSingleton<SystemSubjectPrincipals>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory>(provider => provider.GetRequiredService<SystemSubjectPrincipals>()));
                services.AddNewHeapDivisionAccess<TestDivisionAccess>();
                services.AddAuthorization(options => options.AddPolicy(SystemReadPolicy, policy => policy.RequireClaim("permission", "assistant")));
                if (divisions)
                {
                    services.AddNewHeapDivisionDirectory<TestDivisionDirectory>();
                }
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<TestSubjectResolver>()
                    .UseSystemSubject(system => system.ReadPolicy(SystemReadPolicy).ApprovePolicy(ManagePolicy))
                    .AddAgent(AssistantTestData.Agent(AppWide))
                    .ConfigureAgent(ObjectsOnly, run => run.WorksOn("project"))
                    .ConfigureAgent(AppWide, run => run.WorksOn("project").WorksOnApplication())
                    .AddScheduledTrigger(WeeklyTick, trigger => trigger
                        .StartAgent(AppWide)
                        .Cron("0 7 * * 1")
                        .Message(_ => "Count the open projects."));
                if (divisions)
                {
                    assistant
                        .AddAgent(AssistantTestData.Agent(PerDivisionAgent))
                        .AddAgent(AssistantTestData.Agent(FirstDivisionOnly))
                        .ConfigureAgent(PerDivisionAgent, run => run.WorksOnApplication().PerDivision())
                        .ConfigureAgent(FirstDivisionOnly, run => run.WorksOnApplication().PerDivision())
                        .AddScheduledTrigger(DivisionTick, trigger => trigger
                            .StartAgent(PerDivisionAgent)
                            .Cron("0 7 * * 1")
                            .Message(tick => "Count the open projects of " + tick.Subject.Id + "."))
                        .AddScheduledTrigger(FirstDivisionTick, trigger => trigger
                            .StartAgent(FirstDivisionOnly)
                            .Cron("0 7 * * 1")
                            .MaxSubjectsPerTick(1)
                            .Message(_ => "Count the open projects."));
                }
            });
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> TickAsync(AssistantTestHost host, string triggerId, DateTimeOffset now)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantScheduledTriggerJob>().RunAsync(triggerId, now, CancellationToken.None);
    }

    private static Task<TaskResult<NhAssistantDataTriggerDto>> SaveScheduleAsync(AssistantTestHost host, string agentId, string triggerId)
    {
        return ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(
            agentId,
            triggerId,
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Schedule, Cron: "0 7 * * 1", Message: "Count the open projects."),
            Admin));
    }

    private static async Task<TaskResult<NhAssistantAgent>> UpdateAsync(AssistantTestHost host, string agentId, NhAssistantAgentWorksOn worksOn, int expectedVersion)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>()
            .UpdateAsync(Input(agentId, worksOn), expectedVersion, Admin, CancellationToken.None);
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static NhAssistantAgentInput Input(string agentId, NhAssistantAgentWorksOn worksOn)
    {
        return new NhAssistantAgentInput(
            agentId,
            "Follower",
            "Follows up the projects.",
            "Follow up the projects.",
            ["projects.*"],
            [],
            null,
            NhAiAutonomyLevel.Propose,
            WorksOn: worksOn);
    }

    private static NhAssistantAgentWorksOn Works(
        IReadOnlyList<string>? types = null,
        bool? application = null,
        bool? perDivision = null)
    {
        return NhAssistantAgentWorksOn.FromInput(types, null, application, perDivision, null, null)!;
    }

    private static (string Field, string Code) FieldOf(TaskResult result)
    {
        Assert.False(result.Success);
        var item = result.GetResultItems().Single(entry => entry.Name.StartsWith("field:", StringComparison.Ordinal));
        return (item.Name["field:".Length..], item.ErrorMessages.Single().ToString(CultureInfo.InvariantCulture));
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Registers the assistant with the configuration and checks the agent runs as startup does.
    /// </summary>
    private static void Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_system_subject", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" });
            configure(assistant);
        });
        services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>().ValidateAgentRuns();
    }

    private static async Task StartAsync(Action<NhAssistantBuilder> configure, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NhAssistantOptions.DefaultAccessPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(NhAssistantOptions.DefaultAdminPolicy, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton(new TestSubjects());
        builder.Services.AddSingleton<TestProjectToolRecorder>();
        builder.Services.AddKeyedSingleton<IChatClient>("project-chat-model", new NhAiScriptedChatClient());
        builder.Services.AddNewHeapPlatformAI(ai =>
        {
            ai.AddChatProfile("project-chat", profile => profile
                .UseKeyedClient("project-chat-model")
                .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local"));
            ai.UseBudgetManager<NhAiTestBudgetManager>();
        });
        builder.Services.AddNewHeapPlatformAIAspNet(_ => { });
        builder.Services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_system_subject", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent() with { ProfileName = "project-chat" })
                .AddSubjectType<TestSubjectResolver>();
            configure(assistant);
        });
        services?.Invoke(builder.Services);
        await using var app = builder.Build();
        await app.StartAsync();
        await app.StopAsync();
    }
}

/// <summary>
/// The people of the system subject tests, rebuilt as a headless run would: an administrator who
/// manages projects, a reader who uses the assistant, a viewer who only signs in, a technical
/// account, and nobody else.
/// </summary>
public sealed class SystemSubjectPrincipals : INhAiPrincipalFactory
{
    public const string ServiceAccount = "service-1";

    public System.Collections.Concurrent.ConcurrentQueue<(string ActorId, NhAiSubjectInfo Subject)> Requests { get; } = new();

    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue((actorId, subject));
        var claims = actorId switch
        {
            "admin-1" => new[] { "assistant", "assistant-admin", "app.project.manage" },
            "reader-1" or ServiceAccount => new[] { "assistant" },
            "viewer-1" => Array.Empty<string>(),
            _ => null
        };
        return ValueTask.FromResult(claims is null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity(
                claims.Select(value => new Claim("permission", value)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                "test")));
    }
}

/// <summary>
/// Two divisions, North and South, listed by name.
/// </summary>
public sealed class TestDivisionDirectory : INhDivisionDirectory
{
    private static readonly NhDivisionInfo[] Divisions =
    [
        new(AssistantSystemSubjectTests.South, "South"),
        new(AssistantSystemSubjectTests.North, "North")
    ];

    public Task<IReadOnlyList<NhDivisionInfo>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<NhDivisionInfo>>(Divisions.OrderBy(division => division.Name, StringComparer.Ordinal).Take(limit).ToArray());
    }

    public Task<IReadOnlyDictionary<Guid, NhDivisionInfo>> GetAsync(IReadOnlyCollection<Guid> divisionIds, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyDictionary<Guid, NhDivisionInfo>>(Divisions
            .Where(division => divisionIds.Contains(division.Id))
            .ToDictionary(division => division.Id));
    }
}

/// <summary>
/// The administrator works in every division, the reader in North only.
/// </summary>
public sealed class TestDivisionAccess : INhDivisionAccess
{
    public ValueTask<bool> HasAccessAsync(NhActorRef actor, Guid divisionId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(actor.Id switch
        {
            "admin-1" => true,
            "reader-1" => divisionId == AssistantSystemSubjectTests.North,
            _ => false
        });
    }
}

/// <summary>
/// An application resolver that tries to take the platform's reserved type.
/// </summary>
public sealed class ApplicationSystemResolver : INhAiSubjectAccessResolver
{
    public string SubjectType => NhAiSubjectRef.SystemType;

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<NhAiSubjectInfo?>(null);
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
