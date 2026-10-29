using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
/// FB25: a person's chat about a subject. <c>POST conversations</c> and
/// <see cref="NhAssistantNewConversation.Subject"/> name it; the subject's access resolver, the tenant and
/// what the agent works on decide when the chat is created, and the resolver again on every turn, read,
/// invitation and live delivery, so revoked access takes effect at once. The chat keeps the subject and
/// offers it to tools, the file source, proposal guards and the briefing, without becoming the agent's
/// thread. An agent offered only for chats about a subject (<c>RequireChatSubject()</c>) is listed by
/// <c>GET agents?subjectType=&amp;subjectId=</c> only. Subject retention forgets the chats about a subject
/// in its scope. On both relational providers, since the chat's columns persist.
/// </summary>
public sealed class AssistantChatSubjectTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Channel = "messaging-chat";
    private const string CaseFileType = "case-file";
    private const string InvoiceType = "invoice";
    private const string CaseAgent = "case-assistant";
    private const string InvoiceAgent = "invoice-assistant";
    private const string GeneralAgent = "project-assistant";
    private const string FilesProfile = "subject-files";

    private static readonly byte[] NotePdf = Encoding.UTF8.GetBytes("%PDF-1.7\nThe hearing moves to May.\n%%EOF");

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chat_about_a_subject_is_checked_when_it_is_created_and_keeps_the_subject(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var cases = app.Services.GetRequiredService<ChatSubjectCases>();
        var reading = cases.Add(members: [("user-1", NhAiSubjectRole.Reader)]);
        using var client = app.CreateClient("user-1");

        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = CaseAgent, subjectType = CaseFileType, subjectId = reading.Id }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            var subject = document.RootElement.GetProperty("subject");
            Assert.Equal(CaseFileType, subject.GetProperty("subjectType").GetString());
            Assert.Equal(reading.Id, subject.GetProperty("subjectId").GetString());
            Assert.Equal(reading.Name, subject.GetProperty("subjectDisplayName").GetString());
            Assert.Equal("Case file", subject.GetProperty("subjectTypeName").GetString());
            Assert.Equal("/case-files/" + reading.Id, subject.GetProperty("subjectLink").GetString());
        }

        // The service creates the same chat; the row keeps the subject, its name and its scope.
        var person = Person("user-1");
        var byService = await CreateAsync(app, person, new NhAssistantNewConversation(CaseAgent) { Subject = reading.Ref });
        Assert.True(byService.Success, CodeOf(byService));
        Assert.Equal((CaseFileType, reading.Id), (byService.Data!.Subject!.SubjectType, byService.Data.Subject.SubjectId));
        await using (var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var row = await context.Conversations.AsNoTracking().SingleAsync(item => item.Id == byService.Data.Id);
            Assert.Equal(
                (CaseFileType, reading.Id, reading.Name, NhAiSubjectInfo.CreateScopeKey(null, null)),
                (row.SubjectType, row.SubjectId, row.SubjectDisplayName, row.SubjectScopeKey));
        }

        // The list shows the subject; a chat without one shows none.
        var plain = await CreateAsync(app, person, new NhAssistantNewConversation(GeneralAgent));
        Assert.Null(plain.Data!.Subject);
        using (var listed = JsonDocument.Parse(await client.GetStringAsync("/api/assistant/conversations")))
        {
            var items = listed.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(reading.Id, items.Single(item => item.GetProperty("id").GetGuid() == byService.Data.Id).GetProperty("subject").GetProperty("subjectId").GetString());
            Assert.False(items.Single(item => item.GetProperty("id").GetGuid() == plain.Data.Id).TryGetProperty("subject", out _));
        }

        // Refusals: the subject's form, its type, its existence, the tenant, the role and the agent.
        var otherTenant = cases.Add(tenantId: "tenant-b", members: [("user-1", NhAiSubjectRole.Approver)]);
        var withoutRole = cases.Add(members: [("user-2", NhAiSubjectRole.Reader)]);
        var invoice = cases.Add(type: InvoiceType, members: [("user-1", NhAiSubjectRole.Reader)]);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent, subjectType = CaseFileType }, HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectInvalid);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent, subjectId = reading.Id }, HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectInvalid);
        await AssertCreateRefusedAsync(client, new { agentId = GeneralAgent, subjectType = NhAiSubjectRef.SystemType, subjectId = NhAiSystemSubjects.GlobalId }, HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectInvalid);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent, subjectType = CaseFileType, subjectId = " padded" }, HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectInvalid);
        await AssertCreateRefusedAsync(client, new { agentId = GeneralAgent, subjectType = "contract", subjectId = reading.Id }, HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectTypeUnknown);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent, subjectType = CaseFileType, subjectId = "missing" }, HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent, subjectType = CaseFileType, subjectId = otherTenant.Id }, HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent, subjectType = CaseFileType, subjectId = withoutRole.Id }, HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        await AssertCreateRefusedAsync(client, new { agentId = InvoiceAgent, subjectType = CaseFileType, subjectId = reading.Id }, HttpStatusCode.Conflict, NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent, subjectType = InvoiceType, subjectId = invoice.Id }, HttpStatusCode.Conflict, NhAssistantErrorCodes.AgentDoesNotWorkOn);
        await AssertCreateRefusedAsync(client, new { agentId = CaseAgent }, HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectRequired);
        Assert.Equal(NhAssistantErrorCodes.SubjectRequired, CodeOf(await CreateAsync(app, person, new NhAssistantNewConversation(CaseAgent))));
        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await CreateAsync(app, person, new NhAssistantNewConversation(CaseAgent) { Subject = withoutRole.Ref })));
        Assert.Equal(NhAssistantErrorCodes.SubjectInvalid, CodeOf(await CreateAsync(app, person, new NhAssistantNewConversation(GeneralAgent) { Subject = NhAiSystemSubjects.Global })));
        // Authorization negatives: without the access policy, and the agent's policy comes first.
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await CreateAsync(app, Person("user-1", access: false), new NhAssistantNewConversation(CaseAgent) { Subject = reading.Ref })));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await CreateAsync(app, person, new NhAssistantNewConversation("project-manager") { Subject = withoutRole.Ref })));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Get_agents_lists_the_agents_of_a_chat_about_the_subject(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var cases = app.Services.GetRequiredService<ChatSubjectCases>();
        var reading = cases.Add(members: [("user-1", NhAiSubjectRole.Reader)]);
        var withoutRole = cases.Add(members: [("user-2", NhAiSubjectRole.Reader)]);
        var invoice = cases.Add(type: InvoiceType, members: [("user-1", NhAiSubjectRole.Reader)]);
        using var client = app.CreateClient("user-1");
        var person = Person("user-1");

        // Without a subject: not the agent that requires one, here and in the status.
        var plain = await AgentIdsAsync(client, "/api/assistant/agents");
        Assert.Contains(GeneralAgent, plain);
        Assert.Contains(InvoiceAgent, plain);
        Assert.DoesNotContain(CaseAgent, plain);
        using (var status = JsonDocument.Parse(await client.GetStringAsync("/api/assistant/status")))
        {
            Assert.DoesNotContain(CaseAgent, status.RootElement.GetProperty("agents").EnumerateArray().Select(agent => agent.GetProperty("id").GetString()));
        }

        // With a subject: the agents that work on it, the one that requires a subject included.
        var forCase = await AgentIdsAsync(client, $"/api/assistant/agents?subjectType={CaseFileType}&subjectId={reading.Id}");
        Assert.Equal([CaseAgent, GeneralAgent], forCase.Order(StringComparer.Ordinal).ToArray());
        var forInvoice = await AgentIdsAsync(client, $"/api/assistant/agents?subjectType={InvoiceType}&subjectId={invoice.Id}");
        Assert.Equal([InvoiceAgent, GeneralAgent], forInvoice.Order(StringComparer.Ordinal).ToArray());

        // The service lists the same agents as the endpoint.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<INhAssistantConversations>();
            var listed = await conversations.ListAgentsAsync(person, reading.Ref);
            Assert.True(listed.Success, CodeOf(listed));
            Assert.Equal(
                JsonSerializer.Serialize(listed.Data!.ToArray(), NhAssistantJsonSerializerContext.Default.NhAssistantAgentSummaryDtoArray),
                await client.GetStringAsync($"/api/assistant/agents?subjectType={CaseFileType}&subjectId={reading.Id}"));
            Assert.DoesNotContain((await conversations.ListAgentsAsync(person)).Data!, agent => agent.Id == CaseAgent);

            // Refusals and authorization negatives of the service.
            Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await conversations.ListAgentsAsync(person, withoutRole.Ref)));
            Assert.Equal(NhAssistantErrorCodes.SubjectNotFound, CodeOf(await conversations.ListAgentsAsync(person, new NhAiSubjectRef(CaseFileType, "missing"))));
            Assert.Equal(NhAssistantErrorCodes.SubjectTypeUnknown, CodeOf(await conversations.ListAgentsAsync(person, new NhAiSubjectRef("contract", "c-1"))));
            Assert.Equal(NhAssistantErrorCodes.SubjectInvalid, CodeOf(await conversations.ListAgentsAsync(person, NhAiSystemSubjects.Global)));
            Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await conversations.ListAgentsAsync(Person("user-1", access: false), reading.Ref)));
        }

        // Refusals of the endpoint.
        await AssertGetRefusedAsync(client, $"/api/assistant/agents?subjectType={CaseFileType}", HttpStatusCode.BadRequest, NhAssistantErrorCodes.SubjectInvalid);
        await AssertGetRefusedAsync(client, $"/api/assistant/agents?subjectType={CaseFileType}&subjectId={withoutRole.Id}", HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        await AssertGetRefusedAsync(client, $"/api/assistant/agents?subjectType={CaseFileType}&subjectId=missing", HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        await AssertGetRefusedAsync(client, "/api/assistant/agents?subjectType=contract&subjectId=c-1", HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectTypeUnknown);
        using var withoutAccess = app.CreateClient("user-1", access: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutAccess.GetAsync($"/api/assistant/agents?subjectType={CaseFileType}&subjectId={reading.Id}")).StatusCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Revoked_access_to_the_subject_takes_effect_on_every_turn_read_and_invitation(AssistantTestProvider provider)
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
        var chat = (await CreateAsync(app, ownerPerson, new NhAssistantNewConversation(CaseAgent) { Subject = subject.Ref })).Data!.Id;

        // With a role, the owner sends; a colleague with a role joins through the link.
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await SendAsync(app, ownerPerson, chat, "Is the case file open?")).Data!.Status);
        var token = await ShareTokenAsync(owner, chat);
        using (var joined = await participant.PostAsync($"/api/assistant/conversations/{chat}/join", Json(new { token })))
        {
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await participant.GetAsync($"/api/assistant/conversations/{chat}")).StatusCode);

        // Someone without a role neither joins through the link nor is invited or offered.
        using (var refusedJoin = await outsider.PostAsync($"/api/assistant/conversations/{chat}/join", Json(new { token })))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (refusedJoin.StatusCode, await CodeOfAsync(refusedJoin)));
        }
        using (var refusedInvite = await owner.PostAsync($"/api/assistant/conversations/{chat}/participants", Json(new { actorId = "user-3" })))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (refusedInvite.StatusCode, await CodeOfAsync(refusedInvite)));
        }
        using (var candidates = JsonDocument.Parse(await owner.GetStringAsync($"/api/assistant/conversations/{chat}/participant-candidates?query=Ca")))
        {
            Assert.Empty(candidates.RootElement.EnumerateArray());
        }
        subject.Roles["user-3"] = NhAiSubjectRole.Reader;
        using (var candidates = JsonDocument.Parse(await owner.GetStringAsync($"/api/assistant/conversations/{chat}/participant-candidates?query=Ca")))
        {
            Assert.Equal(["user-3"], candidates.RootElement.EnumerateArray().Select(entry => entry.GetProperty("actorId").GetString()!).ToArray());
        }
        using (var invited = await owner.PostAsync($"/api/assistant/conversations/{chat}/participants", Json(new { actorId = "user-3" })))
        {
            Assert.Equal(HttpStatusCode.NoContent, invited.StatusCode);
        }

        // The live read rule follows the resolver, for the owner too.
        Assert.True(await MayReadAsync(app, chat, "user-1"));
        Assert.True(await MayReadAsync(app, chat, "user-2"));

        // The participant's role ends: reading, turns and live delivery stop for them at once.
        subject.Roles.TryRemove("user-2", out _);
        await AssertGetRefusedAsync(participant, $"/api/assistant/conversations/{chat}", HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        Assert.False(await MayReadAsync(app, chat, "user-2"));
        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await GetAsync(app, Person("user-2"), chat)));

        // The owner's role ends: the owner neither reads nor continues the chat, by endpoint or service.
        subject.Roles.TryRemove("user-1", out _);
        await AssertGetRefusedAsync(owner, $"/api/assistant/conversations/{chat}", HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied);
        using (var refusedMessage = await owner.PostAsync($"/api/assistant/conversations/{chat}/messages", Json(new { text = "And now?" })))
        {
            Assert.Equal((HttpStatusCode.Forbidden, NhAssistantErrorCodes.SubjectAccessDenied), (refusedMessage.StatusCode, await CodeOfAsync(refusedMessage)));
        }
        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await GetAsync(app, ownerPerson, chat)));
        Assert.Equal(NhAssistantErrorCodes.SubjectAccessDenied, CodeOf(await SendAsync(app, ownerPerson, chat, "And now?")));
        Assert.False(await MayReadAsync(app, chat, "user-1"));
        // Marking read and sharing need the role too; stopping a turn and leaving do not.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync($"/api/assistant/conversations/{chat}/read", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync($"/api/assistant/conversations/{chat}/share-link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/assistant/conversations/{chat}/cancel", null)).StatusCode);
        Assert.True((await CancelAsync(app, ownerPerson, chat)).Success);
        // Only one model answer was used: the refused turns never reached the model.
        Assert.Single(model.Requests);

        // The role is back: the owner continues.
        subject.Roles["user-1"] = NhAiSubjectRole.Participant;
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await SendAsync(app, ownerPerson, chat, "And now?")).Data!.Status);

        // The subject is gone: the chat answers as an unknown subject, and the owner still deletes it.
        cases.Cases.TryRemove(subject.Id, out _);
        await AssertGetRefusedAsync(owner, $"/api/assistant/conversations/{chat}", HttpStatusCode.NotFound, NhAssistantErrorCodes.SubjectNotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/assistant/conversations/{chat}")).StatusCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_turn_offers_the_subject_to_tools_guards_the_file_source_and_the_briefing(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "hearing" } })
            .RespondWithText("There is a note about the hearing.")
            .RespondWithText("The note moves the hearing to May.")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "hearing" } })
            .RespondWithText("Nothing about a case file here.")
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Closed" } });
        await using var app = await StartAsync(provider, model);
        var cases = app.Services.GetRequiredService<ChatSubjectCases>();
        var seen = app.Services.GetRequiredService<SeenChatSubjects>();
        var subject = cases.Add(members: [("user-1", NhAiSubjectRole.Reader)]);
        var person = Person("user-1");
        var chat = (await CreateAsync(app, person, new NhAssistantNewConversation(CaseAgent) { Subject = subject.Ref })).Data!.Id;

        var sent = await SendAsync(app, person, chat, "What is new in this case file?");

        Assert.True(sent.Success, CodeOf(sent));
        Assert.Equal(NhAssistantTurnStatuses.Completed, sent.Data!.Status);
        // The tool sees the chat's subject; nothing makes the turn an agent's turn.
        var toolContext = Assert.Single(app.Tools.Contexts);
        Assert.Equal(subject.Ref, toolContext.ConversationSubject);
        Assert.Equal(chat, toolContext.ConversationId);
        Assert.Null(Assert.Single(app.Tools.Scopes));
        // The briefing names the subject and carries the provider's facts for a chat.
        var briefing = model.Requests[0].Messages
            .Where(message => message.Role == ChatRole.System)
            .Select(message => message.Text)
            .Single(text => text.Contains("<subject-briefing>", StringComparison.Ordinal));
        Assert.Contains("The subject of this chat", briefing, StringComparison.Ordinal);
        Assert.Contains($"(case-file {subject.Id})", briefing, StringComparison.Ordinal);
        Assert.Contains("Open since 2031-01-02.", briefing, StringComparison.Ordinal);
        Assert.Equal([NhAssistantBriefingPurposes.Chat], seen.BriefingPurposes.ToArray());

        // The file source stores an upload and opens it for the person with the chat's subject.
        await using var content = new MemoryStream(NotePdf);
        var uploaded = await UploadAsync(app, person, chat, new NhAssistantConversationUpload("Note.pdf", NhAiMediaTypes.Pdf, content));
        Assert.True(uploaded.Success, CodeOf(uploaded));
        var uploads = app.Services.GetRequiredService<TestUploads>();
        var stored = Assert.Single(uploads.Stored.Values);
        Assert.Equal((subject.Ref, (NhAiSubjectRef?)null), (stored.Actor.ConversationSubject, stored.Actor.Subject));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var withFile = await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().SendAsync(
                person,
                chat,
                new NhAssistantConversationMessage("What does the note say?") { Files = [uploaded.Data!] },
                new NhAssistantConversationTurnOptions(Channel));
            Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), (withFile.Data!.Status, withFile.Data.ErrorCode));
        }
        var opened = Assert.Single(uploads.Opened);
        Assert.Equal(("user-1", subject.Ref), (opened.Actor.ActorId, opened.Actor.ConversationSubject));

        // A chat without a subject offers none and has no briefing.
        var plain = (await CreateAsync(app, person, new NhAssistantNewConversation(GeneralAgent))).Data!.Id;
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await SendAsync(app, person, plain, "Any case file?")).Data!.Status);
        Assert.Null(app.Tools.Contexts.Last().ConversationSubject);
        Assert.DoesNotContain(
            model.Requests[^1].Messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text),
            text => text.Contains("<subject-briefing>", StringComparison.Ordinal));

        // The guard of a proposal in the chat sees its subject, which is not where the proposal waits.
        var proposed = await SendAsync(app, person, chat, "Close the project.");
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, proposed.Data!.Status);
        var guarded = Assert.Single(seen.GuardSubjects);
        Assert.Equal(subject.Ref, guarded.ConversationSubject);
        Assert.Equal(subject.Ref, guarded.Invocation!.ConversationSubject);
        Assert.Null(guarded.Subject);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Subject_retention_forgets_the_chats_about_the_subject_in_its_scope(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider);
        var cases = app.Services.GetRequiredService<ChatSubjectCases>();
        var subject = cases.Add(members: [("user-1", NhAiSubjectRole.Reader)]);
        var other = cases.Add(members: [("user-1", NhAiSubjectRole.Reader)]);
        var person = Person("user-1");
        var about = (await CreateAsync(app, person, new NhAssistantNewConversation(CaseAgent) { Subject = subject.Ref })).Data!.Id;
        var aboutOther = (await CreateAsync(app, person, new NhAssistantNewConversation(CaseAgent) { Subject = other.Ref })).Data!.Id;
        var plain = (await CreateAsync(app, person, new NhAssistantNewConversation(GeneralAgent))).Data!.Id;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<INhAiSubjectRetention>();
            // Another scope's subject with the same id keeps its chats.
            Assert.True((await retention.ForgetSubjectAsync(subject.Ref, NhAiSubjectInfo.CreateScopeKey("tenant-b", null))).Success);
            Assert.True(await ExistsAsync(app, about));

            Assert.True((await retention.ForgetSubjectAsync(subject.Ref, NhAiSubjectInfo.CreateScopeKey(null, null))).Success);
        }

        Assert.False(await ExistsAsync(app, about));
        Assert.True(await ExistsAsync(app, aboutOther));
        Assert.True(await ExistsAsync(app, plain));
    }

    [Fact]
    public void Startup_refuses_a_subject_requirement_that_offers_the_agent_nowhere()
    {
        var chatOnly = Assert.Throws<InvalidOperationException>(() => Validate(run => run.ChatOnly().RequireChatSubject()));
        Assert.Contains("is chat-only (ChatOnly), so it works on no subject", chatOnly.Message, StringComparison.Ordinal);

        var hidden = Assert.Throws<InvalidOperationException>(() => Validate(run => run.WorksOn(CaseFileType).HideFromChat().RequireChatSubject()));
        Assert.Contains("is kept out of the chat (HideFromChat), so RequireChatSubject offers it nowhere", hidden.Message, StringComparison.Ordinal);

        var accepted = Validate(run => run.WorksOn(CaseFileType).RequireChatSubject());
        Assert.True(accepted.AgentRuns[CaseAgent].RequiresChatSubject);
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
                services.AddSingleton<TestUploads>();
                // The configured agents only chat here; a host with runs would start background operations.
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(NhAssistantStartupValidator)));
                services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(FilesProfile, profile => profile
                    .UseKeyedClient("project-chat-model")
                    .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                    .WithStreaming(NhAiStreamingPolicy.Allowed)
                    .AcceptMediaTypes(NhAiMediaTypes.Pdf)
                    .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)
                    .PermitExecutionRegions("local")
                    .WithBudget(maxInputTokens: 32_000, maxOutputTokens: 1_024, maxCalls: 16)));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<CaseFileResolver>(type => type
                    .Named(new() { ["en"] = "Case file" }, "en")
                    .OpenAt("/case-files/{id}"))
                .AddSubjectType<InvoiceResolver>()
                .AddSubjectBriefing<CaseFileBriefing>()
                .AddProposalGuard<ChatSubjectGuard>(
                    [TestProjectToolCatalog.ChangeStatus.Id],
                    NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal)
                .UseParticipantDirectory<ChatSubjectDirectory>()
                .UseFileSource<TestUploadFileSource>()
                .ConfigureUploads(uploads => uploads.UseScanner<TestUploadScanner>())
                .AddAgent(AssistantTestData.Agent(CaseAgent) with { ProfileName = FilesProfile })
                .AddAgent(AssistantTestData.Agent(InvoiceAgent))
                .ConfigureAgent(CaseAgent, run => run.WorksOn(CaseFileType).RequireChatSubject().AcceptsFiles())
                .ConfigureAgent(InvoiceAgent, run => run.WorksOn(InvoiceType)),
            provider: provider);
    }

    private static NhAssistantRegistrationState Validate(Action<NhAssistantAgentRunBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant => assistant
            .UsePostgreSql("Host=localhost;Database=nh_assistant_chat_subject", options => options.Schema = "nhai")
            .UseChatProfile("project-chat")
            .AddSubjectType<CaseFileResolver>()
            .AddAgent(AssistantTestData.Agent(CaseAgent))
            .ConfigureAgent(CaseAgent, configure));
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

    private static async Task<TaskResult<NhAssistantConversationDto>> GetAsync(AssistantWebApplication app, NhAssistantDecidingPerson person, Guid chat)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().GetAsync(person, chat);
    }

    private static async Task<TaskResult> CancelAsync(AssistantWebApplication app, NhAssistantDecidingPerson person, Guid chat)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().CancelAsync(person, chat);
    }

    private static async Task<TaskResult<NhAiFileReference>> UploadAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        Guid chat,
        NhAssistantConversationUpload upload)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().UploadAsync(person, chat, upload);
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

    private static async Task<bool> ExistsAsync(AssistantWebApplication app, Guid conversationId)
    {
        await using var context = app.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Conversations.AnyAsync(conversation => conversation.Id == conversationId);
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
}

