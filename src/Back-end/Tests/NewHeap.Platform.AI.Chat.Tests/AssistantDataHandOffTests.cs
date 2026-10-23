using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Hand-offs defined as data (S9 of the agent runs adoption plan): administrators connect agents
/// within the ceiling in code, saving and every load check them as startup checks
/// <c>AcceptHandOffs</c>, a stored hand-off that no longer fits is out of effect with a problem, and
/// in effect it works as one in code, with its depth, scope, daily limit and shadow rules.
/// </summary>
public sealed class AssistantDataHandOffTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Planner = "project-planner";
    private const string Closer = "project-closer";
    private const string Auditor = "project-auditor";
    private const string ShadowPlanner = "shadow-planner";
    private const string OrderDesk = "order-desk";
    private const string ChatOnly = "chat-only";
    private const string Admin = "admin-1";
    private const string OrderToolId = "assistant.handoff.project-closer.order";
    private const string OrderFunction = "handoff_project-closer_order";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Saving_checks_a_hand_off_as_startup_checks_code_and_within_the_ceiling_and_puts_it_in_effect(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);

        // The checks of AcceptHandOffs and the ceiling, as field errors and codes.
        Assert.Equal("sources", Field(await SaveAsync(host, Closer, "order", Request([])), NhAssistantFieldErrors.Required));
        Assert.Equal("sources", Field(await SaveAsync(host, Closer, "order", Request([Closer])), NhAssistantFieldErrors.Invalid));
        Assert.Equal("sources", Field(await SaveAsync(host, Closer, "order", Request([Planner, Planner])), NhAssistantFieldErrors.Invalid));
        Assert.Equal("sources", Field(await SaveAsync(host, Closer, "order", Request([Auditor])), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("sources", Field(await SaveAsync(host, Closer, "order", Request(["gone-agent"])), NhAssistantFieldErrors.NotFound));
        Assert.Equal("description", Field(await SaveAsync(host, Closer, "order", Request([Planner]) with { Description = new string('x', 501) }), NhAssistantFieldErrors.TooLong));
        Assert.Equal("maxPerSubjectPerDay", Field(await SaveAsync(host, Closer, "order", Request([Planner]) with { MaxPerSubjectPerDay = 11 }), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal("maxPerSubjectPerDay", Field(await SaveAsync(host, Closer, "order", Request([Planner]) with { MaxPerSubjectPerDay = 101 }), NhAssistantFieldErrors.Invalid));
        Assert.Equal("subjectType", Field(await SaveAsync(host, Closer, "task", Request([Planner])), NhAssistantFieldErrors.NotAllowed));
        Assert.Equal(NhAssistantErrorCodes.HandOffInCode, Code(await SaveAsync(host, Closer, "project", Request([Planner]))));
        Assert.Equal(NhAssistantErrorCodes.HandOffsNotAllowed, Code(await SaveAsync(host, Planner, "order", Request([Closer]))));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, Code(await SaveAsync(host, "missing-agent", "order", Request([Planner]))));
        // An agent created in the administration needs its run settings, and works on what it says.
        Assert.Equal(NhAssistantErrorCodes.AgentNotConfigured, Code(await SaveAsync(host, ChatOnly, "order", Request([Planner]))));
        Assert.Equal("subjectType", Field(await SaveAsync(host, OrderDesk, "order", Request([Planner])), NhAssistantFieldErrors.Invalid));

        var saved = await SaveAsync(host, Closer, "order", Request([Planner]) with
        {
            Description = "Checks whether an order can be closed.",
            NotifySource = true,
            MaxPerSubjectPerDay = 3,
            Reason = "Pilot"
        });
        Assert.True(saved.Success);
        var dto = saved.Data!;
        Assert.Equal((1, true, (string?)null, OrderToolId, 3), (dto.Version, dto.InEffect, dto.Problem, dto.ToolId, dto.MaxPerSubjectPerDay));
        Assert.Equal([Planner], dto.Sources);

        // In effect as one in code would be, next to the hand-off in code; code itself is unchanged.
        var handOffs = State(host).AgentRuns[Closer].HandOffs;
        Assert.Equal(["project", "order"], handOffs.Select(item => item.SubjectType));
        var data = handOffs.Single(item => item.IsData);
        Assert.Equal((NhAssistantHandOffSources.Data, (int?)1, OrderToolId, true, 3), (data.Source, data.DataVersion, data.ToolId, data.NotifySource, data.MaxPerSubjectPerDay));
        Assert.Equal("Checks whether an order can be closed.", data.Description);
        Assert.Equal([Planner], data.Sources);
        Assert.DoesNotContain(State(host).CodeAgentRuns[Closer].HandOffs, item => item.IsData);

        // A change is the next version; a stale version is refused.
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await SaveAsync(host, Closer, "order", Request([Planner, ShadowPlanner]))));
        var changed = await SaveAsync(host, Closer, "order", Request([Planner, ShadowPlanner]) with { ExpectedVersion = 1 });
        Assert.Equal((2, 5), (changed.Data!.Version, changed.Data.MaxPerSubjectPerDay));

        // The administration sees code, data and what the ceiling lets it add.
        var view = (await HandOffsAsync(host, handOffs => handOffs.GetAsync(Closer)))!;
        Assert.True(view.Configured);
        Assert.Equal(["project"], view.CodeHandOffs.Select(item => item.SubjectType));
        Assert.Equal([Planner], view.CodeHandOffs.Single().Sources);
        Assert.Equal(["order"], view.DataHandOffs.Select(item => item.SubjectType));
        var allowance = Assert.Single(view.Ceiling!.SubjectTypes);
        Assert.Equal(("order", false), (allowance.SubjectType, allowance.SourcesCreatedInAdministration));
        Assert.Equal([Planner, ShadowPlanner], allowance.Sources);
        Assert.Equal(10, view.Ceiling.MaxPerSubjectPerDay);
        Assert.Null((await HandOffsAsync(host, handOffs => handOffs.GetAsync(Planner)))!.Ceiling);
        Assert.Null(await HandOffsAsync(host, handOffs => handOffs.GetAsync("missing-agent")));
        var auditor = (await HandOffsAsync(host, handOffs => handOffs.GetAsync(Auditor)))!;
        Assert.True(Assert.Single(auditor.Ceiling!.SubjectTypes).SourcesCreatedInAdministration);
        Assert.Contains(OrderDesk, auditor.Ceiling.SubjectTypes.Single().Sources);

        // Removing takes it out of effect at once; code stays.
        Assert.Equal(NhAssistantErrorCodes.HandOffInCode, Code(await HandOffsAsync(host, handOffs => handOffs.RemoveAsync(Closer, "project", null, Admin))));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, Code(await HandOffsAsync(host, handOffs => handOffs.RemoveAsync(Closer, "order", 1, Admin))));
        Assert.True((await HandOffsAsync(host, handOffs => handOffs.RemoveAsync(Closer, "order", 2, Admin, "Pilot ended"))).Success);
        Assert.Equal(NhAssistantErrorCodes.HandOffNotFound, Code(await HandOffsAsync(host, handOffs => handOffs.RemoveAsync(Closer, "order", null, Admin))));
        Assert.Equal(["project"], State(host).AgentRuns[Closer].HandOffs.Select(item => item.SubjectType));

        // Audited with the subject type and the version, never the description.
        var audited = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentHandOffDefinitionChanged).ToArray();
        Assert.Equal(["created", "changed", "removed"], audited.Select(evt => evt.ResultCode));
        Assert.Equal([1, 2, 2], audited.Select(evt => evt.AgentVersion));
        Assert.All(audited, evt => Assert.Equal((Closer, "order", "order", Admin), (evt.AgentId, evt.SubjectType, evt.ObjectId, evt.ActorId)));
        Assert.DoesNotContain(host.Business.Events, evt => evt.ResultCode?.Contains("closed", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_hand_off_works_as_one_in_code_with_its_daily_limit_scope_shadow_and_depth(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, limits => limits.MaxHandOffDepth = 1);
        var receipts = new ConcurrentQueue<(string Case, NhAssistantHandOffReceipt Receipt)>();
        var order = "o" + Guid.NewGuid().ToString("N")[1..];
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            if (context.TriggerId == NhAssistantTriggerIds.HandOffEnded)
            {
                return NhAssistantRuleResult.Done("Noted.");
            }

            var change = context.Inputs.Single().Text;
            var subject = new NhAiSubjectRef("order", change.Contains("other", StringComparison.Ordinal) ? "x" + order[1..] : order);
            receipts.Enqueue((change, await context.HandOffAsync(Closer, subject, "Please close the order.", null, cancellationToken)));
            return NhAssistantRuleResult.Done("Handed off.");
        };
        var closer = new ConcurrentQueue<NhAssistantHandOffReceipt>();
        host.Services.GetRequiredService<TestSkillRuleScript>().Run = async (context, cancellationToken) =>
        {
            // On to the auditor by a hand-off as data: one hand-off is the depth here.
            closer.Enqueue(await context.HandOffAsync(Auditor, new NhAiSubjectRef("project", Guid.NewGuid().ToString("N")), "Audit it.", null, cancellationToken));
            return NhAssistantRuleResult.Done("Closed the order.");
        };

        // Without the data hand-off the closer accepts no orders.
        await RunPlannerAsync(host, "plan first");
        Assert.Equal(NhAssistantErrorCodes.HandOffNotAccepted, receipts.Last().Receipt.Code);

        Assert.True((await SaveAsync(host, Closer, "order", Request([Planner, ShadowPlanner]) with { MaxPerSubjectPerDay = 1 })).Success);
        Assert.True((await SaveAsync(host, Auditor, "project", Request([Closer]))).Success);

        await RunPlannerAsync(host, "plan accepted");
        var accepted = receipts.Last().Receipt;
        Assert.Equal(NhAssistantHandOffOutcomes.Accepted, accepted.Outcome);
        await ExecuteAsync(host, accepted.RunId!.Value);
        var target = (await Runs(host).FindRunAsync(accepted.RunId.Value, CancellationToken.None))!;
        Assert.Equal((NhAssistantTriggerIds.HandOff, Planner, 1, false), (target.TriggerId, target.RequestedByAgentId, target.HandOffDepth, target.Shadow));
        Assert.Equal(Closer, (await Runs(host).FindThreadByConversationAsync(target.ConversationId, CancellationToken.None))!.AgentId);

        // The chain ends at its depth, also through a hand-off as data.
        Assert.Equal(NhAssistantErrorCodes.HandOffChainLimit, Assert.Single(closer).Code);

        // The stored daily limit per source and subject holds; another scope is refused as in code.
        await RunPlannerAsync(host, "plan again");
        Assert.Equal(NhAssistantErrorCodes.HandOffLimitReached, receipts.Last().Receipt.Code);
        await RunPlannerAsync(host, "plan other");
        Assert.Equal(NhAssistantErrorCodes.HandOffSubjectUnavailable, receipts.Last().Receipt.Code);

        // Shadow work never starts live work: the shadow source is refused.
        var shadow = Assert.Single(await PublishAsync(host, "shadow plan"));
        await ExecuteAsync(host, shadow.RunId!.Value);
        Assert.Equal(NhAssistantErrorCodes.HandOffShadowToLive, receipts.Last().Receipt.Code);
        Assert.Equal(NhAssistantHandOffOutcomes.Refused, receipts.Last().Receipt.Outcome);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_sources_model_gets_the_generated_tool_while_the_data_hand_off_is_in_effect(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Nothing to hand off.")
            .RespondWithText("Nothing to hand off.")
            .RespondWithText("Nothing to hand off.");
        await using var host = await CreateHostAsync(provider, model: model);

        await RunModelAsync(host);
        Assert.DoesNotContain(OrderFunction, ToolNames(model.Requests[0]));

        Assert.True((await SaveAsync(host, Closer, "order", Request([Planner]) with { Description = "Closes orders." })).Success);
        await RunModelAsync(host);
        var offered = model.Requests[1].Options!.Tools!.Single(tool => tool.Name == OrderFunction);
        Assert.StartsWith("Closes orders.", offered.Description, StringComparison.Ordinal);

        Assert.True((await HandOffsAsync(host, handOffs => handOffs.RemoveAsync(Closer, "order", 1, Admin))).Success);
        await RunModelAsync(host);
        Assert.DoesNotContain(OrderFunction, ToolNames(model.Requests[2]));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_stored_hand_off_that_no_longer_fits_is_out_of_effect_with_its_problem_and_never_used(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var store = host.Services.GetRequiredService<NhAssistantRunConfigurationStore>();

        // Another host, or the code before a deploy, stored what this host's code no longer allows.
        await StoreAsync(store, Row(Closer, "project", [Planner]));
        await StoreAsync(store, Row(Closer, "order", [Planner], row => row.MaxPerSubjectPerDay = 50));
        await StoreAsync(store, Row(Closer, "task", [Planner]));
        await StoreAsync(store, Row(Auditor, "project", ["gone-agent"]));
        await StoreAsync(store, Row(OrderDesk, "order", [Planner]));
        await StoreAsync(store, Row(ChatOnly, "order", [Planner]));
        await StoreAsync(store, Row(Planner, "project", [], row => row.SourcesJson = "not json"));
        // One that fits stays in effect.
        await StoreAsync(store, Row(OrderDesk, "project", [Closer]));
        await host.Services.GetRequiredService<NhAssistantRunConfigurations>().ReloadAsync(CancellationToken.None);

        var problems = host.Services.GetRequiredService<NhAssistantRunConfigurations>().Problems
            .Where(problem => problem.Kind == NhAssistantRunConfigurationProblems.HandOffKind)
            .Select(problem => (problem.AgentId, problem.Id, problem.Code))
            .ToArray();
        Assert.Equivalent(
            new[]
            {
                (Closer, "order", NhAssistantRunConfigurationProblems.LimitAboveCeiling),
                (Closer, "project", NhAssistantRunConfigurationProblems.ConflictsWithCode),
                (Closer, "task", NhAssistantRunConfigurationProblems.OutsideCeiling),
                (ChatOnly, "order", NhAssistantRunConfigurationProblems.UnknownAgent),
                (OrderDesk, "order", NhAssistantRunConfigurationProblems.NotWorkedOn),
                (Planner, "project", NhAssistantRunConfigurationProblems.Invalid),
                (Auditor, "project", NhAssistantRunConfigurationProblems.UnknownSource)
            },
            problems,
            strict: true);

        // Never in effect with less: no data hand-off of these is used.
        var agentRuns = State(host).AgentRuns;
        Assert.Equal(["project"], agentRuns[Closer].HandOffs.Select(item => item.SubjectType));
        Assert.DoesNotContain(agentRuns[Closer].HandOffs, item => item.IsData);
        Assert.DoesNotContain(agentRuns[Auditor].HandOffs, item => item.IsData);
        Assert.DoesNotContain(agentRuns[Planner].HandOffs, item => item.IsData);
        Assert.False(agentRuns.ContainsKey(ChatOnly));
        var fits = Assert.Single(agentRuns[OrderDesk].HandOffs);
        Assert.Equal(("project", true), (fits.SubjectType, fits.IsData));
        Assert.Equal([Closer], fits.Sources);

        var view = (await HandOffsAsync(host, handOffs => handOffs.GetAsync(Closer)))!;
        var stale = Assert.Single(view.DataHandOffs, item => item.SubjectType == "order");
        Assert.Equal((false, NhAssistantRunConfigurationProblems.LimitAboveCeiling), (stale.InEffect, stale.Problem));
        var desk = (await HandOffsAsync(host, handOffs => handOffs.GetAsync(OrderDesk)))!;
        Assert.Equal([(false, "order"), (true, "project")], desk.DataHandOffs.Select(item => (item.InEffect, item.SubjectType)));

        // The same rows without the ceiling: the application no longer calls UseHandOffsAsData.
        var withoutCeiling = new NhAssistantRunConfigurationValidator(new NhAssistantRegistrationState(), new ServiceCollection().BuildServiceProvider());
        Assert.Equal(
            NhAssistantRunConfigurationProblems.OutsideCeiling,
            withoutCeiling.CheckHandOff(Row(OrderDesk, "project", [Closer]), agentRuns).Problem);
    }

    [Fact]
    public async Task Only_administrators_read_or_change_hand_offs_and_shadow_variants_over_HTTP()
    {
        const string Target = "project-assistant";
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
                // The agent is configured without background operations, which runs need.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
            },
            assistantBuilder: assistant => assistant
                .ConfigureAgent(Target, _ => { })
                .UseHandOffsAsData(handOffs => handOffs.Allow("project", rule => rule.To(Target).FromAgentsCreatedInAdministration()))
                .UseShadowVariantsAsData(variants => variants.Of(Target)));
        using var reader = app.CreateClient("reader-1");
        var variant = new
        {
            id = "assistant-v2",
            displayName = "Assistant v2",
            description = "Tries another way.",
            instructions = "Answer briefly.",
            toolSelectors = Array.Empty<string>(),
            autonomy = "execute",
            shadowOf = Target
        };

        await AssertForbiddenAsync(await reader.GetAsync($"/api/assistant/admin/agents/{Target}/hand-offs"));
        await AssertForbiddenAsync(await reader.PutAsJsonAsync($"/api/assistant/admin/agents/{Target}/hand-offs/project", new { sources = new[] { "helper" } }));
        await AssertForbiddenAsync(await reader.DeleteAsync($"/api/assistant/admin/agents/{Target}/hand-offs/project?expectedVersion=1"));
        await AssertForbiddenAsync(await reader.GetAsync($"/api/assistant/admin/agents/{Target}/shadow-variants"));
        await AssertForbiddenAsync(await reader.PostAsJsonAsync("/api/assistant/admin/agents", variant));
        using var anonymous = app.CreateClient(user: null, access: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/assistant/admin/agents/{Target}/hand-offs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/assistant/admin/agents/{Target}/shadow-variants")).StatusCode);

        // Nothing was stored.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = scope.ServiceProvider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
            Assert.Empty(await context.AgentHandOffDefinitions.ToListAsync());
            Assert.Empty(await context.Agents.Where(agent => agent.ShadowOfAgentId != null).ToListAsync());
        }

        // An administrator reads what the ceiling allows, creates a variant and sees it.
        using var admin = app.CreateClient("admin-1", admin: true);
        var handOffs = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{Target}/hand-offs"));
        Assert.Equal("project", handOffs.GetProperty("ceiling").GetProperty("subjectTypes")[0].GetProperty("subjectType").GetString());
        Assert.True(handOffs.GetProperty("configured").GetBoolean());
        var created = await ReadJsonAsync(await admin.PostAsJsonAsync("/api/assistant/admin/agents", variant), HttpStatusCode.Created);
        Assert.Equal((Target, false), (created.GetProperty("shadowOf").GetString(), created.GetProperty("offeredInChat").GetBoolean()));
        var variants = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{Target}/shadow-variants"));
        Assert.True(variants.GetProperty("allowsDataVariants").GetBoolean());
        var listed = variants.GetProperty("variants")[0];
        Assert.Equal(("assistant-v2", "data", false), (listed.GetProperty("agentId").GetString(), listed.GetProperty("source").GetString(), listed.GetProperty("hasRunSettings").GetBoolean()));
        using var missing = await admin.GetAsync("/api/assistant/admin/agents/missing-agent/hand-offs");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, HttpStatusCode.Forbidden);
            Assert.Equal(NhAssistantAdminErrorCodes.Forbidden, body.GetProperty("code").GetString());
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public void The_ceiling_names_configured_agents_and_targets_that_work_on_the_type_as_AcceptHandOffs_requires()
    {
        Assert.Contains(
            "not configured with ConfigureAgent",
            Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
                .UseHandOffsAsData(handOffs => handOffs.Allow("order", rule => rule.To("unknown-agent").From(Planner))))).Message);
        Assert.Contains(
            "does not work on",
            Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
                .ConfigureAgent(Closer, agent => agent.WorksOn("project"))
                .UseHandOffsAsData(handOffs => handOffs.Allow("order", rule => rule.To(Closer).From(Planner))))).Message);
        Validate(assistant => assistant.UseHandOffsAsData(handOffs => handOffs.Allow("order", rule => rule.To(Closer).From(Planner))));

        var builder = new NhAssistantBuilder(new ServiceCollection(), new NhAssistantRegistrationState());
        Assert.Throws<InvalidOperationException>(() => builder.UseHandOffsAsData(_ => { }));
        Assert.Throws<InvalidOperationException>(() => builder.UseHandOffsAsData(handOffs => handOffs.Allow("order", rule => rule.From(Planner))));
        Assert.Throws<InvalidOperationException>(() => builder.UseHandOffsAsData(handOffs => handOffs.Allow("order", rule => rule.ToAgentsCreatedInAdministration())));
        Assert.Throws<ArgumentException>(() => builder.UseHandOffsAsData(handOffs => handOffs.Allow("Order Type", rule => rule.To(Closer).From(Planner))));
        Assert.Throws<ArgumentException>(() => builder.UseHandOffsAsData(handOffs => handOffs.Allow("order", rule => rule.To("Closer").From(Planner))));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseHandOffsAsData(handOffs => handOffs.MaxPerSubjectPerDay(0)));
    }

    private static void Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .AddAgent(AssistantTestData.Agent(Planner))
                .AddAgent(AssistantTestData.Agent(Closer))
                .ConfigureAgent(Planner, _ => { })
                .ConfigureAgent(Closer, _ => { });
            configure(assistant);
        });
        ((NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!)
            .ValidateAgentRuns();
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        Action<NhAssistantLimits>? limits = null,
        NhAiScriptedChatClient? model = null)
    {
        var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model ?? new NhAiScriptedChatClient(),
            limits,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestRuleScript>();
                services.AddSingleton<TestSkillRuleScript>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddSubjectType<OrderSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddAgent(AssistantTestData.Agent(Planner) with { ToolSelectors = ["projects.*", "assistant.handoff.*"] })
                .AddAgent(AssistantTestData.Agent(Closer) with { ToolSelectors = ["projects.*", "assistant.handoff.*"] })
                .AddAgent(AssistantTestData.Agent(Auditor))
                .AddAgent(AssistantTestData.Agent(ShadowPlanner) with { ToolSelectors = ["projects.*", "assistant.handoff.*"] })
                .ConfigureAgent(Planner, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>())
                .ConfigureAgent(Closer, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestSkillRules>()
                    .AcceptHandOffs("project", handOff => handOff.From(Planner)))
                .ConfigureAgent(Auditor, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>())
                .ConfigureAgent(ShadowPlanner, agent => agent
                    .Shadow()
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>())
                .UseHandOffsAsData(handOffs => handOffs
                    .Allow("order", rule => rule
                        .To(Closer)
                        .ToAgentsCreatedInAdministration()
                        .From(Planner, ShadowPlanner))
                    .Allow("project", rule => rule
                        .To(Auditor)
                        .ToAgentsCreatedInAdministration()
                        .From(Closer)
                        .FromAgentsCreatedInAdministration())
                    .MaxPerSubjectPerDay(10))
                .AddTrigger<ProjectChanged>("plan-changed", trigger => trigger
                    .StartAgent(Planner)
                    .When(evt => evt.Change.StartsWith("plan", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("model-changed", trigger => trigger
                    .StartAgent(Planner)
                    .When(evt => evt.Change.StartsWith("model", StringComparison.Ordinal))
                    .UseModel()
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("shadow-changed", trigger => trigger
                    .StartAgent(ShadowPlanner)
                    .When(evt => evt.Change.StartsWith("shadow", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue)));

        // Two agents created in the administration: the order desk works on projects only and has run
        // settings; the chat-only agent has none.
        await using var scope = host.Services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        Assert.True((await administration.CreateAsync(
            new NhAssistantAgentInput(
                OrderDesk,
                "Order desk",
                "Handles orders.",
                "Look after the orders of a project.",
                ["projects.*"],
                [],
                null,
                NhAiAutonomyLevel.Execute,
                WorksOn: NhAssistantAgentWorksOn.FromInput(["project"], null, null, null, null, null)),
            Admin,
            CancellationToken.None)).Success);
        Assert.True((await administration.CreateAsync(
            new NhAssistantAgentInput(ChatOnly, "Chat only", "Only chats.", "Answer questions.", ["projects.*"], [], null, NhAiAutonomyLevel.Execute),
            Admin,
            CancellationToken.None)).Success);
        Assert.True((await scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>().SaveRunSettingsAsync(
            OrderDesk,
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent),
            Admin)).Success);
        return host;
    }

    private static NhAssistantDataHandOffRequest Request(IReadOnlyList<string> sources)
    {
        return new NhAssistantDataHandOffRequest(sources);
    }

    private static Task<TaskResult<NhAssistantDataHandOffDto>> SaveAsync(
        AssistantTestHost host,
        string agentId,
        string subjectType,
        NhAssistantDataHandOffRequest request)
    {
        return HandOffsAsync(host, handOffs => handOffs.SaveAsync(agentId, subjectType, request, Admin));
    }

    private static async Task<T> HandOffsAsync<T>(AssistantTestHost host, Func<INhAssistantDataHandOffs, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantDataHandOffs>());
    }

    private static AssistantAgentHandOffDefinition Row(
        string agentId,
        string subjectType,
        IReadOnlyList<string> sources,
        Action<AssistantAgentHandOffDefinition>? configure = null)
    {
        var row = new AssistantAgentHandOffDefinition
        {
            AgentId = agentId,
            SubjectType = subjectType,
            SourcesJson = NhAssistantDataHandOffSources.Write(sources),
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = Admin
        };
        configure?.Invoke(row);
        return row;
    }

    private static async Task StoreAsync(NhAssistantRunConfigurationStore store, AssistantAgentHandOffDefinition row)
    {
        Assert.True((await store.SaveHandOffAsync(row, null, CancellationToken.None)).Succeeded);
    }

    private static async Task RunPlannerAsync(AssistantTestHost host, string change)
    {
        var source = Assert.Single(await PublishAsync(host, change));
        await ExecuteAsync(host, source.RunId!.Value);
    }

    private static async Task RunModelAsync(AssistantTestHost host)
    {
        var source = Assert.Single(await PublishAsync(host, "model check"));
        await ExecuteAsync(host, source.RunId!.Value);
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, string change)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), change));
        return receipts.Where(receipt => receipt.Outcome != NhAssistantTriggerOutcomes.Ignored).ToArray();
    }

    private static async Task ExecuteAsync(AssistantTestHost host, Guid runId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var operation = ActivatorUtilities.CreateInstance<NhAssistantAgentRunOperation>(scope.ServiceProvider);
        var context = Substitute.For<INhBackgroundOperationContext>();
        context.AttemptNumber.Returns(1);
        await operation.ExecuteAsync(new NhAssistantAgentRunRequest(runId), context, CancellationToken.None);
    }

    private static NhAssistantRunStore Runs(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }

    private static NhAssistantRegistrationState State(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRegistrationState>();
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The one field a validation failure names, with the error it expects.
    /// </summary>
    private static string Field(TaskResult result, string error)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        var field = Assert.Single(NhAssistantResults.FieldErrorsOf(result));
        Assert.Equal([error], field.Value);
        return field.Key;
    }
}
