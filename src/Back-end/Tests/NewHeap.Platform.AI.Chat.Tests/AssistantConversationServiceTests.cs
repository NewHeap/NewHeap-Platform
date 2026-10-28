using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB5: a person's chat through the application's own channel. <see cref="INhAssistantConversations"/>
/// creates, reads and stops it and <see cref="INhAssistantConversationTurns.AnswerAsync"/> answers its
/// question, with the endpoints' checks and codes; every turn names its conversation and channel on the
/// invocation context, and <see cref="INhAiTurnPerson"/> names its person, to turn context providers,
/// tool policies and tools alike, in a turn of the endpoints and in a turn of the channel. On both
/// relational providers.
/// </summary>
public sealed class AssistantConversationServiceTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ChannelCode = "messaging-chat";
    private const string Issuer = "https://identity.example.test";
    private const string MemberName = "Mia";
    private const string Question = "Which status should the launch project get?";

    private static readonly string[] Choices = ["Active", "On hold", "Closed"];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_channel_creates_reads_and_stops_a_persons_chat(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } });
        await using var app = await StartAsync(provider, model);
        var person = Person("user-1");

        var created = await CreateAsync(app.Services, person, new NhAssistantNewConversation(AgentId) { Title = "  From the messaging app  " });

        Assert.True(created.Success);
        var chat = created.Data!;
        Assert.Equal(
            (AgentId, "From the messaging app", NhAssistantConversationStatuses.Idle, "user-1", NhAssistantConversationKinds.Chat),
            (chat.AgentId, chat.Title, chat.Status, chat.CurrentActorId, chat.Kind));
        Assert.Empty(chat.Messages);
        // The person's web client reads the same chat.
        using var client = app.CreateClient();
        using (var portal = await client.GetAsync($"/api/assistant/conversations/{chat.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        }

        var sent = await SendAsync(app.Services, person, chat.Id, "Activate the launch project.");
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, sent.Data!.Status);

        var waiting = await GetAsync(app.Services, person, chat.Id);
        Assert.True(waiting.Success);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, waiting.Data!.Status);
        var approval = Assert.IsType<NhAssistantApprovalPartDto>(waiting.Data.PendingApproval);
        Assert.Equal(sent.Data.PendingApprovals.Single().ApprovalId, approval.ApprovalId);
        Assert.Equal(2, waiting.Data.Messages.Count);

        var cancelled = await CancelAsync(app.Services, person, chat.Id);

        Assert.True(cancelled.Success);
        var stopped = await GetAsync(app.Services, person, chat.Id);
        Assert.Equal(NhAssistantConversationStatuses.Idle, stopped.Data!.Status);
        Assert.Null(stopped.Data.PendingApproval);
        Assert.Empty(app.Tools.StatusChanges);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approval.ApprovalId, CancellationToken.None);
            Assert.Equal(NhAssistantApprovalStatuses.Rejected, stored!.Status);
        }
        // A chat without a running or waiting turn stops as it is.
        Assert.True((await CancelAsync(app.Services, person, chat.Id)).Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_services_refuse_another_person_an_agent_thread_and_a_person_without_access(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } });
        await using var app = await StartAsync(provider, model);
        var owner = Person("user-1");
        var chat = (await CreateAsync(app.Services, owner, new NhAssistantNewConversation(AgentId))).Data!;
        var thread = await AddThreadAsync(app, participant: "user-1");
        await AddParticipantAsync(app.Services, chat.Id, "user-2", MemberName);

        // Creating: the agent, the title, the access policy and the agent's own policy.
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, CodeOf(await CreateAsync(app.Services, owner, new NhAssistantNewConversation("unknown-agent"))));
        Assert.Equal(NhAssistantErrorCodes.TitleInvalid, CodeOf(await CreateAsync(app.Services, owner, new NhAssistantNewConversation(AgentId) { Title = new string('t', 201) })));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await CreateAsync(app.Services, PersonWithoutAccess("user-1"), new NhAssistantNewConversation(AgentId))));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await CreateAsync(app.Services, owner, new NhAssistantNewConversation("project-manager"))));
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(await CreateAsync(app.Services, new NhAssistantDecidingPerson(owner.User, "user-2", null), new NhAssistantNewConversation(AgentId))));

        // Reading and stopping: someone outside the chat finds nothing, nor does anyone find an agent
        // thread, even one they take part in; without the access policy the assistant is forbidden.
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await GetAsync(app.Services, Person("user-3"), chat.Id)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await GetAsync(app.Services, owner, thread)));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(await GetAsync(app.Services, PersonWithoutAccess("user-1"), chat.Id)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await CancelAsync(app.Services, Person("user-3"), chat.Id)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await CancelAsync(app.Services, owner, thread)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await AnswerAsync(app.Services, owner, thread, Guid.NewGuid(), 0)));
        Assert.True((await GetAsync(app.Services, Person("user-2"), chat.Id)).Success);

        // A participant may not stop the owner's waiting turn; it keeps waiting.
        await SendAsync(app.Services, owner, chat.Id, "Activate the launch project.");
        Assert.Equal(NhAssistantErrorCodes.TurnForbidden, CodeOf(await CancelAsync(app.Services, Person("user-2"), chat.Id)));
        var still = await GetAsync(app.Services, owner, chat.Id);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, still.Data!.Status);
        Assert.NotNull(still.Data.PendingApproval);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_participant_of_another_tenant_finds_no_chat_through_the_services(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(
            provider,
            new NhAiScriptedChatClient(),
            configure: services => services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaims(Issuer)));
        var owner = new NhAssistantDecidingPerson(TenantPrincipal("owner-1", "tenant-a"), ActorOf("owner-1"), "tenant-a");
        var created = await CreateAsync(app.Services, owner, new NhAssistantNewConversation(AgentId));
        Assert.True(created.Success);
        var chat = created.Data!;
        var actor = ActorOf("subject-1");
        await AddParticipantAsync(app.Services, chat.Id, actor, "Participant");
        var ofTenant = new NhAssistantDecidingPerson(TenantPrincipal("subject-1", "tenant-a"), actor, "tenant-a");
        var ofOtherTenant = new NhAssistantDecidingPerson(TenantPrincipal("subject-1", "tenant-b"), actor, "tenant-b");

        Assert.True((await GetAsync(app.Services, ofTenant, chat.Id)).Success);
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await GetAsync(app.Services, ofOtherTenant, chat.Id)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await CancelAsync(app.Services, ofOtherTenant, chat.Id)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(await AnswerAsync(app.Services, ofOtherTenant, chat.Id, Guid.NewGuid(), 0)));
        // A tenant the principal does not carry is refused before anything is read.
        var claimed = new NhAssistantDecidingPerson(TenantPrincipal("subject-1", "tenant-b"), actor, "tenant-a");
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(await GetAsync(app.Services, claimed, chat.Id)));
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().FindConversationAsync(chat.Id, owner.ActorId, CancellationToken.None);
        Assert.Equal("tenant-a", stored!.TenantId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_channel_answers_the_question_in_the_answer_endpoints_order(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices, addressee = MemberName }, "call-ask")
            .RespondWithText("Mia chose to put it on hold.")
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices }, "call-ask-again");
        var clock = new AssistantTestClock(new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero));
        // Questions are a run setting of the agent (AllowQuestions), which a host without background
        // operations refuses at start; the test host does not start.
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services.AddSingleton<TimeProvider>(clock),
            assistantBuilder: assistant => assistant.ConfigureAgent(AgentId, agent => agent.AllowQuestions()));
        var owner = Person(AssistantTestHost.UserId);
        var member = Person("user-2");
        var chat = (await CreateAsync(host.Services, owner, new NhAssistantNewConversation(AgentId))).Data!;
        await AddParticipantAsync(host.Services, chat.Id, "user-2", MemberName);

        var asking = await SendAsync(host.Services, owner, chat.Id, "Ask Mia what happens to the launch project.");

        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, asking.Data!.Status);
        Assert.Empty(asking.Data.PendingApprovals);
        var question = asking.Data.PendingQuestion!;
        Assert.Equal(("user-2", NhAssistantQuestionStatuses.Pending), (question.AddresseeActorId, question.Status));
        Assert.Equal(Choices, question.Choices);

        // Not found, another person (before the status), a choice outside the question: nothing resumes.
        Assert.Equal(NhAssistantErrorCodes.QuestionNotFound, CodeOf(await AnswerAsync(host.Services, member, chat.Id, Guid.NewGuid(), 0)));
        Assert.Equal(NhAssistantErrorCodes.QuestionForbidden, CodeOf(await AnswerAsync(host.Services, owner, chat.Id, question.QuestionId, 0)));
        Assert.Equal(NhAssistantErrorCodes.QuestionAnswerInvalid, CodeOf(await AnswerAsync(host.Services, member, chat.Id, question.QuestionId, 3)));

        var answered = await AnswerAsync(host.Services, member, chat.Id, question.QuestionId, 1);

        Assert.True(answered.Success);
        Assert.Equal((NhAssistantTurnStatuses.Completed, asking.Data.TurnId), (answered.Data!.Status, answered.Data.TurnId));
        Assert.Null(answered.Data.PendingQuestion);
        Assert.Equal("Mia chose to put it on hold.", TextOf(answered.Data.Message));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionAnswered
            && evt.InvocationId == question.QuestionId
            && evt.ActorId == "user-2"
            && evt.Channel == ChannelCode);
        Assert.Equal(NhAssistantErrorCodes.QuestionNotPending, CodeOf(await AnswerAsync(host.Services, member, chat.Id, question.QuestionId, 1)));

        // A question whose wait passed on the application's clock closes as expired and frees the chat.
        var again = (await SendAsync(host.Services, owner, chat.Id, "Ask me instead.")).Data!.PendingQuestion!;
        clock.Advance(new NhAssistantLimits().ApprovalLifetime);

        Assert.Equal(NhAssistantErrorCodes.QuestionExpired, CodeOf(await AnswerAsync(host.Services, owner, chat.Id, again.QuestionId, 0)));
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await GetAsync(host.Services, owner, chat.Id)).Data!.Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.QuestionExpired
            && evt.InvocationId == again.QuestionId
            && evt.Channel == ChannelCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Providers_policies_and_tools_see_the_conversation_the_channel_and_the_person(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Found it in the portal.")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Found it in the messaging app.");
        await using var app = await StartAsync(provider, model);
        var seen = app.Services.GetRequiredService<SeenTurns>();
        using var client = app.CreateClient();
        var portalChat = Guid.Parse(await CreateConversationAsync(client));
        var channelChat = (await CreateAsync(app.Services, Person("user-1"), new NhAssistantNewConversation(AgentId))).Data!.Id;

        // A turn of the endpoints: the request's user, and no channel.
        using (var response = await client.PostAsync(
                   $"/api/assistant/conversations/{portalChat}/messages",
                   Json(new { text = "Find the roadmap project.", clientMessageId = "portal-1" })))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await ReadAllAsync(response);
        }
        // A turn of the channel: the person the channel vouched for, and its channel.
        var sent = await SendAsync(app.Services, Person("user-1"), channelChat, "Find the roadmap project.");
        Assert.Equal(NhAssistantTurnStatuses.Completed, sent.Data!.Status);

        Assert.Equal(
            [new Seen(portalChat, null, "user-1", "test"), new Seen(channelChat, ChannelCode, "user-1", ChannelCode)],
            seen.Providers.ToArray());
        Assert.Equal(
            [new Seen(null, null, "user-1", "test"), new Seen(null, null, "user-1", ChannelCode)],
            seen.Policies.ToArray());
        Assert.Equal(
            [(portalChat, null), (channelChat, ChannelCode)],
            app.Tools.Contexts.Select(context => (context.ConversationId, context.Channel)).ToArray());

        // Outside a person's turn nobody is named.
        var outside = app.Services.GetRequiredService<INhAiTurnPerson>();
        Assert.Equal((null, null, null), (outside.User, outside.ActorId, outside.TenantId));
    }

    private Task<AssistantWebApplication> StartAsync(
        AssistantTestProvider provider,
        IChatClient model,
        Action<IServiceCollection>? configure = null)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            provider: provider,
            configure: services =>
            {
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
                services.AddSingleton<SeenTurns>();
                services.AddSingleton<IAuthorizationHandler, TurnPersonRequirementHandler>();
                // The search tool's policy records whose turn it authorizes.
                services.AddAuthorization(options => options.AddPolicy(
                    TestProjectToolCatalog.ReadPolicy,
                    policy => policy.AddRequirements(new TurnPersonRequirement())));
                configure?.Invoke(services);
            },
            assistantBuilder: assistant => assistant.UseTurnContextProvider<RecordingTurnContextProvider>());
    }

    private static async Task<TaskResult<NhAssistantConversationDto>> CreateAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        NhAssistantNewConversation conversation)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().CreateAsync(person, conversation);
    }

    private static async Task<TaskResult<NhAssistantConversationDto>> GetAsync(IServiceProvider services, NhAssistantDecidingPerson person, Guid conversationId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().GetAsync(person, conversationId);
    }

    private static async Task<TaskResult> CancelAsync(IServiceProvider services, NhAssistantDecidingPerson person, Guid conversationId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversations>().CancelAsync(person, conversationId);
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> SendAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        string text)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().SendAsync(
            person,
            conversationId,
            new NhAssistantConversationMessage(text),
            new NhAssistantConversationTurnOptions(ChannelCode));
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> AnswerAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        Guid questionId,
        int choice)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().AnswerAsync(
            person,
            conversationId,
            questionId,
            new NhAssistantConversationAnswer(choice),
            new NhAssistantConversationTurnOptions(ChannelCode));
    }

    private static async Task AddParticipantAsync(IServiceProvider services, Guid conversationId, string actorId, string displayName)
    {
        await using var scope = services.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().TryAddParticipantAsync(
            new AssistantConversationParticipant
            {
                ConversationId = conversationId,
                ActorId = actorId,
                DisplayName = displayName,
                JoinedVia = NhAssistantParticipantSources.Link,
                JoinedAt = DateTimeOffset.UtcNow
            },
            5,
            CancellationToken.None);
        Assert.Equal(NhAssistantParticipantAddResult.Added, added);
    }

    /// <summary>
    /// An agent's thread about a project, in which <paramref name="participant"/> takes part, so only its
    /// kind keeps the services out.
    /// </summary>
    private static async Task<Guid> AddThreadAsync(AssistantWebApplication app, string participant)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            Kind = NhAssistantConversationKinds.Thread,
            OwnerActorId = "agent:" + AgentId,
            AgentId = AgentId,
            AgentVersion = 1,
            Title = "Project",
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        var thread = new AssistantThread
        {
            ConversationId = conversation.Id,
            ScopeKey = NhAiSubjectInfo.CreateScopeKey(null, null),
            AgentId = AgentId,
            SubjectType = "project",
            SubjectId = Guid.NewGuid().ToString("N"),
            Accountability = NhAssistantAccountabilities.Agent,
            AccountableOwnerId = "agent:" + AgentId,
            MemoryConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await new NhAssistantRunStore(app.Services.GetRequiredService<NhAssistantDbContextFactory>())
            .AddThreadAsync(conversation, thread, CancellationToken.None);
        await AddParticipantAsync(app.Services, conversation.Id, participant, "Participant");
        return conversation.Id;
    }

    private static async Task<string> CreateConversationAsync(HttpClient client)
    {
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = AgentId }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// The person as the application's channel authenticated them, with the assistant's access permission.
    /// </summary>
    private static NhAssistantDecidingPerson Person(string user)
    {
        return new NhAssistantDecidingPerson(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user), new Claim("permission", "assistant")], ChannelCode)),
            user,
            null);
    }

    private static NhAssistantDecidingPerson PersonWithoutAccess(string user)
    {
        return new NhAssistantDecidingPerson(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], ChannelCode)),
            user,
            null);
    }

    private static ClaimsPrincipal TenantPrincipal(string subject, string tenant)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", Issuer),
                new Claim("sub", subject),
                new Claim("tenant_id", tenant),
                new Claim("permission", "assistant")
            ],
            ChannelCode));
    }

    private static string ActorOf(string subject)
    {
        return "oidc:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Issuer + "\n" + subject)));
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static string TextOf(NhAssistantMessageDto? message)
    {
        return string.Concat(message?.Parts.OfType<NhAssistantTextPartDto>().Select(part => part.Text) ?? []);
    }

    /// <summary>
    /// What a turn context provider or a tool policy saw: the context's conversation and channel, and the
    /// turn person's actor and the authentication type of their principal.
    /// </summary>
    private sealed record Seen(Guid? ConversationId, string? Channel, string? ActorId, string? AuthenticationType);

    private sealed class SeenTurns
    {
        public ConcurrentQueue<Seen> Providers { get; } = new();

        public ConcurrentQueue<Seen> Policies { get; } = new();
    }

    private sealed class RecordingTurnContextProvider(SeenTurns seen, INhAiTurnPerson person) : INhAssistantTurnContextProvider
    {
        public ValueTask<IReadOnlyList<NhAssistantContextFact>> GetFactsAsync(NhAiInvocationContext context, CancellationToken cancellationToken)
        {
            seen.Providers.Enqueue(new Seen(context.ConversationId, context.Channel, person.ActorId, person.User?.Identity?.AuthenticationType));
            return ValueTask.FromResult<IReadOnlyList<NhAssistantContextFact>>([]);
        }
    }

    private sealed record TurnPersonRequirement : IAuthorizationRequirement;

    /// <summary>
    /// The search tool's policy: it reads whose turn it authorizes from <see cref="INhAiTurnPerson"/>,
    /// as an application's policy that needs the person beyond the principal it is given.
    /// </summary>
    private sealed class TurnPersonRequirementHandler(SeenTurns seen, INhAiTurnPerson person) : AuthorizationHandler<TurnPersonRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TurnPersonRequirement requirement)
        {
            seen.Policies.Enqueue(new Seen(null, null, person.ActorId, person.User?.Identity?.AuthenticationType));
            if (context.User.Identity?.IsAuthenticated == true)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
