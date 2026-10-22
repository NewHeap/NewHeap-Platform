using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Named filters of a trigger event: a data trigger selects them per trigger, so an agent is not
/// started again by a change it made itself. They decide after the event's own <c>When</c>, know the
/// trigger they decide for and fail closed when the code no longer registers them.
/// </summary>
public sealed class AssistantTriggerEventFilterTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string TriggerId = "project-edited-check";
    private const string EventName = "project.edited";
    private const string NotOwnChange = "not-own-change";
    private const string NotBulk = "not-bulk";
    private const string Admin = "admin-1";
    private const string Person = "member-1";

    [Fact]
    public void A_filter_has_a_dash_case_name_unique_per_event_and_a_short_description()
    {
        var builder = new NhAssistantTriggerEventBuilder<ProjectEdited>()
            .Filter(NotOwnChange, "Skips changes the agent made itself.", (_, _) => true);

        Assert.Throws<ArgumentException>(() => builder.Filter("Not Own", "Skips changes.", (_, _) => true));
        Assert.Throws<ArgumentException>(() => builder.Filter(new string('a', 65), "Skips changes.", (_, _) => true));
        Assert.Throws<ArgumentException>(() => builder.Filter(NotOwnChange, "Skips them again.", (_, _) => true));
        Assert.Throws<ArgumentException>(() => builder.Filter(NotBulk, " ", (_, _) => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Filter(NotBulk, new string('x', 301), (_, _) => true));
        Assert.Throws<ArgumentNullException>(() => builder.Filter(NotBulk, "Skips imports.", null!));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_selected_filter_skips_the_agents_own_change_and_a_persons_change_starts_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "edited" } }, "call-search")
            .RespondWithText("Checked the edited project.");
        await using var host = await CreateHostAsync(provider, model);
        var decisions = host.Services.GetRequiredService<FilterDecisions>();
        Assert.True((await SaveTriggerAsync(host, [NotOwnChange])).Success);
        Assert.True((await ConfigureAsync(host, configuration => configuration.SetTriggerShadowAsync(AgentId, TriggerId, false, Admin))).Success);

        // A person's change starts the agent; the filter learns which trigger it decides for.
        var project = Guid.NewGuid();
        var started = await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), project, Person));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, started.Outcome);
        var context = Assert.Single(decisions.Contexts);
        Assert.Equal(new NhAssistantTriggerFilterContext(AgentId, null, TriggerId, "assistant-agent:" + AgentId), context);

        // The actor the filter compares with is the one the agent's tool calls run as.
        await ExecuteAsync(host, started.RunId!.Value);
        var toolCall = Assert.Single(host.Tools.Contexts);
        Assert.Equal(context.AgentActorId, toolCall.ActorId);
        Assert.Equal(NhAiActorKind.Agent, toolCall.ActorKind);

        // A change the agent's tool made, stamped with that actor, starts nothing and is not audited.
        var audited = TriggerAudits(host);
        var own = await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), project, toolCall.ActorId));
        Assert.Equal((NhAssistantTriggerOutcomes.Ignored, NhAssistantTriggerReasons.Filtered, (Guid?)null), (own.Outcome, own.ReasonCode, own.RunId));
        Assert.Equal(audited, TriggerAudits(host));

        // The event's own When decides first: an event it refuses reaches no filter.
        var before = decisions.Contexts.Count;
        var draft = await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), project, Person, Draft: true));
        Assert.Equal((NhAssistantTriggerOutcomes.Ignored, NhAssistantTriggerReasons.Filtered), (draft.Outcome, draft.ReasonCode));
        Assert.Equal(before, decisions.Contexts.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Every_selected_filter_must_accept_and_a_change_of_the_filters_is_the_triggers_next_version(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());
        var store = host.Services.GetRequiredService<NhAssistantRunConfigurationStore>();
        var created = await SaveTriggerAsync(host, [NotOwnChange]);
        Assert.Equal([NotOwnChange], created.Data!.Filters);
        var first = (await store.FindTriggerAsync(TriggerId, CancellationToken.None))!;
        Assert.Equal("[\"not-own-change\"]", first.FiltersJson);

        // Both filters, in the order chosen; a name given twice counts once.
        var changed = await SaveTriggerAsync(host, [NotBulk, NotOwnChange, NotBulk], expectedVersion: 1);
        Assert.True(changed.Success);
        Assert.Equal(2, changed.Data!.Version);
        Assert.Equal([NotBulk, NotOwnChange], changed.Data.Filters);
        var second = (await store.FindTriggerAsync(TriggerId, CancellationToken.None))!;
        Assert.Equal("[\"not-bulk\",\"not-own-change\"]", second.FiltersJson);
        Assert.NotEqual(first.ConcurrencyStamp, second.ConcurrencyStamp);
        Assert.Equal([NotBulk, NotOwnChange], State(host).Triggers[TriggerId].Filters.Select(filter => filter.Name));
        var view = (await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!;
        Assert.Equal([NotBulk, NotOwnChange], Assert.Single(view.DataTriggers).Filters);

        // A person's bulk import passes one filter and not the other.
        var bulk = await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), Guid.NewGuid(), Person, Bulk: true));
        Assert.Equal((NhAssistantTriggerOutcomes.Ignored, NhAssistantTriggerReasons.Filtered), (bulk.Outcome, bulk.ReasonCode));
        Assert.Equal(NhAssistantTriggerOutcomes.Started, (await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), Guid.NewGuid(), Person))).Outcome);

        // Without filters the trigger takes the agent's own change as well.
        var cleared = await SaveTriggerAsync(host, [], expectedVersion: 2);
        Assert.Equal((3, 0), (cleared.Data!.Version, cleared.Data.Filters.Count));
        Assert.Null((await store.FindTriggerAsync(TriggerId, CancellationToken.None))!.FiltersJson);
        Assert.Equal(
            NhAssistantTriggerOutcomes.Started,
            (await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), Guid.NewGuid(), "assistant-agent:" + AgentId))).Outcome);

        var changes = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentTriggerDefinitionChanged);
        Assert.Equal(["created", "changed", "changed"], changes.Select(evt => evt.ResultCode));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Saving_refuses_a_filter_the_event_does_not_register(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());

        var unknown = await SaveTriggerAsync(host, [NotOwnChange, "not-weekend"]);
        Assert.Equal(NhAssistantErrorCodes.TriggerFilterNotFound, NhAssistantResults.CodeOf(unknown));
        Assert.Equal(["not-found"], NhAssistantResults.FieldErrorsOf(unknown)["filters"]);
        Assert.Equal("filters", Field(await SaveTriggerAsync(host, ["Not Own"])));
        Assert.Equal("filters", Field(await SaveTriggerAsync(host, Enumerable.Range(1, 11).Select(index => $"filter-{index}").ToArray())));
        Assert.Equal("filters", Field(await ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(
            AgentId,
            "weekly-check",
            new NhAssistantDataTriggerRequest(NhAssistantDataTriggerSources.Schedule, Cron: "0 7 * * 1", SubjectSet: "test-projects", Message: "Weekly check", Filters: [NotOwnChange]),
            Admin))));
        Assert.Null(await host.Services.GetRequiredService<NhAssistantRunConfigurationStore>().FindTriggerAsync(TriggerId, CancellationToken.None));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_filter_the_event_no_longer_registers_keeps_the_trigger_from_firing_and_is_reported(AssistantTestProvider provider)
    {
        // The host reads the stamp at every check, so it sees the row another host stored at once.
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), new NhAssistantTiming { RunConfigurationCheckInterval = TimeSpan.Zero });
        var configurations = host.Services.GetRequiredService<NhAssistantRunConfigurations>();
        await configurations.EnsureCurrentAsync(CancellationToken.None);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            // Seeds the agents, which the stored trigger belongs to.
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().GetAsync(AgentId));
        }

        // A host whose code still registered the filter stored the trigger; this deploy removed it.
        var now = DateTimeOffset.UtcNow;
        var stored = await host.Services.GetRequiredService<NhAssistantRunConfigurationStore>().SaveTriggerAsync(
            new AssistantAgentTriggerDefinition
            {
                Id = TriggerId,
                AgentId = AgentId,
                Source = NhAssistantDataTriggerSources.Event,
                EventName = EventName,
                FiltersJson = "[\"not-own-change\",\"not-weekend\"]",
                Conflict = NhAssistantTriggerConflictNames.Queue,
                UpdatedAt = now,
                UpdatedBy = Admin
            },
            null,
            CancellationToken.None);
        Assert.True(stored.Succeeded);
        await configurations.EnsureCurrentAsync(CancellationToken.None);

        var definition = State(host).Triggers[TriggerId];
        Assert.Equal(["not-weekend"], definition.MissingFilters);
        Assert.Equal(
            new NhAssistantRunConfigurationProblem(AgentId, NhAssistantRunConfigurationProblems.TriggerKind, TriggerId, NhAssistantRunConfigurationProblems.UnknownFilter),
            Assert.Single(configurations.Problems));
        var view = Assert.Single((await ConfigureAsync(host, configuration => configuration.GetAsync(AgentId)))!.DataTriggers);
        Assert.Equal((false, NhAssistantRunConfigurationProblems.UnknownFilter), (view.InEffect, view.Problem));
        Assert.Equal([NotOwnChange, "not-weekend"], view.Filters);

        // Fail closed: nobody knows what the missing filter would have skipped.
        var audited = TriggerAudits(host);
        var receipt = await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), Guid.NewGuid(), Person));
        Assert.Equal((NhAssistantTriggerOutcomes.Ignored, NhAssistantTriggerReasons.FilterMissing, (Guid?)null), (receipt.Outcome, receipt.ReasonCode, receipt.RunId));
        Assert.Equal(audited, TriggerAudits(host));
        Assert.Contains(host.Logs.Messages, message => message.Contains("no longer registers the filters not-weekend", StringComparison.Ordinal));

        // Saved without the missing filter, the trigger fires again.
        Assert.True((await SaveTriggerAsync(host, [NotOwnChange], expectedVersion: 1)).Success);
        Assert.Empty(configurations.Problems);
        Assert.Equal(NhAssistantTriggerOutcomes.Started, (await PublishAsync(host, new ProjectEdited(Guid.NewGuid(), Guid.NewGuid(), Person))).Outcome);
    }

    [Fact]
    public async Task The_run_catalog_lists_the_filters_of_an_event_and_a_data_trigger_carries_its_own()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                // This host runs no agents; the startup check would require their background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => Compose(assistant.AddSubjectType<TestSubjectResolver>(), decisions: null));
        using var admin = app.CreateClient(admin: true);

        var catalog = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/run-catalog"));
        var edited = Assert.Single(catalog.GetProperty("triggerEvents").EnumerateArray());
        Assert.Equal(
            [(NotOwnChange, "Skips changes the agent made itself."), (NotBulk, "Skips bulk imports.")],
            edited.GetProperty("filters").EnumerateArray().Select(filter => (filter.GetProperty("name").GetString(), filter.GetProperty("description").GetString())));
        // Other building blocks have no filters.
        Assert.Equal(JsonValueKind.Null, catalog.GetProperty("subjectSets")[0].GetProperty("filters").ValueKind);

        using var saved = await admin.PutAsync(
            $"/api/assistant/admin/agents/{AgentId}/triggers/{TriggerId}",
            Json(new { source = "event", @event = EventName, conflict = "queue", filters = new[] { NotOwnChange } }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal([NotOwnChange], (await ReadJsonAsync(saved)).GetProperty("filters").EnumerateArray().Select(name => name.GetString()));
        var configuration = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{AgentId}/configuration"));
        Assert.Equal([NotOwnChange], configuration.GetProperty("dataTriggers")[0].GetProperty("filters").EnumerateArray().Select(name => name.GetString()));

        using var unknown = await admin.PutAsync(
            $"/api/assistant/admin/agents/{AgentId}/triggers/{TriggerId}",
            Json(new { source = "event", @event = EventName, filters = new[] { "not-weekend" }, expectedVersion = 1 }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var error = await ReadJsonAsync(unknown);
        Assert.Equal(NhAssistantErrorCodes.TriggerFilterNotFound, error.GetProperty("code").GetString());
        Assert.Equal("not-found", error.GetProperty("errors").GetProperty("filters")[0].GetString());
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        NhAssistantTiming? timing = null)
    {
        var decisions = new FilterDecisions();
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                if (timing is not null)
                {
                    services.AddSingleton(timing);
                }

                services.AddSingleton(decisions);
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => Compose(assistant.AddSubjectType<TestSubjectResolver>(), decisions));
    }

    /// <summary>
    /// The agent works with the model and the project tools; the edited-project event offers two
    /// filters. <paramref name="decisions"/> records what the own-change filter learns.
    /// </summary>
    private static NhAssistantBuilder Compose(NhAssistantBuilder assistant, FilterDecisions? decisions)
    {
        return assistant
            .AddApprovalPolicy("project-review", policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(3))
                .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
            .AddTriggerEvent<ProjectEdited>(EventName, evt => evt
                .Description("A project was edited.")
                .When(edit => !edit.Draft)
                .Subject("project", edit => edit.ProjectId.ToString("N"))
                .Initiator(edit => edit.EditedBy)
                .Message(edit => "The project was edited.")
                .Dedupe(edit => edit.EventId.ToString("N"))
                .Filter(NotOwnChange, "Skips changes the agent made itself.", (edit, trigger) =>
                {
                    decisions?.Contexts.Enqueue(trigger);
                    return edit.EditedBy != trigger.AgentActorId;
                })
                .Filter(NotBulk, "Skips bulk imports.", (edit, _) => !edit.Bulk))
            .AddSubjectSet("test-projects", "One test project", TestProjectsAsync)
            .ConfigureAgent(AgentId, agent => agent
                .Grant("projects.*")
                .UseApprovalPolicy("project-review"));
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> TestProjectsAsync(
        IServiceProvider services,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new NhAiSubjectRef("project", "11111111111111111111111111111111");
    }

    private static Task<TaskResult<NhAssistantDataTriggerDto>> SaveTriggerAsync(
        AssistantTestHost host,
        IReadOnlyList<string> filters,
        int? expectedVersion = null)
    {
        return ConfigureAsync(host, configuration => configuration.SaveTriggerAsync(
            AgentId,
            TriggerId,
            new NhAssistantDataTriggerRequest(
                NhAssistantDataTriggerSources.Event,
                Event: EventName,
                Conflict: NhAssistantTriggerConflictNames.Queue,
                DebounceSeconds: 0,
                ExpectedVersion: expectedVersion,
                Filters: filters),
            Admin));
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    /// <summary>
    /// Publishes the event and returns the receipt of the data trigger.
    /// </summary>
    private static async Task<NhAssistantTriggerReceipt> PublishAsync(AssistantTestHost host, ProjectEdited evt)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().PublishAsync(evt);
        return Assert.Single(receipts, receipt => receipt.TriggerId == TriggerId);
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        var result = await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
        Assert.True(result.Success);
    }

    private static int TriggerAudits(AssistantTestHost host)
    {
        return host.Business.Events.Count(evt => evt.Kind is NhAssistantAuditEventKind.AgentTriggerAccepted or NhAssistantAuditEventKind.AgentTriggerRejected);
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }

    /// <summary>
    /// The one field a validation failure names.
    /// </summary>
    private static string? Field(TaskResult result)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, NhAssistantResults.CodeOf(result));
        return Assert.Single(NhAssistantResults.FieldErrorsOf(result).Keys);
    }

    private static StringContent Json(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// A project was edited by <see cref="EditedBy"/>: a person, or the actor of an agent whose tool
/// edited it.
/// </summary>
public sealed record ProjectEdited(Guid EventId, Guid ProjectId, string? EditedBy, bool Bulk = false, bool Draft = false);

/// <summary>
/// What the own-change filter learned, in the order it decided.
/// </summary>
public sealed class FilterDecisions
{
    public ConcurrentQueue<NhAssistantTriggerFilterContext> Contexts { get; } = new();
}
