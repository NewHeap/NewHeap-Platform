using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Turns that outlive their client (<see cref="NhAssistantOptions.KeepTurnsAfterDisconnect"/>, LS18) and
/// a person's turns through the application's own channel (<see cref="INhAssistantConversationTurns"/>,
/// LS19): a turn completes and is stored after its client or caller left, its tools authorize the person
/// captured when it started and never the request that runs around it, and the service applies the
/// endpoints' access rules. On both relational providers.
/// </summary>
public sealed class AssistantConversationTurnTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ChannelCode = "messaging-chat";
    private const string ChannelPath = "/test/channel";
    private const string Issuer = "https://identity.example.test";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_turn_kept_after_a_disconnect_completes_and_its_tool_authorizes_the_captured_principal(AssistantTestProvider provider)
    {
        var model = new GatedChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Found the roadmap project."));
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAsync(provider, model, keepTurns: true, aborted: aborted);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);
        var endings = app.Services.GetRequiredService<TurnEndings>();
        var authorizations = app.Services.GetRequiredService<PersonAuthorizations>();

        await DisconnectDuringTurnAsync(client, id, model, aborted);
        // The client is gone; the model answers only now, so the tool runs after the disconnect.
        model.Release();
        var ended = await endings.WaitAsync(Guid.Parse(id));

        Assert.Equal(NhAssistantTurnStatuses.Completed, ended.Status);
        var authorized = Assert.Single(authorizations.Records);
        Assert.Equal(("user-1", true), (authorized.User, authorized.RequestAborted));
        Assert.Single(app.Tools.Contexts);
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        var answer = conversation.GetProperty("messages")[1];
        Assert.Equal("Found the roadmap project.", TextOf(answer));
        Assert.Equal("succeeded", ToolCallsOf(answer).Single().GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_option_a_disconnect_cancels_the_turn_as_before(AssistantTestProvider provider)
    {
        var model = new GatedChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Found the roadmap project."));
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAsync(provider, model, keepTurns: false, aborted: aborted);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        await DisconnectDuringTurnAsync(client, id, model, aborted);
        var ended = await app.Services.GetRequiredService<TurnEndings>().WaitAsync(Guid.Parse(id));

        Assert.Equal(NhAssistantTurnStatuses.Cancelled, ended.Status);
        Assert.Empty(app.Tools.Contexts);
        Assert.Empty(app.Services.GetRequiredService<PersonAuthorizations>().Records);
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Cancel_still_stops_a_turn_kept_after_a_disconnect(AssistantTestProvider provider)
    {
        var model = new GatedChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Found the roadmap project."));
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAsync(provider, model, keepTurns: true, aborted: aborted);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        await DisconnectDuringTurnAsync(client, id, model, aborted);
        using var cancel = await client.PostAsync($"/api/assistant/conversations/{id}/cancel", null);
        var ended = await app.Services.GetRequiredService<TurnEndings>().WaitAsync(Guid.Parse(id));

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal(NhAssistantTurnStatuses.Cancelled, ended.Status);
        Assert.Empty(app.Tools.Contexts);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_channel_sends_a_persons_message_and_decides_their_approval(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } })
            .RespondWithText("Done: the project is active.");
        await using var app = await StartAsync(provider, model);
        using var client = app.CreateClient();
        var id = Guid.Parse(await CreateConversationAsync(client));
        var person = Person("user-1");
        var audit = app.Services.GetRequiredService<CapturedBusinessAuditSink>();

        var sent = await SendAsync(app, person, id, new NhAssistantConversationMessage("Activate the roadmap project.") { ClientMessageId = "delivery-1" });

        Assert.True(sent.Success);
        Assert.Equal((NhAssistantTurnStatuses.WaitingForApproval, id), (sent.Data!.Status, sent.Data.ConversationId));
        var approval = Assert.Single(sent.Data.PendingApprovals);
        Assert.Equal("projects.change-status", approval.ToolId);
        Assert.Empty(app.Tools.StatusChanges);
        Assert.NotNull(sent.Data.Message);

        // A channel that delivers the same message again starts no second turn.
        var repeated = await SendAsync(app, person, id, new NhAssistantConversationMessage("Activate the roadmap project.") { ClientMessageId = "delivery-1" });
        Assert.Equal(NhAssistantErrorCodes.MessageDuplicate, CodeOf(repeated));

        var decided = await DecideAsync(app, person, id, approval.ApprovalId, new NhAssistantConversationDecision(NhAssistantProposalDecision.Approve, approval.ProposalHash));

        Assert.True(decided.Success);
        Assert.Equal(NhAssistantTurnStatuses.Completed, decided.Data!.Status);
        Assert.Empty(decided.Data.PendingApprovals);
        Assert.Equal(sent.Data.TurnId, decided.Data.TurnId);
        Assert.Equal("Done: the project is active.", string.Concat(decided.Data.Message!.Parts.OfType<NhAssistantTextPartDto>().Select(part => part.Text)));
        var change = Assert.Single(app.Tools.StatusChanges);
        Assert.Equal(projectId, change.ProjectId);
        var context = app.Tools.Contexts.Single();
        Assert.Equal(("user-1", NhAiActorKind.Agent), (context.AccountableOwnerId, context.ActorKind));

        // The audit records the channel on the turn's events, as for the decision service.
        Assert.Contains(audit.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested && evt.Channel == ChannelCode);
        Assert.Contains(audit.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalApproved && evt.Channel == ChannelCode);
        Assert.Contains(audit.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked && evt.Channel == ChannelCode);
        Assert.DoesNotContain(audit.Events, evt => evt.Channel is not null && evt.Channel != ChannelCode);

        // The person's web client reads the same conversation.
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        // The message, the answer that proposed and the answer after the decision.
        Assert.Equal(3, conversation.GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_without_access_is_refused_before_the_turn_starts(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new NhAiScriptedChatClient());
        using var client = app.CreateClient();
        var id = Guid.Parse(await CreateConversationAsync(client));
        var managed = await AddConversationAsync(app, "user-1", null, "project-manager");
        var message = new NhAssistantConversationMessage("Which projects exist?");

        // Someone who does not take part in the conversation finds nothing.
        var outsider = await SendAsync(app, Person("user-2"), id, message);
        // Without the assistant's access policy, or the agent's own policy, the agent is forbidden.
        var withoutAccess = await SendAsync(app, PersonWithoutAccess("user-1"), id, message);
        var withoutAgentPolicy = await SendAsync(app, Person("user-1"), managed.Id, message);
        // The application names another person than the principal says.
        var mismatch = await SendAsync(app, new NhAssistantDecidingPerson(Person("user-1").User, "user-2", null), id, message);
        var unauthenticated = await SendAsync(app, new NhAssistantDecidingPerson(new ClaimsPrincipal(new ClaimsIdentity()), "user-1", null), id, message);
        var otherDecision = await DecideAsync(app, Person("user-2"), id, Guid.NewGuid(), new NhAssistantConversationDecision(NhAssistantProposalDecision.Approve, new string('0', 64)));

        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(outsider));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(withoutAccess));
        Assert.Equal(NhAssistantErrorCodes.AgentForbidden, CodeOf(withoutAgentPolicy));
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(mismatch));
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(unauthenticated));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(otherDecision));
        await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(
            app,
            Person("user-1"),
            id,
            message,
            configure: _ => new NhAssistantConversationTurnOptions("Not a channel")));

        // No refusal claimed the conversation.
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        Assert.Empty(conversation.GetProperty("messages").EnumerateArray());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_authorizes_the_persons_policy_and_never_the_request_around_the_service(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Found the roadmap project.")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("I may not search projects.");
        await using var app = await StartAsync(provider, model, readPermission: "assistant-admin");
        using var reader = app.CreateClient("user-1");
        using var other = app.CreateClient("user-3");
        var readerConversation = await CreateConversationAsync(reader);
        var otherConversation = await CreateConversationAsync(other);
        var authorizations = app.Services.GetRequiredService<PersonAuthorizations>();

        // The channel's own request is someone else's: one without the tool's permission sends for a
        // person who has it, and one with it sends for a person who lacks it.
        using var channelWithout = app.CreateClient("user-9");
        using var channelWith = app.CreateClient("user-8", admin: true);
        var allowed = await ReadJsonAsync(await channelWithout.PostAsync(
            $"{ChannelPath}/{readerConversation}?person=user-1&permission=assistant-admin", null));
        var refused = await ReadJsonAsync(await channelWith.PostAsync(
            $"{ChannelPath}/{otherConversation}?person=user-3", null));

        Assert.Equal(("completed", "succeeded"), (allowed.GetProperty("status").GetString(), allowed.GetProperty("toolStatus").GetString()));
        Assert.Equal("completed", refused.GetProperty("status").GetString());
        Assert.NotEqual("succeeded", refused.GetProperty("toolStatus").GetString());
        Assert.Equal("ai-tool-authorization-denied", refused.GetProperty("toolResultCode").GetString());
        Assert.Single(app.Tools.Contexts);
        // Each tool call authorized the person the channel named, without a request around the turn.
        Assert.Equal(
            [("user-1", false), ("user-3", false)],
            authorizations.Records.Select(record => (record.User, record.HadRequest)).Order());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_of_another_tenant_finds_nothing_and_a_tenant_the_principal_does_not_carry_is_refused(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Hello from tenant A.");
        await using var app = await StartAsync(
            provider,
            model,
            configure: services => services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaims(Issuer)));
        // A shared conversation of tenant A: the participant reaches it only in that tenant, as the
        // endpoints decide; its owner owns it whatever the tenant.
        var actor = ActorOf("subject-1");
        var conversation = await AddConversationAsync(app, ActorOf("owner-1"), "tenant-a");
        await AddParticipantAsync(app, conversation.Id, actor);
        var message = new NhAssistantConversationMessage("Hello?");

        var ofTenant = await SendAsync(app, new NhAssistantDecidingPerson(TenantPrincipal("subject-1", "tenant-a"), actor, "tenant-a"), conversation.Id, message);
        var ofOtherTenant = await SendAsync(app, new NhAssistantDecidingPerson(TenantPrincipal("subject-1", "tenant-b"), actor, "tenant-b"), conversation.Id, message);
        var claimedTenant = await SendAsync(app, new NhAssistantDecidingPerson(TenantPrincipal("subject-1", "tenant-b"), actor, "tenant-a"), conversation.Id, message);
        var withoutTenant = await SendAsync(app, new NhAssistantDecidingPerson(TenantPrincipal("subject-1", "tenant-a"), actor, null), conversation.Id, message);

        Assert.True(ofTenant.Success);
        Assert.Equal(NhAssistantTurnStatuses.Completed, ofTenant.Data!.Status);
        Assert.Equal("Hello from tenant A.", string.Concat(ofTenant.Data.Message!.Parts.OfType<NhAssistantTextPartDto>().Select(part => part.Text)));
        Assert.Equal(NhAssistantErrorCodes.ConversationNotFound, CodeOf(ofOtherTenant));
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(claimedTenant));
        Assert.Equal(NhAssistantErrorCodes.ContextUnavailable, CodeOf(withoutTenant));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_turn_outlives_its_caller_only_when_asked(AssistantTestProvider provider)
    {
        var model = new GatedChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("Found the roadmap project."));
        await using var app = await StartAsync(provider, model);
        using var client = app.CreateClient();
        var cancelled = Guid.Parse(await CreateConversationAsync(client));
        var kept = Guid.Parse(await CreateConversationAsync(client));
        var endings = app.Services.GetRequiredService<TurnEndings>();

        // By default the turn ends with its caller, as a request ends with its client.
        using (var caller = new CancellationTokenSource())
        {
            var sending = SendAsync(app, Person("user-1"), cancelled, new NhAssistantConversationMessage("Search the projects."), caller.Token);
            await model.WaitForTurnAsync();
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        }
        Assert.Equal(NhAssistantTurnStatuses.Cancelled, (await endings.WaitAsync(cancelled)).Status);
        Assert.Empty(app.Tools.Contexts);

        // Asked to, it continues after the caller left, and the conversation carries its result.
        using (var caller = new CancellationTokenSource())
        {
            var sending = SendAsync(
                app,
                Person("user-1"),
                kept,
                new NhAssistantConversationMessage("Search the projects."),
                caller.Token,
                options => options with { KeepTurnAfterCallerLeaves = true });
            await model.WaitForTurnAsync();
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        }
        model.Release();
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await endings.WaitAsync(kept)).Status);
        Assert.Single(app.Tools.Contexts);
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{kept}"));
        Assert.Equal("Found the roadmap project.", TextOf(conversation.GetProperty("messages")[1]));
    }

    private static async Task DisconnectDuringTurnAsync(HttpClient client, string id, GatedChatClient model, TaskCompletionSource aborted)
    {
        using var abort = new CancellationTokenSource();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assistant/conversations/{id}/messages")
        {
            Content = Json(new { text = "Search the projects.", clientMessageId = "disconnect" })
        };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await model.WaitForTurnAsync();
        await abort.CancelAsync();
        response.Dispose();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private Task<AssistantWebApplication> StartAsync(
        AssistantTestProvider provider,
        IChatClient model,
        bool keepTurns = false,
        TaskCompletionSource? aborted = null,
        string? readPermission = null,
        Action<IServiceCollection>? configure = null)
    {
        return AssistantWebApplication.StartAsync(
            database,
            model,
            provider: provider,
            configure: services =>
            {
                services.PostConfigure<NhAssistantOptions>(options => options.KeepTurnsAfterDisconnect = keepTurns);
                services.AddSingleton<TurnEndings>();
                services.Replace(ServiceDescriptor.Scoped<INhAssistantNotifier>(serviceProvider => serviceProvider.GetRequiredService<TurnEndings>()));
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
                services.AddSingleton<PersonAuthorizations>();
                services.AddSingleton<IAuthorizationHandler, PersonRequirementHandler>();
                // The search tool's policy records whom it authorized, and asks for a permission when set.
                services.AddAuthorization(options => options.AddPolicy(
                    TestProjectToolCatalog.ReadPolicy,
                    policy => policy.AddRequirements(new PersonRequirement(readPermission))));
                configure?.Invoke(services);
            },
            map: app =>
            {
                if (aborted is not null)
                {
                    // Tells the test when the server saw the client go.
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Path.Value?.EndsWith("/messages", StringComparison.Ordinal) == true)
                        {
                            context.RequestAborted.Register(() => aborted.TrySetResult());
                        }
                        await next(context);
                    });
                }
                // The application's channel: its own request names the person it sends for.
                app.MapPost(ChannelPath + "/{conversationId:guid}", SendThroughChannelAsync);
            });
    }

    private static async Task<IResult> SendThroughChannelAsync(
        Guid conversationId,
        string person,
        string? permission,
        HttpContext httpContext,
        INhAssistantConversationTurns turns)
    {
        var sent = await turns.SendAsync(
            Person(person, permission is null ? [] : [permission]),
            conversationId,
            new NhAssistantConversationMessage("Search the projects."),
            new NhAssistantConversationTurnOptions(ChannelCode),
            httpContext.RequestAborted);
        var tool = sent.Data?.Message?.Parts.OfType<NhAssistantToolCallPartDto>().SingleOrDefault();
        return Results.Json(new
        {
            status = sent.Data?.Status,
            code = sent.Success ? null : CodeOf(sent),
            toolStatus = tool?.Status,
            toolResultCode = tool?.ResultCode
        });
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> SendAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationMessage message,
        CancellationToken cancellationToken = default,
        Func<NhAssistantConversationTurnOptions, NhAssistantConversationTurnOptions>? configure = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var options = new NhAssistantConversationTurnOptions(ChannelCode);
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>()
            .SendAsync(person, conversationId, message, configure?.Invoke(options) ?? options, cancellationToken);
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> DecideAsync(
        AssistantWebApplication app,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        Guid approvalId,
        NhAssistantConversationDecision decision)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>()
            .DecideAsync(person, conversationId, approvalId, decision, new NhAssistantConversationTurnOptions(ChannelCode));
    }

    private static async Task<AssistantConversation> AddConversationAsync(
        AssistantWebApplication app,
        string owner,
        string? tenant,
        string agentId = "project-assistant")
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = owner,
            TenantId = tenant,
            AgentId = agentId,
            AgentVersion = 1,
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().AddConversationAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private static async Task AddParticipantAsync(AssistantWebApplication app, Guid conversationId, string actorId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<INhAssistantStore>().TryAddParticipantAsync(
            new AssistantConversationParticipant
            {
                ConversationId = conversationId,
                ActorId = actorId,
                DisplayName = "Participant",
                JoinedVia = NhAssistantParticipantSources.Link,
                JoinedAt = DateTimeOffset.UtcNow
            },
            5,
            CancellationToken.None);
        Assert.Equal(NhAssistantParticipantAddResult.Added, added);
    }

    /// <summary>
    /// The person as the application's channel authenticated them, with the assistant's access
    /// permission and <paramref name="permissions"/>.
    /// </summary>
    private static NhAssistantDecidingPerson Person(string user, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user), new("permission", "assistant") };
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        return new NhAssistantDecidingPerson(new ClaimsPrincipal(new ClaimsIdentity(claims, ChannelCode)), user, null);
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

    private static string TextOf(JsonElement message)
    {
        return string.Concat(message.GetProperty("parts").EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "text")
            .Select(part => part.GetProperty("text").GetString()));
    }

    private static IEnumerable<JsonElement> ToolCallsOf(JsonElement message)
    {
        return message.GetProperty("parts").EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "tool-call");
    }

    private static async Task<string> CreateConversationAsync(HttpClient client)
    {
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = "project-assistant" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadJsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Holds the first model call of every turn until the test releases it, so the test decides when the
    /// turn goes on; calls that continue a turn after a tool pass at once.
    /// </summary>
    private sealed class GatedChatClient(IChatClient inner) : IChatClient
    {
        private readonly SemaphoreSlim _gate = new(0);
        private readonly Channel<bool> _started = System.Threading.Channels.Channel.CreateUnbounded<bool>();

        public Task WaitForTurnAsync()
        {
            return _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }

        public void Release()
        {
            _gate.Release();
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            if (list.Count > 0 && list[^1].Role == ChatRole.User)
            {
                _started.Writer.TryWrite(true);
                await _gate.WaitAsync(cancellationToken);
            }

            await foreach (var update in inner.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return inner.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Signals when a turn of a conversation ended, after the conversation stored its outcome.
    /// </summary>
    private sealed class TurnEndings : INhAssistantNotifier
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<NhAssistantTurnOutcome>> _ended = new();

        public Task<NhAssistantTurnOutcome> WaitAsync(Guid conversationId)
        {
            return SourceOf(conversationId).Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public Task TurnEndedAsync(NhAssistantTurnOutcome outcome, CancellationToken cancellationToken)
        {
            SourceOf(outcome.ConversationId).TrySetResult(outcome);
            return Task.CompletedTask;
        }

        public Task InvitedAsync(Guid conversationId, string inviteeActorId, string inviterDisplayName, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task AgentNotificationAsync(NhAssistantAgentNotificationContext context, IReadOnlyList<string> recipientActorIds, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ApplicationMessageAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private TaskCompletionSource<NhAssistantTurnOutcome> SourceOf(Guid conversationId)
        {
            return _ended.GetOrAdd(conversationId, _ => new TaskCompletionSource<NhAssistantTurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    private sealed record PersonRequirement(string? Permission) : IAuthorizationRequirement;

    private sealed record PersonAuthorization(string? User, bool HadRequest, bool RequestAborted);

    private sealed class PersonAuthorizations
    {
        public ConcurrentQueue<PersonAuthorization> Records { get; } = new();
    }

    /// <summary>
    /// Records whom a tool policy authorized and whether a request ran around it, and asks for the
    /// requirement's permission when it names one.
    /// </summary>
    private sealed class PersonRequirementHandler(PersonAuthorizations records, IHttpContextAccessor accessor)
        : AuthorizationHandler<PersonRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PersonRequirement requirement)
        {
            var httpContext = accessor.HttpContext;
            records.Records.Enqueue(new PersonAuthorization(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                httpContext is not null,
                httpContext?.RequestAborted.IsCancellationRequested == true));
            if (context.User.Identity?.IsAuthenticated == true
                && (requirement.Permission is null || context.User.HasClaim("permission", requirement.Permission)))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