/// <summary>
/// The subjects of the test application, by id, with their tenant, name and people's roles, which a
/// test changes to revoke access.
/// </summary>
public sealed class ChatSubjectCases
{
    public ConcurrentDictionary<string, ChatSubjectCase> Cases { get; } = new(StringComparer.Ordinal);

    public ChatSubjectCase Add(
        string type = "case-file",
        string? tenantId = null,
        params (string ActorId, NhAiSubjectRole Role)[] members)
    {
        var id = Guid.NewGuid().ToString("N");
        var subject = new ChatSubjectCase(new NhAiSubjectRef(type, id), tenantId, "Case " + id[..6]);
        foreach (var (actorId, role) in members)
        {
            subject.Roles[actorId] = role;
        }

        Cases[id] = subject;
        return subject;
    }
}

public sealed class ChatSubjectCase(NhAiSubjectRef subject, string? tenantId, string name)
{
    public NhAiSubjectRef Ref { get; } = subject;

    public string Id => Ref.Id;

    public string? TenantId { get; } = tenantId;

    public string Name { get; } = name;

    public ConcurrentDictionary<string, NhAiSubjectRole> Roles { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// What the chat's guard and briefing provider were given.
/// </summary>
public sealed class SeenChatSubjects
{
    public ConcurrentQueue<NhAiProposalGuardContext> GuardSubjects { get; } = new();

    public ConcurrentQueue<string> BriefingPurposes { get; } = new();
}

public abstract class ChatSubjectResolver(ChatSubjectCases cases, string subjectType) : INhAiSubjectAccessResolver
{
    public string SubjectType { get; } = subjectType;

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        if (!cases.Cases.TryGetValue(subject.Id, out var found) || found.Ref != subject)
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(null);
        }

        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(found.TenantId, null))
        {
            TenantId = found.TenantId,
            DisplayName = found.Name
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(cases.Cases.TryGetValue(subject.Id, out var found)
            && found.Ref == subject
            && found.Roles.TryGetValue(actorId, out var role)
                ? new NhAiSubjectAccess(role)
                : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (!cases.Cases.TryGetValue(subject.Id, out var found) || found.Ref != subject)
        {
            yield break;
        }

        foreach (var (actorId, role) in found.Roles)
        {
            yield return new NhAiSubjectMember(actorId, role) { DisplayName = actorId };
        }
    }
}

public sealed class CaseFileResolver(ChatSubjectCases cases) : ChatSubjectResolver(cases, "case-file");

public sealed class InvoiceResolver(ChatSubjectCases cases) : ChatSubjectResolver(cases, "invoice");

/// <summary>
/// Briefs a case file with one dated fact, and records why it was asked.
/// </summary>
public sealed class CaseFileBriefing(SeenChatSubjects seen) : INhAssistantSubjectBriefingProvider
{
    public string SubjectType => "case-file";

