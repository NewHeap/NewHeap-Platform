using System.Collections.Concurrent;
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
/// Shadow variants created as data (S9 of the agent runs adoption plan): an agent created in the
/// administration names its live agent within the ceiling in code, receives every trigger of the
/// live agent, always works in shadow, never hands shadow work to a live agent and is compared with
/// the live agent as a variant in code is; one that no longer fits is out of effect, never live.
/// </summary>
public sealed class AssistantDataShadowVariantTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Planner = "project-planner";
    private const string Closer = "project-closer";
    private const string Auditor = "project-auditor";
    private const string Variant = "planner-v1";
    private const string Second = "planner-v2";
    private const string Admin = "admin-1";
    private const string Rules = "test-rules";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_data_variant_receives_the_live_agents_triggers_always_works_in_shadow_and_is_compared_with_it(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var handOffs = new ConcurrentQueue<NhAssistantHandOffReceipt>();
        host.Services.GetRequiredService<TestRuleScript>().Run = async (context, cancellationToken) =>
        {
            if (context.AgentId == Variant)
            {
                // Shadow work never starts live work, also through a hand-off as data.
                handOffs.Enqueue(await context.HandOffAsync(Closer, context.Subject.Subject, "Close it.", null, cancellationToken));
            }

            return NhAssistantRuleResult.Done("Planned.");
        };

        var created = await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(VariantRequest(Variant), Admin));
        Assert.True(created.Success);
        var agent = (await host.Services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(Variant, includeDisabled: true, CancellationToken.None))!;
        Assert.Equal((NhAssistantAgentSources.Admin, Planner, false), (agent.Source, agent.ShadowOfAgentId, agent.OfferedInChat));
        var audit = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated && evt.ObjectId == Variant);
        Assert.Equal(Planner, audit.LiveAgentId);

        // Without run settings the variant receives nothing.
        var before = Guid.NewGuid();
        Assert.Single(await PublishAsync(host, "plan alpha", before));
        Assert.Null(await ThreadOfAsync(host, Variant, before));

        await SaveRunSettingsAsync(host, Variant);
        var options = State(host).AgentRuns[Variant];
        Assert.Equal((Planner, true), (options.ShadowOfAgentId, options.Shadow));
        Assert.True((await DataHandOffsAsync(host, data => data.SaveAsync(Closer, "project", new NhAssistantDataHandOffRequest([Variant]), Admin))).Success);

        // The publisher reads the live receipt; the variant gets the same event in its shadow thread.
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var project = Guid.NewGuid();
        var live = Assert.Single(await PublishAsync(host, "plan beta", project));
        Assert.Equal(Planner, (await Runs(host).FindThreadByConversationAsync(live.ConversationId!.Value, CancellationToken.None))!.AgentId);
        var thread = (await ThreadOfAsync(host, Variant, project))!;
        Assert.True(thread.Shadow);
        var mirrored = Assert.Single(await RunsOfAsync(host, thread.ConversationId));
        var liveRun = (await Runs(host).FindRunAsync(live.RunId!.Value, CancellationToken.None))!;
        Assert.Equal((liveRun.TriggerId, liveRun.TriggerEventKey, true), (mirrored.TriggerId, mirrored.TriggerEventKey, mirrored.Shadow));
        await ExecuteAsync(host, liveRun.Id);
        await ExecuteAsync(host, mirrored.Id);
        Assert.Equal(NhAssistantRunStatuses.Completed, (await Runs(host).FindRunAsync(mirrored.Id, CancellationToken.None))!.Status);
        Assert.Equal(NhAssistantErrorCodes.HandOffShadowToLive, Assert.Single(handOffs).Code);

        // Its shadow mode cannot change, as for a variant in code.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>();
            Assert.Equal(NhAssistantErrorCodes.ShadowVariant, Code(await control.SetShadowAsync(Variant, false, Admin, "Try it live")));
            Assert.Equal(NhAssistantErrorCodes.ShadowVariant, Code(await control.SetShadowAsync(Variant, null, Admin)));
            Assert.Equal(Planner, (await control.GetAsync(Variant))!.ShadowOf);
        }

        // The evaluation pairs the runs that handled the same event, as for a variant in code.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var evaluation = scope.ServiceProvider.GetRequiredService<NhAssistantEvaluationService>();
            Assert.True(evaluation.IsVariant(Variant));
            var report = await evaluation.CompareAsync(Planner, Variant, from, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
            Assert.Equal((Planner, Variant, 1), (report.AgentId, report.VariantId, report.Runs.Paired));
        }

        // An update keeps the variant's live agent and keeps it out of the chat.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var updated = await scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>().UpdateAsync(
                new NhAssistantAgentInput(Variant, "Planner v1", "Plans strictly.", "Plan strictly.", ["projects.*"], [], null, NhAiAutonomyLevel.Execute, OfferedInChat: true)
                {
                    ShadowOf = Auditor
                },
                1,
                Admin,
                CancellationToken.None);
            Assert.True(updated.Success);
            Assert.Equal((Planner, false), (updated.Data!.ShadowOfAgentId, updated.Data.OfferedInChat));
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Creating_a_data_variant_mirrors_ShadowOf_and_one_that_no_longer_fits_is_out_of_effect_and_never_live(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);

        // Within the ceiling, of a live agent with a run configuration in effect.
        Assert.Equal(NhAssistantErrorCodes.ShadowVariantsNotAllowed, Code(await CreateAsync(host, "auditor-v1", Auditor)));
        Assert.Equal("shadowOf", Field(await CreateAsync(host, "ghost-v1", "unknown-agent"), NhAssistantFieldErrors.NotFound));
        Assert.True((await ConfigureAsync(host, configuration => configuration.CreateAgentAsync(
            new NhAssistantAgentRequest("chat-desk", "Chat desk", "Only chats.", "Answer questions.", ["projects.*"], NhAiAutonomyLevel.Execute),
            Admin))).Success);
        Assert.Equal("shadowOf", Field(await CreateAsync(host, "chat-desk-v1", "chat-desk"), NhAssistantFieldErrors.NotFound));
        Assert.Equal("shadowOf", Field(await CreateAsync(host, "self-v1", "self-v1"), NhAssistantFieldErrors.Invalid));

        Assert.True((await CreateAsync(host, Variant, Planner)).Success);
        Assert.True((await CreateAsync(host, Second, Planner)).Success);
        Assert.Equal(NhAssistantErrorCodes.ConfigurationLimit, Code(await CreateAsync(host, "planner-v3", Planner)));
        await SaveRunSettingsAsync(host, Variant);
        await SaveRunSettingsAsync(host, Second);
        // A variant is no live agent: it gets no variants of its own.
        Assert.Equal("shadowOf", Field(await CreateAsync(host, "planner-v1-v1", Variant), NhAssistantFieldErrors.Invalid));

        // Another host, or the code before a deploy, stored variants this host's code does not allow.
        await StoreVariantAsync(host, "rogue-variant", Auditor);
        await StoreVariantAsync(host, "orphan-variant", "gone-agent");
        await StoreVariantAsync(host, "planner-v9", Planner);
        await host.Services.GetRequiredService<NhAssistantRunConfigurations>().ReloadAsync(CancellationToken.None);

        var problems = host.Services.GetRequiredService<NhAssistantRunConfigurations>().Problems
            .Where(problem => problem.Kind == NhAssistantRunConfigurationProblems.AgentKind)
            .Select(problem => (problem.AgentId, problem.Code))
            .ToArray();
        Assert.Equivalent(
            new[]
            {
                ("orphan-variant", NhAssistantRunConfigurationProblems.UnknownLiveAgent),
                ("planner-v9", NhAssistantRunConfigurationProblems.TooManyVariants),
                ("rogue-variant", NhAssistantRunConfigurationProblems.OutsideCeiling)
            },
            problems,
            strict: true);

        // Out of effect means never live: the variants out of effect receive nothing, also not as
        // agents of their own.
        var agentRuns = State(host).AgentRuns;
        Assert.DoesNotContain("rogue-variant", agentRuns.Keys);
        Assert.DoesNotContain("orphan-variant", agentRuns.Keys);
        Assert.DoesNotContain("planner-v9", agentRuns.Keys);
        var project = Guid.NewGuid();
        Assert.Single(await PublishAsync(host, "plan gamma", project));
        Assert.NotNull(await ThreadOfAsync(host, Variant, project));
        Assert.NotNull(await ThreadOfAsync(host, Second, project));
        Assert.Null(await ThreadOfAsync(host, "planner-v9", project));
        Assert.Single(await PublishAsync(host, "audit delta", project));
        Assert.Null(await ThreadOfAsync(host, "rogue-variant", project));

        // The administration lists them with their problems.
        await using var scope = host.Services.CreateAsyncScope();
        var variants = (await scope.ServiceProvider.GetRequiredService<NhAssistantShadowVariants>().ListAsync(Planner, CancellationToken.None))!;
        Assert.Equal((true, 2), (variants.AllowsDataVariants, variants.MaxDataVariants));
        Assert.Equal(
            [(Variant, true, true, (string?)null), (Second, true, true, null), ("planner-v9", true, false, NhAssistantRunConfigurationProblems.TooManyVariants)],
            variants.Variants.Select(item => (item.AgentId, item.HasRunSettings, item.InEffect, item.Problem)));
        Assert.All(variants.Variants, item => Assert.Equal(NhAssistantShadowVariantSources.Data, item.Source));
        var auditor = (await scope.ServiceProvider.GetRequiredService<NhAssistantShadowVariants>().ListAsync(Auditor, CancellationToken.None))!;
        Assert.Equal((false, 0), (auditor.AllowsDataVariants, auditor.MaxDataVariants));
        Assert.Null(await scope.ServiceProvider.GetRequiredService<NhAssistantShadowVariants>().ListAsync("unknown-agent", CancellationToken.None));
    }

    [Fact]
    public void The_ceiling_names_live_agents_in_code_as_ShadowOf_requires()
    {
        Assert.Contains(
            "not configured with ConfigureAgent",
            Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant.UseShadowVariantsAsData(variants => variants.Of("unknown-agent")))).Message);
        Assert.Contains(
            "not a live agent",
            Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
                .ConfigureAgent(Auditor, agent => agent.Shadow())
                .UseShadowVariantsAsData(variants => variants.Of(Auditor)))).Message);
        Validate(assistant => assistant.UseShadowVariantsAsData(variants => variants.Of(Planner).MaxPerAgent(10)));

        var builder = new NhAssistantBuilder(new ServiceCollection(), new NhAssistantRegistrationState());
        Assert.Throws<InvalidOperationException>(() => builder.UseShadowVariantsAsData(_ => { }));
        Assert.Throws<ArgumentException>(() => builder.UseShadowVariantsAsData(variants => variants.Of()));
        Assert.Throws<ArgumentException>(() => builder.UseShadowVariantsAsData(variants => variants.Of("Planner")));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseShadowVariantsAsData(variants => variants.Of(Planner).MaxPerAgent(11)));
    }

    private static void Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .AddAgent(AssistantTestData.Agent(Planner))
                .AddAgent(AssistantTestData.Agent(Auditor))
                .ConfigureAgent(Planner, _ => { })
                .ConfigureAgent(Auditor, _ => { });
            configure(assistant);
        });
        ((NhAssistantRegistrationState)services.Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState)).ImplementationInstance!)
            .ValidateAgentRuns();
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
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
                .AddApprovalPolicy("project-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .AddRuleHandler<TestRules>(Rules, "The rules of the test.")
                .AddAgent(AssistantTestData.Agent(Planner))
                .AddAgent(AssistantTestData.Agent(Closer))
                .AddAgent(AssistantTestData.Agent(Auditor))
                .ConfigureAgent(Planner, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>())
                .ConfigureAgent(Closer, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestSkillRules>())
                .ConfigureAgent(Auditor, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")
                    .UseRuleHandler<TestRules>())
                .UseShadowVariantsAsData(variants => variants
                    .Of(Planner)
                    .OfAgentsCreatedInAdministration()
                    .MaxPerAgent(2))
                .UseHandOffsAsData(handOffs => handOffs
                    .Allow("project", rule => rule.To(Closer).FromAgentsCreatedInAdministration()))
                .AddTrigger<ProjectChanged>("plan-changed", trigger => trigger
                    .StartAgent(Planner)
                    .When(evt => evt.Change.StartsWith("plan", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Queue))
                .AddTrigger<ProjectChanged>("audit-changed", trigger => trigger
                    .StartAgent(Auditor)
                    .When(evt => evt.Change.StartsWith("audit", StringComparison.Ordinal))
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => evt.Change)
                    .OnBusy(NhAssistantTriggerConflict.Queue)));
    }

    private static NhAssistantAgentRequest VariantRequest(string variantId, string liveAgentId = Planner)
    {
        // An administrator starts from the live agent's definition and changes what the variant tries.
        return new NhAssistantAgentRequest(
            variantId,
            "Planner " + variantId,
            "Plans strictly.",
            "Plan strictly.",
            ["projects.*", "assistant.handoff.*"],
            NhAiAutonomyLevel.Execute,
            OfferedInChat: true)
        {
            ShadowOf = liveAgentId
        };
    }

    private static Task<TaskResult<NhAssistantAgentConfigurationDto>> CreateAsync(AssistantTestHost host, string variantId, string liveAgentId)
    {
        return ConfigureAsync(host, configuration => configuration.CreateAgentAsync(VariantRequest(variantId, liveAgentId), Admin));
    }

    private static async Task SaveRunSettingsAsync(AssistantTestHost host, string agentId)
    {
        var saved = await ConfigureAsync(host, configuration => configuration.SaveRunSettingsAsync(
            agentId,
            new NhAssistantAgentRunSettingsRequest(NhAssistantAccountabilities.Agent, RuleHandler: Rules),
            Admin));
        Assert.True(saved.Success);
    }

    /// <summary>
    /// Stores an agent created in the administration as a variant of <paramref name="liveAgentId"/>,
    /// with run settings, without the checks of a save.
    /// </summary>
    private static async Task StoreVariantAsync(AssistantTestHost host, string variantId, string liveAgentId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        context.Agents.Add(new AssistantAgent
        {
            Id = variantId,
            Source = NhAssistantAgentSources.Admin,
            DisplayName = variantId,
            Description = "Stored elsewhere.",
            ProfileName = "project-chat",
            Instructions = "Plan.",
            InstructionsAssetId = variantId + "-instructions",
            InstructionsHash = NhAssistantAgentCatalog.ComputeHash("Plan."),
            ToolSelectorsJson = "[\"projects.*\"]",
            HiddenFromChat = true,
            ShadowOfAgentId = liveAgentId,
            UpdatedAt = now
        });
        context.AgentRunConfigurations.Add(new AssistantAgentRunConfiguration
        {
            AgentId = variantId,
            Accountability = NhAssistantAccountabilities.Agent,
            RuleHandlerName = Rules,
            CreatedAt = now,
            CreatedBy = Admin,
            UpdatedAt = now,
            UpdatedBy = Admin,
            ConcurrencyStamp = Guid.NewGuid()
        });
        await context.SaveChangesAsync();
    }

    private static async Task<T> ConfigureAsync<T>(AssistantTestHost host, Func<INhAssistantAgentConfiguration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>());
    }

    private static async Task<T> DataHandOffsAsync<T>(AssistantTestHost host, Func<INhAssistantDataHandOffs, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantDataHandOffs>());
    }

    private static async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync(AssistantTestHost host, string change, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipts = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), projectId, change));
        return receipts.Where(receipt => receipt.Outcome != NhAssistantTriggerOutcomes.Ignored).ToArray();
    }

    private static async Task<AssistantThread?> ThreadOfAsync(AssistantTestHost host, string agentId, Guid projectId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var subjectId = projectId.ToString("N");
        return await context.Threads.SingleOrDefaultAsync(thread => thread.AgentId == agentId && thread.SubjectId == subjectId);
    }

    private static async Task<IReadOnlyList<AssistantRun>> RunsOfAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Runs.Where(run => run.ConversationId == conversationId).ToListAsync();
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

    private static string? Code(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static string Field(TaskResult result, string error)
    {
        Assert.Equal(NhAssistantAdminErrorCodes.ValidationFailed, Code(result));
        var field = Assert.Single(NhAssistantResults.FieldErrorsOf(result));
        Assert.Equal([error], field.Value);
        return field.Key;
    }
}
