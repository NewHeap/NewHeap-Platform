using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Chat settings without run settings (FB8 of the platform feedback plan on 21.0.0):
/// <c>run.ChatOnly()</c> keeps an agent's chat settings, such as its skills in chat and questions,
/// but it works on no subject, so no object's page and no subject's list shows it and every start
/// path refuses it; host start refuses every setting that only applies to runs on subjects. The chat
/// and the start paths are proved on both relational providers.
/// </summary>
public sealed class AssistantChatOnlyAgentTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ChatHelper = "chat-helper";
    private const string Runner = "project-runner";
    private const string Notes = "notes";
    private const string Group = "project-desks";
    private const string Admin = "admin-1";
    private const string Member = "member-1";

    [Fact]
    public void Startup_refuses_every_setting_of_a_chat_only_agent_that_applies_to_runs_on_subjects()
    {
        var cases = new (string Setting, Func<NhAssistantAgentRunBuilder, NhAssistantAgentRunBuilder> Configure)[]
        {
            ("WorksOn", run => run.WorksOn("project")),
            ("WorksOnAllObjects", run => run.WorksOnAllObjects()),
            ("WorksOnApplication", run => run.WorksOnApplication()),
            ("PerDivision", run => run.PerDivision()),
            ("RunPolicy", run => run.RunPolicy("app.run")),
            ("ViewPolicy", run => run.ViewPolicy("app.view")),
            ("InGroup", run => run.InGroup(Group)),
            ("AcceptHandOffs", run => run.AcceptHandOffs("project", accept => accept.From(Runner))),
            ("AllowConsultation", run => run.AllowConsultation("project")),
            ("ConsultableAsData", run => run.ConsultableAsData()),
            ("Shadow", run => run.Shadow()),
            ("ShadowOf", run => run.ShadowOf(Runner)),
            ("HideFromChat", run => run.HideFromChat()),
            ("StartsOff", run => run.StartsOff()),
            ("Enabled", run => run.Enabled(false)),
            ("Accountability", run => run.Accountability(NhAiAgentAccountability.SubjectResponsible)),
            ("ServiceAccount", run => run.ServiceAccount("service-1")),
            ("Grant", run => run.Grant("projects.*")),
            ("DelegateFromInitiator", run => run.DelegateFromInitiator()),
            ("ActWithAccountableRights", run => run.ActWithAccountableRights()),
            ("ElevatedReads", run => run.ElevatedReads(NhAiElevatedReads.Deny)),
            ("AllowGatewayReads", run => run.AllowGatewayReads()),
            ("Retry", run => run.Retry(1)),
            ("Budget", run => run.Budget(NhAssistantAgentBudget.PerDay(10, 1_000))),
            ("Triage", run => run.Triage(triage => triage.UseProfile("project-chat"))),
            ("Memory", run => run.Memory<HelperMemory>(1)),
            ("UseRuleHandler", run => run.UseRuleHandler<TestRules>())
        };

        foreach (var (setting, configure) in cases)
        {
            // The order does not matter: before ChatOnly as after it.
            foreach (var chatOnlyFirst in new[] { true, false })
            {
                var refused = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
                    .ConfigureAgent(ChatHelper, run =>
                    {
                        if (chatOnlyFirst)
                        {
                            configure(run.ChatOnly());
                        }
                        else
                        {
                            configure(run).ChatOnly();
                        }
                    })));
                Assert.Contains($"'{ChatHelper}' is chat-only", refused.Message);
                Assert.Contains(setting, refused.Message);
            }
        }
    }

    [Fact]
    public void Startup_refuses_triggers_variants_hand_offs_and_ceilings_that_name_a_chat_only_agent()
    {
        var trigger = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(ChatHelper, run => run.ChatOnly())
            .AddTrigger<WorkItemChanged>("helper-trigger", fire => fire
                .StartAgent(ChatHelper)
                .Subject(change => change.Subject)
                .Message(_ => "Changed"))));
        Assert.Contains("'helper-trigger' starts agent 'chat-helper', which is chat-only", trigger.Message);

        var variant = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(ChatHelper, run => run.ChatOnly())
            .ConfigureAgent(Runner, run => run.ShadowOf(ChatHelper))));
        Assert.Contains("shadow variant of 'chat-helper', which is chat-only", variant.Message);

        var handOffSource = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(ChatHelper, run => run.ChatOnly())
            .ConfigureAgent(Runner, run => run.WorksOn("project").AcceptHandOffs("project", accept => accept.From(ChatHelper)))));
        Assert.Contains("accepts hand-offs from 'chat-helper', which is chat-only", handOffSource.Message);

        var handOffsAsData = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(ChatHelper, run => run.ChatOnly())
            .UseHandOffsAsData(handOffs => handOffs.Allow("project", rule => rule.To(ChatHelper).FromAgentsCreatedInAdministration()))));
        Assert.Contains("UseHandOffsAsData connects agent 'chat-helper'", handOffsAsData.Message);

        var variantsAsData = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(ChatHelper, run => run.ChatOnly())
            .UseShadowVariantsAsData(variants => variants.Of(ChatHelper))));
        Assert.Contains("UseShadowVariantsAsData allows variants of 'chat-helper', which is chat-only", variantsAsData.Message);
    }

    [Fact]
    public void A_chat_only_agent_keeps_its_chat_settings_and_works_on_nothing()
    {
        var state = Validate(assistant => assistant
            .AddApprovalPolicy("helper-review", policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(1))
                .Approvers(NhAiApprovers.Initiator))
            .ConfigureAgent(ChatHelper, run => ChatSettings(run)));

        var options = state.AgentRuns[ChatHelper];
        Assert.True(options.ChatOnly);
        Assert.Empty(options.RunSettings);
        Assert.Equal((true, true, 2, 4_000L, true), (options.SkillsInChat, options.AllowQuestions, options.MaxConsultationsPerTurn, options.MaxTokensPerRun, options.AcceptsFiles));
        Assert.Equal((NhAiDataClassification.Confidential, "helper-review"), (options.DataClassification, options.ApprovalPolicyId));
        Assert.True(options.Skills.ContainsKey(Notes));

        // It works on no object type and not on the application, said on purpose.
        Assert.Equal((true, false, false, 0), (options.WorksOn.Declared, options.WorksOn.AllSubjects, options.WorksOn.Application, options.WorksOn.SubjectTypes.Count));
        Assert.False(options.WorksOn.Covers("project"));
        Assert.False(options.WorksOn.Covers(NhAiSubjectRef.SystemType));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chat_only_agent_offers_its_skills_and_questions_in_a_chat_and_is_never_started_on_its_own(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("I can take notes and ask you what you mean.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services.AddSingleton(new TestSubjects()),
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgent(AssistantTestData.Agent(ChatHelper))
                .ConfigureAgent(ChatHelper, run => run
                    .ChatOnly()
                    .Skill(Notes, skill => skill.Description("Writes notes about a project.").Tools("projects.search"))
                    .SkillsInChat()
                    .AllowQuestions()));
        var conversation = await host.CreateConversationAsync(agentId: ChatHelper);

        var turn = await host.SendAsync(conversation.Id, "What can you do?");

        // In the chat it keeps its run settings for chat: skills with use_skill and questions.
        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<Runtime.NhAssistantTurnCompletedEvent>().Status);
        var tools = model.Requests[0].Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.Contains("use_skill", tools);
        Assert.Contains("ask_question", tools);

        // It is never started by hand or by the application, and no thread exists for it.
        var project = new NhAiSubjectRef("project", Guid.NewGuid().ToString("N"));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
            var byHand = await triggers.StartAsync(ChatHelper, project, Member, "Look at this project.");
            Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (byHand.Outcome, byHand.ReasonCode));
            var application = await triggers.StartAsync(ChatHelper, NhAiSystemSubjects.Global, Admin, "Count the projects.");
            Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (application.Outcome, application.ReasonCode));
        }
        var rule = host.Services.GetRequiredService<NhAssistantWorksOnRule>();
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, rule.RefusalOf(ChatHelper, project));
        Assert.Null(await host.Services.GetRequiredService<NhAssistantRunStore>()
            .FindThreadAsync(NhAiSubjectInfo.CreateScopeKey(null, null), ChatHelper, project, CancellationToken.None));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentTriggerRejected
            && evt.AgentId == ChatHelper
            && evt.ResultCode == NhAssistantErrorCodes.AgentDoesNotWorkOn);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Every_start_path_refuses_a_chat_only_agent_and_no_subject_lists_it(AssistantTestProvider provider)
    {
        // The host keeps its startup check: a chat-only agent needs no background operations.
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services => services.AddSingleton(new TestSubjects()),
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Project desks" }, fallbackLanguage: "en"))
                .AddAgent(AssistantTestData.Agent(ChatHelper))
                .ConfigureAgent(ChatHelper, run => run
                    .ChatOnly()
                    .Skill(Notes, skill => skill.Description("Writes notes about a project."))
                    .SkillsInChat()
                    .AllowQuestions()),
            provider: provider);
        using var member = app.CreateClient(Member);
        using var admin = app.CreateClient(Admin, admin: true);
        var project = Guid.NewGuid().ToString("N");

        // No object's page and no subject's list shows it, also when asked for by id.
        var agents = await ReadJsonAsync(await member.GetAsync($"/api/assistant/subjects/project/{project}/agents"));
        Assert.DoesNotContain(agents.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("agentId").GetString() == ChatHelper);
        var work = await ReadJsonAsync(await member.GetAsync($"/api/assistant/subjects/project/{project}/work?agentId={ChatHelper}"));
        Assert.DoesNotContain(work.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("agentId").GetString() == ChatHelper);
        var lookup = await ReadJsonAsync(await member.GetAsync($"/api/assistant/threads?agentId={ChatHelper}&subjectType=project&subjectId={project}"));
        Assert.False(lookup.GetProperty("canStart").GetBoolean());

        // Every start by hand is refused with the code of what an agent works on.
        await AssertErrorAsync(
            await member.PostAsJsonAsync("/api/assistant/threads/start", new { agentId = ChatHelper, subjectType = "project", subjectId = project }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await member.PostAsJsonAsync("/api/assistant/threads/open", new { agentId = ChatHelper, subjectType = "project", subjectId = project }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);

        // The administration says so and refuses what only applies to runs on subjects.
        var listed = (await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/agents"))).EnumerateArray()
            .Single(agent => agent.GetProperty("id").GetString() == ChatHelper);
        Assert.Equal((true, false, false, 0), (
            listed.GetProperty("chatOnly").GetBoolean(),
            listed.GetProperty("worksOnAllSubjects").GetBoolean(),
            listed.GetProperty("worksOnApplication").GetBoolean(),
            listed.GetProperty("worksOnSubjectTypes").GetArrayLength()));
        var configuration = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{ChatHelper}/configuration"));
        Assert.True(configuration.GetProperty("codeRunSettings").GetProperty("chatOnly").GetBoolean());
        await AssertErrorAsync(
            await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{ChatHelper}/shadow", new { mode = "shadow" }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{ChatHelper}/triggers/helper-on-change", new { source = "event", @event = "project.changed" }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{ChatHelper}/groups", new { groups = new[] { Group } }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{ChatHelper}/replay", new { }),
            HttpStatusCode.BadRequest,
            NhAssistantErrorCodes.ReplayNotShadow);

        // An agent that may not use the assistant learns nothing more than before.
        using var anonymous = app.CreateClient(user: null, access: false);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/assistant/threads/start", new { agentId = ChatHelper, subjectType = "project", subjectId = project })).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<INhAssistantAgentDirectory>();
        Assert.True((await directory.GetAsync(ChatHelper))!.ChatOnly);
    }

    private static NhAssistantAgentRunBuilder ChatSettings(NhAssistantAgentRunBuilder run)
    {
        return run
            .ChatOnly()
            .Skill(Notes, skill => skill.Description("Writes notes about a project."))
            .SkillsInChat()
            .AllowQuestions()
            .MaxConsultationsPerTurn(2)
            .MaxTokensPerRun(4_000)
            .AcceptsFiles()
            .DataClassification(NhAiDataClassification.Confidential)
            .UseApprovalPolicy("helper-review");
    }

    /// <summary>
    /// Registers the assistant with the configuration and checks the agent runs as startup does.
    /// </summary>
    private static NhAssistantRegistrationState Validate(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant
                .UsePostgreSql("Host=localhost;Database=nh_assistant_chat_only", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddAgent(AssistantTestData.Agent(ChatHelper) with { ProfileName = "project-chat" })
                .AddAgent(AssistantTestData.Agent(Runner) with { ProfileName = "project-chat" })
                .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Project desks" }, fallbackLanguage: "en"))
                .ConfigureAgent(Runner, _ => { });
            configure(assistant);
        });
        var state = services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>();
        state.ValidateAgentRuns();
        return state;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await ReadJsonAsync(response, status);
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A memory a chat-only agent cannot have: it works on no subject.</summary>
    public sealed record HelperMemory(string Note);
}
