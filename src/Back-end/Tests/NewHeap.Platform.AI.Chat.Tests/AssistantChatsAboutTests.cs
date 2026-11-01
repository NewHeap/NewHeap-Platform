using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB76 of the platform feedback plan on 29.0.0: <c>ChatOnly().ChatsAbout(types)</c> lets people chat
/// about an object with an agent that does not run. The object's access resolver decides as for any chat
/// about a subject, on creation and on every turn, read and invitation; the agent keeps what a chat-only
/// agent has and keeps working on nothing, so every start, trigger, hand-off and consultation stays
/// refused and it joins no board or run maintenance. Chats persist, so these run on both providers.
/// </summary>
public sealed class AssistantChatsAboutTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Channel = "messaging-chat";
    private const string CaseFileType = "case-file";
    private const string InvoiceType = "invoice";
    private const string GeneralAgent = "project-assistant";

    /// <summary>Offered only for chats about a case file.</summary>
    private const string CaseNotes = "case-notes";

    /// <summary>Offered everywhere, and for chats about a case file.</summary>
    private const string FileHelper = "file-helper";

    /// <summary>Chat-only without object types: it chats about nothing.</summary>
    private const string PlainHelper = "plain-helper";

    private const string Notes = "notes";
    private const string Group = "case-desks";

    [Fact]
    public void ChatsAbout_keeps_the_agent_chat_only_and_working_on_nothing()
    {
        // The order does not matter, repeated calls add types and a type counts once.
        var state = Validate(assistant => assistant
            .ConfigureAgent(CaseNotes, run => run
                .ChatsAbout(InvoiceType)
                .RequireChatSubject()
                .ChatOnly()
                .ChatsAbout(CaseFileType, InvoiceType)));

        var options = state.AgentRuns[CaseNotes];
        Assert.Equal([CaseFileType, InvoiceType], options.ChatsAbout);
        Assert.True(options.ChatOnly);
        Assert.True(options.RequiresChatSubject);
        Assert.Empty(options.RunSettings);

        // It says so on purpose: it works on no object type and not on the application.
        Assert.Equal((true, false, false, 0), (options.WorksOn.Declared, options.WorksOn.AllSubjects, options.WorksOn.Application, options.WorksOn.SubjectTypes.Count));
        Assert.False(options.WorksOn.Covers(CaseFileType));
        Assert.False(options.WorksOn.Covers(new NhAiSubjectRef(CaseFileType, "case-1")));
        Assert.False(options.WorksOn.Covers(NhAiSubjectRef.SystemType));
    }

    [Fact]
    public void Startup_refuses_what_ChatsAbout_cannot_mean()
    {
        // Without ChatOnly: an agent that runs chats about what it works on.
        var undeclared = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(CaseNotes, run => run.ChatsAbout(CaseFileType))));
        Assert.Contains("'case-notes' names object types to chat about (ChatsAbout) but is not chat-only", undeclared.Message, StringComparison.Ordinal);
        var runner = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(CaseNotes, run => run.WorksOn(CaseFileType).ChatsAbout(CaseFileType))));
        Assert.Contains("is not chat-only", runner.Message, StringComparison.Ordinal);

        // RequireChatSubject stays refused for a chat-only agent without it.
        var withoutTypes = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(CaseNotes, run => run.ChatOnly().RequireChatSubject())));
        Assert.Contains("is chat-only (ChatOnly), so it works on no subject", withoutTypes.Message, StringComparison.Ordinal);
        Assert.Contains("ChatsAbout", withoutTypes.Message, StringComparison.Ordinal);

        // ChatsAbout changes nothing about what a chat-only agent refuses.
        var settings = new (string Setting, Func<NhAssistantAgentRunBuilder, NhAssistantAgentRunBuilder> Configure)[]
        {
            ("WorksOn", run => run.WorksOn(CaseFileType)),
            ("WorksOnAllObjects", run => run.WorksOnAllObjects()),
            ("InGroup", run => run.InGroup(Group)),
            ("AcceptHandOffs", run => run.AcceptHandOffs(CaseFileType, accept => accept.From(GeneralAgent))),
            ("AllowConsultation", run => run.AllowConsultation(CaseFileType)),
            ("ConsultableAsData", run => run.ConsultableAsData()),
            ("Shadow", run => run.Shadow()),
            ("HideFromChat", run => run.HideFromChat()),
            ("Memory", run => run.Memory<CaseMemory>(1))
        };
        foreach (var (setting, configure) in settings)
        {
            var refused = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
                .ConfigureAgent(CaseNotes, run => configure(run.ChatOnly().ChatsAbout(CaseFileType)))));
            Assert.Contains("'case-notes' is chat-only", refused.Message, StringComparison.Ordinal);
            Assert.Contains(setting, refused.Message, StringComparison.Ordinal);
        }

        var trigger = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(CaseNotes, run => run.ChatOnly().ChatsAbout(CaseFileType))
            .AddTrigger<WorkItemChanged>("notes-trigger", fire => fire
                .StartAgent(CaseNotes)
                .Subject(change => change.Subject)
                .Message(_ => "Changed"))));
        Assert.Contains("'notes-trigger' starts agent 'case-notes', which is chat-only", trigger.Message, StringComparison.Ordinal);
        var handOffsAsData = Assert.Throws<InvalidOperationException>(() => Validate(assistant => assistant
            .ConfigureAgent(CaseNotes, run => run.ChatOnly().ChatsAbout(CaseFileType))
            .UseHandOffsAsData(handOffs => handOffs.Allow(CaseFileType, rule => rule.To(CaseNotes).FromAgentsCreatedInAdministration()))));
        Assert.Contains("UseHandOffsAsData connects agent 'case-notes'", handOffsAsData.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatsAbout_takes_registered_type_ids_of_the_application_only()
    {
        void Configure(params string[] types)
        {
            Validate(assistant => assistant.ConfigureAgent(CaseNotes, run => run.ChatOnly().ChatsAbout(types)));
        }

        Assert.Throws<ArgumentException>(() => Configure());
        Assert.Throws<ArgumentException>(() => Configure("Not A Type"));
        var platform = Assert.Throws<ArgumentException>(() => Configure(NhAiSubjectRef.SystemType));
        Assert.Contains("belongs to the platform", platform.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Configure(
            Enumerable.Range(0, NhAssistantAgentWorksOn.MaxSubjectTypes + 1).Select(index => $"type-{index}").ToArray()));
    }

    [Fact]
    public async Task A_host_refuses_a_type_without_a_resolver()
    {
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(CaseNotes))
                .ConfigureAgent(CaseNotes, run => run.ChatOnly().ChatsAbout("contract")));

        var validator = host.Services.GetServices<IHostedService>().OfType<NhAssistantStartupValidator>().Single();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("'case-notes' chats about 'contract', which has no subject access resolver", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Chats_about_a_named_type_are_listed_and_checked_when_they_are_created(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var cases = app.Services.GetRequiredService<ChatSubjectCases>();
        var reading = cases.Add(members: [("user-1", NhAiSubjectRole.Reader)]);
        var withoutRole = cases.Add(members: [("user-2", NhAiSubjectRole.Reader)]);
        var otherTenant = cases.Add(tenantId: "tenant-b", members: [("user-1", NhAiSubjectRole.Approver)]);
        var invoice = cases.Add(type: InvoiceType, members: [("user-1", NhAiSubjectRole.Reader)]);
        using var client = app.CreateClient("user-1");
        var person = Person("user-1");

        // Without a subject: not the agent that requires one, here and in the status.
        var plain = await AgentIdsAsync(client, "/api/assistant/agents");
        Assert.DoesNotContain(CaseNotes, plain);
        Assert.Contains(FileHelper, plain);
        Assert.Contains(PlainHelper, plain);
        using (var status = JsonDocument.Parse(await client.GetStringAsync("/api/assistant/status")))
        {
            var listed = status.RootElement.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("id").GetString()).ToArray();
            Assert.DoesNotContain(CaseNotes, listed);
            Assert.Contains(FileHelper, listed);
        }

        // With a subject of a named type: both agents that name it, not the one that names nothing.
        var forCase = await AgentIdsAsync(client, $"/api/assistant/agents?subjectType={CaseFileType}&subjectId={reading.Id}");
        Assert.Equal([CaseNotes, FileHelper, GeneralAgent], forCase.Order(StringComparer.Ordinal).ToArray());
        var forInvoice = await AgentIdsAsync(client, $"/api/assistant/agents?subjectType={InvoiceType}&subjectId={invoice.Id}");
        Assert.Equal([GeneralAgent], forInvoice);

        // The service lists the same, and refuses a subject the person may not use.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<INhAssistantConversations>();
            var listed = await conversations.ListAgentsAsync(person, reading.Ref);
            Assert.True(listed.Success, CodeOf(listed));
            Assert.Contains(listed.Data!, agent => agent.Id == CaseNotes);
            Assert.DoesNotContain((await conversations.ListAgentsAsync(person)).Data!, agent => agent.Id == CaseNotes);
            Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await conversations.ListAgentsAsync(person, withoutRole.Ref)));
        }

        // The chat keeps the subject, created by endpoint or service; the row carries its name and scope.
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = CaseNotes, subjectType = CaseFileType, subjectId = reading.Id }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            Assert.Equal(reading.Id, document.RootElement.GetProperty("subject").GetProperty("subjectId").GetString());
        }
        var byService = await CreateAsync(app, person, new NhAssistantNewConversation(FileHelper) { Subject = reading.Ref });
        Assert.True(byService.Success, CodeOf(byService));
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var row = await context.Conversations.AsNoTracking().SingleAsync(item => item.Id == byService.Data!.Id);
            Assert.Equal(
                (FileHelper, CaseFileType, reading.Id, reading.Name, NhAiSubjectInfo.CreateScopeKey(null, null)),
                (row.AgentId, row.SubjectType, row.SubjectId, row.SubjectDisplayName, row.SubjectScopeKey));
        }

        // An agent that does not require a subject still chats about nothing in particular.
        Assert.Null((await CreateAsync(app, person, new NhAssistantNewConversation(FileHelper))).Data!.Subject);

        // Refusals: a type not named, the agent that names nothing, the subject's existence, the tenant,
        // the role and the subject the agent requires.
        await AssertCreateRefusedAsync(client, new { agentId = CaseNotes, subjectType = InvoiceType, subjectId = invoice.Id }, HttpStatusCode.Conflict, NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertCreateRefusedAsync(client, new { agentId = PlainHelper, subjectType = CaseFileType, subjectId = reading.Id }, HttpStatusCode.Conflict, NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertCreateRefusedAsync(client, new { agentId = CaseNotes, subjectType = CaseFileType, subjectId = "missing" }, HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        await AssertCreateRefusedAsync(client, new { agentId = CaseNotes, subjectType = CaseFileType, subjectId = otherTenant.Id }, HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        await AssertCreateRefusedAsync(client, new { agentId = CaseNotes, subjectType = CaseFileType, subjectId = withoutRole.Id }, HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        await AssertCreateRefusedAsync(client, new { agentId = CaseNotes }, HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectRequired);
        await AssertCreateRefusedAsync(client, new { agentId = FileHelper, subjectType = NhAiSubjectRef.SystemType, subjectId = NhAiSystemSubjects.GlobalId }, HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectInvalid);
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, CodeOf(await CreateAsync(app, person, new NhAssistantNewConversation(CaseNotes) { Subject = invoice.Ref })));
        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await CreateAsync(app, person, new NhAssistantNewConversation(CaseNotes) { Subject = withoutRole.Ref })));
        Assert.Equal(NhAssistantErrorCodes.SubjectRequired, CodeOf(await CreateAsync(app, person, new NhAssistantNewConversation(CaseNotes))));

        // Authorization negatives: without the assistant's access policy nothing is listed or created.
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await CreateAsync(app, Person("user-1", access: false), new NhAssistantNewConversation(CaseNotes) { Subject = reading.Ref })));
        using var withoutAccess = app.CreateClient("user-1", access: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutAccess.GetAsync($"/api/assistant/agents?subjectType={CaseFileType}&subjectId={reading.Id}")).StatusCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_subject_is_checked_again_on_every_turn_read_and_invitation_and_the_chat_keeps_its_settings(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("The case file is open.")
            .RespondWithText("Still open.");
        await using var app = await StartAsync(provider, model);
        var cases = app.Services.GetRequiredService<ChatSubjectCases>();
        var subject = cases.Add(members: [("user-1", NhAiSubjectRole.Reader), ("user-2", NhAiSubjectRole.Reader)]);
        using var owner = app.CreateClient("user-1");
        using var participant = app.CreateClient("user-2");
        using var outsider = app.CreateClient("user-3");
        var ownerPerson = Person("user-1");
        var chat = (await CreateAsync(app, ownerPerson, new NhAssistantNewConversation(CaseNotes) { Subject = subject.Ref })).Data!.Id;

        // With a role the turn runs, with the agent's skills and questions and the subject's briefing.
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await SendAsync(app, ownerPerson, chat, "Is the case file open?")).Data!.Status);
        var tools = model.Requests[0].Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.Contains("use_skill", tools);
        Assert.Contains("ask_question", tools);
        Assert.Contains(NhAssistantBriefingPurposes.Chat, app.Services.GetRequiredService<SeenChatSubjects>().BriefingPurposes);

        // A colleague with a role joins through the link; someone without a role is neither let in nor invited.
        var token = await ShareTokenAsync(owner, chat);
        using (var joined = await participant.PostAsync($"/api/assistant/conversations/{chat}/join", Json(new { token })))
        {
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }
        using (var refusedJoin = await outsider.PostAsync($"/api/assistant/conversations/{chat}/join", Json(new { token })))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (refusedJoin.StatusCode, await CodeOfAsync(refusedJoin)));
        }
        using (var refusedInvite = await owner.PostAsync($"/api/assistant/conversations/{chat}/participants", Json(new { actorId = "user-3" })))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (refusedInvite.StatusCode, await CodeOfAsync(refusedInvite)));
        }
        Assert.True(await MayReadAsync(app, chat, "user-2"));

        // The participant's role ends: reading and live delivery stop at once.
        subject.Roles.TryRemove("user-2", out _);
        await AssertGetRefusedAsync(participant, $"/api/assistant/conversations/{chat}", HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        Assert.False(await MayReadAsync(app, chat, "user-2"));

        // The owner's role ends: no read and no turn, by endpoint or service, and the model is not called.
        subject.Roles.TryRemove("user-1", out _);
        await AssertGetRefusedAsync(owner, $"/api/assistant/conversations/{chat}", HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        using (var refusedMessage = await owner.PostAsync($"/api/assistant/conversations/{chat}/messages", Json(new { text = "And now?" })))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (refusedMessage.StatusCode, await CodeOfAsync(refusedMessage)));
        }
        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await SendAsync(app, ownerPerson, chat, "And now?")));
        Assert.Single(model.Requests);

        // The role is back: the owner continues.
        subject.Roles["user-1"] = NhAiSubjectRole.Participant;
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await SendAsync(app, ownerPerson, chat, "And now?")).Data!.Status);
        Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chat_refuses_its_next_turn_once_the_agent_no_longer_names_the_type(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("The case file is open.");
        await using var app = await StartAsync(provider, model);
        var subject = app.Services.GetRequiredService<ChatSubjectCases>().Add(members: [("user-1", NhAiSubjectRole.Reader)]);
        var person = Person("user-1");
        var chat = (await CreateAsync(app, person, new NhAssistantNewConversation(FileHelper) { Subject = subject.Ref })).Data!.Id;
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await SendAsync(app, person, chat, "Is the case file open?")).Data!.Status);

        // A deploy that drops the type: the chat is no longer one the agent can have.
        app.Services.GetRequiredService<NhAssistantRegistrationState>().CodeAgentRuns[FileHelper].ChatsAbout = [];

        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, CodeOf(await SendAsync(app, person, chat, "And now?")));
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Every_start_stays_refused_and_nothing_lists_the_agent_as_working_on_the_object(AssistantTestProvider provider)
    {
        const string Member = "member-1";
        const string Admin = "admin-1";

        // This host keeps its startup check: the agent runs on nothing, so it needs no background operations.
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services => services.AddSingleton(new TestSubjects()),
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Case desks" }, fallbackLanguage: "en"))
                .AddAgent(AssistantTestData.Agent(CaseNotes))
                .ConfigureAgent(CaseNotes, run => run
                    .ChatOnly()
                    .ChatsAbout("project")
                    .RequireChatSubject()
                    .Skill(Notes, skill => skill.Description("Writes notes about a project."))
                    .SkillsInChat()
                    .AllowQuestions()),
            provider: provider);
        using var member = app.CreateClient(Member);
        using var admin = app.CreateClient(Admin, admin: true);
        var project = Guid.NewGuid().ToString("N");
        var subject = new NhAiSubjectRef("project", project);

        // People may chat about the object, so the agent is in the chat's list and nowhere else.
        Assert.Contains(CaseNotes, await AgentIdsAsync(member, $"/api/assistant/agents?subjectType=project&subjectId={project}"));

        // No object's page or tab shows it, also when asked for by id, so nothing offers a start.
        var agents = await ReadJsonAsync(await member.GetAsync($"/api/assistant/subjects/project/{project}/agents"));
        Assert.DoesNotContain(agents.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("agentId").GetString() == CaseNotes);
        var work = await ReadJsonAsync(await member.GetAsync($"/api/assistant/subjects/project/{project}/work?agentId={CaseNotes}"));
        Assert.DoesNotContain(work.GetProperty("agents").EnumerateArray(), agent => agent.GetProperty("agentId").GetString() == CaseNotes);
        var lookup = await ReadJsonAsync(await member.GetAsync($"/api/assistant/threads?agentId={CaseNotes}&subjectType=project&subjectId={project}"));
        Assert.False(lookup.GetProperty("canStart").GetBoolean());

        // Every start by hand, and by the application, is refused with the code of what it works on.
        await AssertErrorAsync(
            await member.PostAsJsonAsync("/api/assistant/threads/start", new { agentId = CaseNotes, subjectType = "project", subjectId = project }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await member.PostAsJsonAsync("/api/assistant/threads/open", new { agentId = CaseNotes, subjectType = "project", subjectId = project }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var triggers = scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>();
            var started = await triggers.StartAsync(CaseNotes, subject, Member, "Look at this project.");
            Assert.Equal((NhAssistantTriggerOutcomes.Rejected, NhAssistantErrorCodes.AgentDoesNotWorkOn), (started.Outcome, started.ReasonCode));
        }

        // The one rule of what an agent works on, which triggers, starts, hand-offs and consultations ask.
        var rule = app.Services.GetRequiredService<NhAssistantWorksOnRule>();
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, rule.RefusalOf(CaseNotes, subject));
        Assert.Equal(NhAssistantErrorCodes.AgentDoesNotWorkOn, rule.RefusalOf(CaseNotes, "project"));

        // The administration refuses what only applies to runs on subjects: a board, a data trigger,
        // shadow, a hand-off and a consultation as data.
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{CaseNotes}/groups", new { groups = new[] { Group } }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{CaseNotes}/triggers/notes-on-change", new { source = "event", @event = "project.changed" }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await admin.PostAsJsonAsync($"/api/assistant/admin/agents/{CaseNotes}/shadow", new { mode = "shadow" }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{CaseNotes}/hand-offs/project", new { sources = new[] { "project-assistant" } }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.HandOffsNotAllowed);
        await AssertErrorAsync(
            await admin.PutAsJsonAsync($"/api/assistant/admin/agents/{CaseNotes}/consultations/project", new { description = "Answers about a project." }),
            HttpStatusCode.Conflict,
            NhAssistantErrorCodes.ConsultationsNotAllowed);

        // It joins no run maintenance, and no thread exists for it.
        Assert.True(app.Services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[CaseNotes].ChatOnly);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<NhAssistantRunStore>()
                .FindThreadAsync(NhAiSubjectInfo.CreateScopeKey(null, null), CaseNotes, subject, CancellationToken.None));
        }
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, NhAiScriptedChatClient? model = null)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model ?? new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<ChatSubjectCases>();
                services.AddSingleton<SeenChatSubjects>();
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<CaseFileResolver>()
                .AddSubjectType<InvoiceResolver>()
                .AddSubjectBriefing<CaseFileBriefing>()
                .UseParticipantDirectory<ChatSubjectDirectory>()
                .AddAgent(AssistantTestData.Agent(CaseNotes))
                .AddAgent(AssistantTestData.Agent(FileHelper))
                .AddAgent(AssistantTestData.Agent(PlainHelper))
                .ConfigureAgent(CaseNotes, run => run
                    .ChatOnly()
                    .ChatsAbout(CaseFileType)
                    .RequireChatSubject()
                    .Skill(Notes, skill => skill.Description("Writes notes about a case file."))
                    .SkillsInChat()
                    .AllowQuestions())
                .ConfigureAgent(FileHelper, run => run.ChatOnly().ChatsAbout(CaseFileType))
                .ConfigureAgent(PlainHelper, run => run.ChatOnly()),
            provider: provider);
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
                .UsePostgreSql("Host=localhost;Database=nh_assistant_chats_about", options => options.Schema = "nhai")
                .UseChatProfile("project-chat")
                .AddSubjectType<CaseFileResolver>()
                .AddAgent(AssistantTestData.Agent(CaseNotes) with { ProfileName = "project-chat" })
                .AddAgent(AssistantTestData.Agent(GeneralAgent) with { ProfileName = "project-chat" })
                .AddAgentGroup(Group, group => group.Named(new() { ["en"] = "Case desks" }, fallbackLanguage: "en"))
                .ConfigureAgent(GeneralAgent, _ => { });
            configure(assistant);
        });
        var state = services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>();
        state.ValidateAgentRuns();
        return state;
    }

    private static async Task<TaskResult<NhAssistantConversationDto>> CreateAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        NhAssistantNewConversation conversation)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().CreateAsync(person, conversation);
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> SendAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        Guid chat,
        string text)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().SendAsync(
            person,
            chat,
            new NhAssistantConversationMessage(text),
            new NhAssistantConversationTurnOptions(Channel));
    }

    /// <summary>
    /// Whether the rule of live updates and notifications lets <paramref name="actorId"/> read the chat now.
    /// </summary>
    private static async Task<bool> MayReadAsync(AssistantWebApplication app, Guid chat, string actorId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INhAssistantStore>();
        var conversation = (await store.FindShareableConversationAsync(chat, CancellationToken.None))!;
        var readers = scope.ServiceProvider.GetRequiredService<NhAssistantConversationReaders>();
        var rule = await readers.RuleOfAsync(conversation, CancellationToken.None);
        return await readers.MayReadAsync(rule, actorId, CancellationToken.None);
    }

    private static async Task<string> ShareTokenAsync(HttpClient owner, Guid chat)
    {
        using var response = await owner.PostAsync($"/api/assistant/conversations/{chat}/share-link", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<string[]> AgentIdsAsync(HttpClient client, string path)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(path));
        return document.RootElement.EnumerateArray().Select(agent => agent.GetProperty("id").GetString()!).ToArray();
    }

    private static async Task AssertCreateRefusedAsync(HttpClient client, object body, HttpStatusCode status, string code)
    {
        using var response = await client.PostAsync("/api/assistant/conversations", Json(body));
        Assert.Equal((status, code), (response.StatusCode, await CodeOfAsync(response)));
    }

    private static async Task AssertGetRefusedAsync(HttpClient client, string path, HttpStatusCode status, string code)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal((status, code), (response.StatusCode, await CodeOfAsync(response)));
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

    /// <summary>
    /// The person as the application's channel authenticated them.
    /// </summary>
    private static NhAssistantDecidingPerson Person(string user, bool access = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
        if (access)
        {
            claims.Add(new Claim("permission", "assistant"));
        }

        return new NhAssistantDecidingPerson(new ClaimsPrincipal(new ClaimsIdentity(claims, Channel)), user, null);
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>A memory a chat-only agent cannot have: it works on no subject.</summary>
    public sealed record CaseMemory(string Note);
}
