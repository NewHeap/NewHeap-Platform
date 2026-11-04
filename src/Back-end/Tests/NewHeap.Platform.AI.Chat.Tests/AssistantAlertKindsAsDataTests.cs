using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Alert kinds of agents created in the administration (FB93, lane AL): the run settings list the kinds
/// the agent may raise, out of every kind registered in code; its rules and rule scripts raise those and
/// are refused any other; saving refuses a kind the code does not register as a field error; a listed kind
/// the code later removes is a problem of the run configuration while the settings stay in effect; agent
/// packages carry the list. On both relational providers.
/// </summary>
public sealed class AssistantAlertKindsAsDataTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "task-watch";
    private const string Listed = "task-due-today";
    private const string Unlisted = "task-unlisted";
    private const string Templated = "task-at-risk";
    private const string Gone = "task-gone";
    private const string RuleHandler = "task-alert-rules";
    private const string Administrator = "admin-1";
    private const string OtherAdministrator = "admin-2";
    private const string ScopeKey = "division:11111111111111111111111111111111";
    private const string PackageId = "task-watch-package";
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_administration_agent_raises_the_kinds_its_run_settings_list_and_is_refused_any_other(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await CreateAgentAsync(host);
        var rules = host.Services.GetRequiredService<AlertRuleScript>();
        var task = TaskSubject(Guid.NewGuid());

        // The run catalog lists every kind the code registers, which the run settings may list.
        var catalog = await ConfigureAsync(host, configuration => Task.FromResult(configuration.GetCatalog()));
        Assert.Equal(
            [
                new NhAssistantRunAlertKindDto(Templated, "tests.alerts.task-at-risk", "urgent", true),
                new NhAssistantRunAlertKindDto(Listed, "tests.alerts.task-due-today", "warning", false),
                new NhAssistantRunAlertKindDto(Unlisted, "tests.alerts.task-unlisted", "info", false)
            ],
            catalog.AlertKinds);

        // A malformed id, more kinds than the bound and a kind the code does not register are field errors.
        Assert.Equal(("alertKinds", NhAssistantFieldErrors.Invalid), FieldError(await SaveAsync(host, Settings(["Not a kind"]))));
        Assert.Equal(
            ("alertKinds", NhAssistantFieldErrors.Invalid),
            FieldError(await SaveAsync(host, Settings(Enumerable.Range(1, NhAssistantAgentConfigurationLimits.MaxAlertKinds + 1).Select(index => $"kind-{index}").ToArray()))));
        Assert.Equal(("alertKinds", NhAssistantFieldErrors.NotFound), FieldError(await SaveAsync(host, Settings([Listed, Gone]))));
        Assert.Null(await host.Services.GetRequiredService<NhAssistantRunConfigurationStore>().FindRunSettingsAsync(AgentId, CancellationToken.None));

        // The settings list the kind once; the agent's options in effect list it as Alerts(...) does in code.
        var saved = await SaveAsync(host, Settings([Listed, $" {Listed}"]));
        Assert.True(saved.Success, string.Join(",", saved.GetResultItems().Select(item => item.Name)));
        Assert.Equal([Listed], saved.Data!.AlertKinds);
        Assert.True(saved.Data.InEffect);
        Assert.Equal([Listed], State(host).AgentRuns[AgentId].AlertKinds);
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!;
        Assert.Equal([Listed], view.RunSettings!.AlertKinds);
        Assert.Null(view.CodeRunSettings);

        // Its rules raise the listed kind; the kind in code chooses the recipients.
        rules.Run = (context, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(new NhAssistantAlert(Listed, context.Subject.Subject) { Key = "today", Parameters = new Dictionary<string, string> { ["task"] = "Report" } }));
        var raised = await RunAsync(host, task);
        Assert.Equal(NhAssistantTurnStatuses.Completed, raised.Outcome);
        var alert = (await OpenAsync(host, task, "today", shadow: false))!;
        Assert.Equal((AgentId, NhAssistantAlertUrgency.Warning), (alert.AgentId, alert.Urgency));
        Assert.Equal(["owner-1"], alert.Deliveries.Select(delivery => delivery.RecipientActorId));

        // A kind the settings do not list is refused, and the run raises nothing.
        var other = TaskSubject(Guid.NewGuid());
        rules.Run = (context, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(new NhAssistantAlert(Listed, context.Subject.Subject), new NhAssistantAlert(Unlisted, context.Subject.Subject)));
        var unlisted = await RunAsync(host, other);
        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAssistantAlertCodes.KindNotListed), (unlisted.Outcome, unlisted.ErrorCode));
        Assert.Empty(await AlertsAboutAsync(host, other));

        // In shadow the listed kind is a shadow alert, as for an agent in code.
        var shadowTask = TaskSubject(Guid.NewGuid());
        rules.Run = (context, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(new NhAssistantAlert(Listed, context.Subject.Subject) { Key = "today", Parameters = new Dictionary<string, string> { ["task"] = "Plan" } }));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunAsync(host, shadowTask, shadow: true)).Outcome);
        Assert.Null(await OpenAsync(host, shadowTask, "today", shadow: false));
        Assert.Empty((await OpenAsync(host, shadowTask, "today", shadow: true))!.Deliveries);

        // A save without the list keeps it; an empty list lists none, so the kind is refused again.
        var kept = await SaveAsync(host, Settings(null) with { ExpectedVersion = 1 });
        Assert.Equal(2, kept.Data!.Version);
        Assert.Equal([Listed], kept.Data.AlertKinds);
        var cleared = await SaveAsync(host, Settings([]) with { ExpectedVersion = 2 });
        Assert.Empty(cleared.Data!.AlertKinds);
        Assert.Empty(State(host).AgentRuns[AgentId].AlertKinds);
        rules.Run = (context, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(new NhAssistantAlert(Listed, context.Subject.Subject)));
        Assert.Equal(NhAssistantAlertCodes.KindNotListed, (await RunAsync(host, TaskSubject(Guid.NewGuid()))).ErrorCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_rule_script_of_an_administration_agent_raises_a_listed_kind_and_is_refused_another(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await CreateAgentAsync(host);
        Assert.True((await SaveAsync(host, Settings([Listed]) with { RuleHandler = null })).Success);

        // What the script sees: the kinds the run settings list.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var catalog = (await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().GetCatalogAsync(AgentId, null)).Data!;
            Assert.Equal([Listed], catalog.AlertKinds);
        }

        Assert.True((await SaveScriptAsync(host, $$"""alerts.raise({ kind: "{{Listed}}", key: "today", parameters: { task: subject.id } }); done("Warned.");""", 0)).Success);
        var task = TaskSubject(Guid.NewGuid());
        var raised = await RunAsync(host, task);
        Assert.Equal((NhAssistantTurnStatuses.Completed, NhAssistantRunKinds.Rule), (raised.Outcome, raised.Kind));
        Assert.Equal(AgentId, (await OpenAsync(host, task, "today", shadow: false))!.AgentId);
        Assert.Equal(0, host.Services.GetRequiredService<AlertRuleScript>().Runs);

        // A kind the run settings do not list stops the script, and nothing is raised.
        Assert.True((await SaveScriptAsync(host, $$"""alerts.raise({ kind: "{{Unlisted}}" }); done("Never.");""", 1)).Success);
        var other = TaskSubject(Guid.NewGuid());
        var refused = await RunAsync(host, other);
        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAiScriptCodes.RuntimeError), (refused.Outcome, refused.ErrorCode));
        Assert.Empty(await AlertsAboutAsync(host, other));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_listed_kind_the_code_no_longer_registers_is_a_problem_while_the_run_settings_stay_in_effect(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await CreateAgentAsync(host);
        Assert.True((await SaveAsync(host, Settings([Listed]))).Success);

        // Another host, whose code still registered the kind, stored it; this host's code no longer does.
        var store = host.Services.GetRequiredService<NhAssistantRunConfigurationStore>();
        var stored = (await store.FindRunSettingsAsync(AgentId, CancellationToken.None))!;
        stored.AlertKindsJson = $"[\"{Gone}\",\"{Listed}\"]";
        stored.UpdatedAt = DateTimeOffset.UtcNow;
        Assert.True((await store.SaveRunSettingsAsync(stored, 1, CancellationToken.None)).Succeeded);
        var configurations = host.Services.GetRequiredService<NhAssistantRunConfigurations>();
        await configurations.ReloadAsync(CancellationToken.None);

        // The settings stay in effect without the kind, and the administration shows why.
        Assert.Equal(
            new NhAssistantRunConfigurationProblem(AgentId, NhAssistantRunConfigurationProblems.AlertKindKind, Gone, NhAssistantRunConfigurationProblems.UnknownAlertKind),
            Assert.Single(configurations.Problems));
        Assert.Equal([Listed], State(host).AgentRuns[AgentId].AlertKinds);
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!;
        Assert.True(view.RunSettings!.InEffect);
        Assert.Null(view.RunSettings.Problem);
        Assert.Equal([Gone, Listed], view.RunSettings.AlertKinds);
        Assert.Contains(view.Problems, problem => problem.Kind == NhAssistantRunConfigurationProblems.AlertKindKind && problem.Id == Gone);
        Assert.Contains(host.Logs.Messages, message => message.Contains("out of effect (unknown-alert-kind)", StringComparison.Ordinal));

        // Its alerts stay refused; the listed kind still works.
        var rules = host.Services.GetRequiredService<AlertRuleScript>();
        rules.Run = (context, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(new NhAssistantAlert(Gone, context.Subject.Subject)));
        Assert.Equal(NhAssistantAlertCodes.KindNotListed, (await RunAsync(host, TaskSubject(Guid.NewGuid()))).ErrorCode);
        rules.Run = (context, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(new NhAssistantAlert(Listed, context.Subject.Subject) { Parameters = new Dictionary<string, string> { ["task"] = "Report" } }));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunAsync(host, TaskSubject(Guid.NewGuid()))).Outcome);

        // A save that keeps the list is not refused for it; one that names the kind again is.
        var kept = await SaveAsync(host, Settings(null) with { ExpectedVersion = 2, RetryCount = 1 });
        Assert.True(kept.Success, string.Join(",", kept.GetResultItems().Select(item => item.Name)));
        Assert.Equal([Gone, Listed], kept.Data!.AlertKinds);
        Assert.Equal(("alertKinds", NhAssistantFieldErrors.NotFound), FieldError(await SaveAsync(host, Settings([Gone, Listed]) with { ExpectedVersion = 3 })));
        var removed = await SaveAsync(host, Settings([Listed]) with { ExpectedVersion = 3 });
        Assert.Equal([Listed], removed.Data!.AlertKinds);
        Assert.Empty(configurations.Problems);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_package_carries_the_listed_kinds_and_its_import_refuses_a_kind_the_host_does_not_register(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await CreateAgentAsync(host);
        Assert.True((await SaveAsync(host, Settings(null))).Success);

        // Without kinds the package leaves the list out, so its hash is the one it had before the list existed.
        var plain = await PackagesAsync(host, packages => packages.ExportAsync([AgentId], new NhAssistantAgentPackageExportOptions(PackageId), Administrator));
        Assert.Null(Assert.Single(plain.Data!.Package.Agents).RunSettings!.AlertKinds);
        Assert.DoesNotContain("alertKinds", NhAssistantAgentPackageFormat.Write(plain.Data.Package), StringComparison.Ordinal);

        Assert.True((await SaveAsync(host, Settings([Listed]) with { ExpectedVersion = 1 })).Success);
        var exported = await PackagesAsync(host, packages => packages.ExportAsync([AgentId], new NhAssistantAgentPackageExportOptions(PackageId), Administrator));
        var exportedAgent = Assert.Single(exported.Data!.Package.Agents);
        Assert.Equal([Listed], exportedAgent.RunSettings!.AlertKinds);
        Assert.NotEqual(Assert.Single(plain.Data.Package.Agents).Hashes!["runSettings"], exportedAgent.Hashes!["runSettings"]);

        // Imported as another agent, the list arrives; a kind the host does not register refuses the part.
        var copy = exportedAgent with { Id = "task-watch-copy", Hashes = null };
        var unknown = exported.Data.Package with
        {
            Package = new NhAssistantAgentPackageInfo("task-watch-unknown", 1),
            Agents = [copy with { RunSettings = copy.RunSettings! with { AlertKinds = [Listed, Gone] } }]
        };
        var preview = (await PackagesAsync(host, packages => packages.PreviewImportAsync(unknown, new NhAssistantAgentPackageImportOptions())))!.Data!;
        Assert.False(preview.Importable);
        var refused = preview.Parts.Single(part => part.Part == NhAssistantPackagePartKinds.RunSettings);
        Assert.Equal(NhAssistantPackagePartOutcomes.Refused, refused.Outcome);
        Assert.Equal([NhAssistantFieldErrors.NotFound], refused.Errors!["alertKinds"]);

        var package = exported.Data.Package with { Package = new NhAssistantAgentPackageInfo("task-watch-copy", 1), Agents = [copy] };
        var imported = await PackagesAsync(host, packages => packages.ImportAsync(package, new NhAssistantAgentPackageImportOptions(), Administrator));
        Assert.True(imported.Success, string.Join(", ", imported.AllErrorMessages));
        Assert.Equal([Listed], State(host).AgentRuns["task-watch-copy"].AlertKinds);

        // A package locks the list like any field of the run settings.
        Assert.Contains("alertKinds", NhAssistantPackageLockFields.RunSettings);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(new AssistantTestClock(Start));
                services.AddSingleton<AlertTestSubjects>();
                services.AddSingleton<AlertRuleScript>();
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, AlertPrincipalFactory>());
                services.AddNewHeapPlatformAIScripting();
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<AlertTestSubjectResolver>()
                .AddRuleHandler<AlertTestRules>(RuleHandler, "Checks a task by rule.")
                .AddAlertKind(Listed, kind => kind
                    .DisplayName("tests.alerts.task-due-today")
                    .Urgency(NhAssistantAlertUrgency.Warning)
                    .Title((_, parameters) => parameters.GetValueOrDefault("task", "A task") + " is due today"))
                .AddAlertKind(Unlisted, kind => kind
                    .DisplayName("tests.alerts.task-unlisted")
                    .Title((_, _) => "Unlisted"))
                .AddAlertKind(Templated, kind => kind
                    .DisplayName("tests.alerts.task-at-risk")
                    .Urgency(NhAssistantAlertUrgency.Urgent)
                    .Title((_, parameters) => parameters["task"] + " is at risk")
                    .RaisedByModel(template => template
                        .Describe("A task is at risk.")
                        .Text("task", "The task's name.", 80)))
                // Agents created in the administration may have read-only rule scripts.
                .UseRuleScriptsAsData(scripts => scripts.ForAgentsCreatedInAdministration()));
    }

    private static async Task CreateAgentAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
            new NhAssistantAgentInput(
                AgentId,
                "Task watch",
                "Warns about tasks.",
                "Warn about tasks that are due.",
                ["projects.*"],
                [],
                null,
                NhAiAutonomyLevel.Propose),
            Administrator,
            CancellationToken.None);
        Assert.True(created.Success);
    }

    /// <summary>
    /// The run settings of the task watch: it answers for itself and works by the test's rules.
    /// </summary>
    private static NhAssistantAgentRunSettingsRequest Settings(IReadOnlyList<string>? alertKinds)
    {
        return new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent, RuleHandler: RuleHandler, Reason: "Warn about tasks")
        {
            AlertKinds = alertKinds
        };
    }

    private static Task<TaskResult<NhAssistantAgentRunSettingsDto>> SaveAsync(AssistantTestHost host, NhAssistantAgentRunSettingsRequest request)
    {
        return ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(AgentId, request, Administrator));
    }

    /// <summary>
    /// Saves a rule script of the agent's own rules and sets it live by another administrator.
    /// </summary>
    private static async Task<TaskResult<NhAssistantRuleScriptVersionDto>> SaveScriptAsync(AssistantTestHost host, string script, int revision)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().SaveAsync(
            AgentId,
            null,
            new NhAssistantRuleScriptSaveRequest(script, [], revision, null, Live: true),
            new NhAssistantRuleScriptAuthor(OtherAdministrator));
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task<T> PackagesAsync<T>(AssistantTestHost host, Func<INhAssistantAgentPackages, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>());
    }

    /// <summary>
    /// The one field error of a refused save.
    /// </summary>
    private static (string Field, string Error) FieldError(TaskResult result)
    {
        Assert.False(result.Success);
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, NhAssistantResults.CodeOf(result));
        var (field, errors) = Assert.Single(NhAssistantResults.FieldErrorsOf(result));
        return (field, Assert.Single(errors));
    }

    private static NhAiSubjectRef TaskSubject(Guid taskId)
    {
        return new NhAiSubjectRef("task", taskId.ToString("D"));
    }

    private static Task<AssistantAlert?> OpenAsync(AssistantTestHost host, NhAiSubjectRef task, string key, bool shadow)
    {
        return host.Services.GetRequiredService<NhAssistantAlertStore>().FindOpenAsync(ScopeKey, Listed, task, key, shadow, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<AssistantAlert>> AlertsAboutAsync(AssistantTestHost host, NhAiSubjectRef subject)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Alerts
            .AsNoTracking()
            .Where(alert => alert.SubjectType == subject.Type && alert.SubjectId == subject.Id)
            .ToListAsync();
    }

    /// <summary>
    /// A headless run of the task watch on <paramref name="subject"/>, in its live or shadow thread.
    /// </summary>
    private static async Task<NhAssistantAgentTurnResult> RunAsync(AssistantTestHost host, NhAiSubjectRef subject, bool shadow = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "morning-round",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            Shadow = shadow,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false);
        return await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Morning round.")]),
            CancellationToken.None);
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }
}
