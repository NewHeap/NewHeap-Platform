using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB6 (CS6): a turn that waits for a question keeps <c>waiting-for-approval</c>, and Web Push tells the
/// question's addressee with a question notice of its own, in the language of their browser; the person
/// whose turn asked hears nothing when the question asks someone else, and an approval still notifies
/// the person whose turn it is. The assistant's own Web Push notifier sends to a fake push service. On
/// both relational providers.
/// </summary>
public sealed class AssistantQuestionNoticeTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Member = "member-1";
    private const string MemberName = "Mia";
    private const string OwnerEndpoint = "https://fcm.googleapis.com/fcm/send/owner-browser";
    private const string MemberEndpoint = "https://fcm.googleapis.com/fcm/send/member-browser";
    private const string Question = "Which status should the launch project get?";

    private static readonly string[] Choices = ["Active", "On hold", "Closed"];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_question_notifies_its_addressee_and_an_approval_the_person_whose_turn_it_is(AssistantTestProvider provider)
    {
        // Each chat gets a script of its own: every turn here waits, so none returns a call's result.
        var model = new SwitchingChatClient(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices, addressee = MemberName }, "call-ask-member"));
        var pushService = new FakePushService();
        var (publicKey, privateKey) = NhAssistantWebPushKeys.Generate();
        // Questions are a run setting of the agent (AllowQuestions), which a host without background
        // operations refuses at start; the test host does not start.
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services
                .AddHttpClient(NhAssistantWebPushNotifier.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => pushService),
            assistantBuilder: assistant => assistant
                .ConfigureAgent(AgentId, agent => agent.AllowQuestions())
                .ConfigurePush(push =>
                {
                    push.PublicKey = publicKey;
                    push.PrivateKey = privateKey;
                    push.Subject = "mailto:assistant@example.com";
                }));
        using var ownerBrowser = await SubscribeAsync(host, AssistantTestHost.UserId, OwnerEndpoint, "en");
        using var memberBrowser = await SubscribeAsync(host, Member, MemberEndpoint, "nl");

        // A question to another person of the shared chat reaches that person only, in Dutch.
        var toMember = await CreateSharedChatAsync(host);
        var asked = await host.SendAsync(toMember, "Ask Mia what happens to the launch project.");
        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, asked.Single<NhAssistantTurnCompletedEvent>().Status);
        var notice = Assert.Single(pushService.Requests);
        Assert.Equal((new Uri(MemberEndpoint), "high"), (notice.Uri, notice.Urgency));
        Assert.Equal(
            ("question-waiting", toMember.ToString(), "De assistent wacht op je antwoord op een vraag."),
            memberBrowser.Read(notice));
        var waiting = await host.ReadViewAsync(toMember);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, waiting.Status);
        Assert.Equal(Member, waiting.PendingQuestion?.AddresseeActorId);

        // A question to the person whose turn it is reaches them with the question notice.
        var toOwner = await CreateSharedChatAsync(host);
        model.Current = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = Question, choices = Choices }, "call-ask-owner");
        await host.SendAsync(toOwner, "Change the status of the launch project.");
        Assert.Equal(2, pushService.Requests.Count);
        var ownNotice = pushService.Requests[1];
        Assert.Equal(new Uri(OwnerEndpoint), ownNotice.Uri);
        Assert.Equal(
            ("question-waiting", toOwner.ToString(), "The assistant is waiting for your answer to a question."),
            ownerBrowser.Read(ownNotice));

        // An approval still tells only the person whose turn created it.
        var approving = await CreateSharedChatAsync(host);
        model.Current = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } });
        await host.SendAsync(approving, "Activate the launch project.");
        Assert.Equal(3, pushService.Requests.Count);
        var approval = pushService.Requests[2];
        Assert.Equal(new Uri(OwnerEndpoint), approval.Uri);
        Assert.Equal(
            ("approval-required", approving.ToString(), "An action is waiting for approval."),
            ownerBrowser.Read(approval));
    }

    /// <summary>
    /// A chat of the test user that Mia shares, so a question may ask her.
    /// </summary>
    private static async Task<Guid> CreateSharedChatAsync(AssistantTestHost host)
    {
        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Participants.Add(new AssistantConversationParticipant
        {
            ConversationId = conversation.Id,
            ActorId = Member,
            DisplayName = MemberName,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        return conversation.Id;
    }

    private static async Task<Browser> SubscribeAsync(AssistantTestHost host, string actorId, string endpoint, string language)
    {
        var browser = new Browser();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantNotificationStore>().UpsertSubscriptionAsync(
            actorId,
            endpoint,
            NhAssistantBase64Url.Encode(browser.PublicKey),
            NhAssistantBase64Url.Encode(browser.AuthSecret),
            language,
            5,
            CancellationToken.None);
        return browser;
    }

    /// <summary>
    /// A browser's push keys, which decrypt the notifications sent to it.
    /// </summary>
    private sealed class Browser : IDisposable
    {
        private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        public Browser()
        {
            PublicKey = NhAssistantWebPushKeys.UncompressedPoint(_key.ExportParameters(false).Q);
        }

        public byte[] PublicKey { get; }

        public byte[] AuthSecret { get; } = RandomNumberGenerator.GetBytes(16);

        public (string? Kind, string? ConversationId, string? Body) Read(PushDelivery delivery)
        {
            var payload = JsonDocument.Parse(AssistantWebPushTests.Decrypt(delivery.Body, _key, PublicKey, AuthSecret)).RootElement;
            return (
                payload.GetProperty("kind").GetString(),
                payload.GetProperty("conversationId").GetString(),
                payload.GetProperty("body").GetString());
        }

        public void Dispose()
        {
            _key.Dispose();
        }
    }

    private sealed record PushDelivery(Uri Uri, string? Urgency, byte[] Body);

    private sealed class FakePushService : HttpMessageHandler
    {
        private readonly ConcurrentQueue<PushDelivery> _requests = new();

        public IReadOnlyList<PushDelivery> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(new PushDelivery(
                request.RequestUri!,
                request.Headers.TryGetValues("Urgency", out var urgency) ? urgency.Single() : null,
                await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