    public ValueTask<NhAssistantSubjectBriefing?> GetBriefingAsync(NhAssistantBriefingContext context, CancellationToken cancellationToken)
    {
        seen.BriefingPurposes.Enqueue(context.Purpose);
        return ValueTask.FromResult<NhAssistantSubjectBriefing?>(
            new NhAssistantSubjectBriefing([new NhAssistantBriefingSection("Status", "Open since 2031-01-02.")]));
    }
}

/// <summary>
/// Records the guard context of a chat's proposal and lets it stand.
/// </summary>
public sealed class ChatSubjectGuard(SeenChatSubjects seen) : INhAiProposalGuard
{
    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(NhAiProposalGuardContext context, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([]);
    }

    public ValueTask<NhAiPreconditionResult> CheckAsync(NhAiProposalGuardContext context, CancellationToken cancellationToken = default)
    {
        if (context.Invocation is not null)
        {
            seen.GuardSubjects.Enqueue(context);
        }

        return ValueTask.FromResult(NhAiPreconditionResult.Satisfied);
    }
}

/// <summary>
/// The colleagues an owner may invite to a chat, whatever their role on its subject.
/// </summary>
internal sealed class ChatSubjectDirectory : INhAssistantParticipantDirectory
{
    private static readonly NhAssistantDirectoryEntry[] People =
    [
        new("user-2", "Pat from Planning"),
        new("user-3", "Casey from Cases")
    ];

    public ValueTask<IReadOnlyList<NhAssistantDirectoryEntry>> SearchAsync(NhAssistantDirectorySearch search, CancellationToken cancellationToken)
    {
        IReadOnlyList<NhAssistantDirectoryEntry> matches = People
            .Where(person => person.DisplayName.Contains(search.Query, StringComparison.OrdinalIgnoreCase))
            .Take(search.Limit)
            .ToArray();
        return ValueTask.FromResult(matches);
    }

    public ValueTask<NhAssistantDirectoryEntry?> FindAsync(NhAssistantDirectoryLookup lookup, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(People.FirstOrDefault(person => person.ActorId == lookup.ActorId));
    }
}
