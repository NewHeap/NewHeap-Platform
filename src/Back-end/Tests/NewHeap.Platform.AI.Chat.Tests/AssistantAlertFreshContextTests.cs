using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Alerts in a host whose principals come from the application's access source
/// (<c>UsePrincipalsFromAccessSource</c>), on both relational providers. A person whose rights the
/// application holds no fresh context of cannot be told about yet: their delivery stays pending, a later
/// pass checks them again and it goes out once they are back, or it is dropped with
/// <see cref="NhAssistantAlertDropReasons.NoFreshContext"/> after
/// <see cref="NhAssistantLimits.AlertFreshContextWait"/>. A person whose fresh rights give no read role is
/// dropped at once.
/// </summary>
public sealed class AssistantAlertFreshContextTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Kind = "fresh-context-warning";

    /// <summary>The claim that lets a person read the tasks, as the application's claims transformation gives it.</summary>
    private const string ReadsTasks = "reads-tasks";

    private const string Present = "0c1d0000-0000-4000-8000-000000000001";
    private const string Outsider = "0c1d0000-0000-4000-8000-000000000002";
    private const string Away = "0c1d0000-0000-4000-8000-000000000003";
    private const string Gone = "0c1d0000-0000-4000-8000-000000000004";
    private const string LosesAccess = "0c1d0000-0000-4000-8000-000000000005";

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Wait = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_without_a_fresh_context_waits_for_one_and_a_person_without_access_drops_at_once(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        var contexts = host.Services.GetRequiredService<FreshContexts>();
        // Present, the outsider and the person who loses access later were here a minute ago; the person
        // away and the one who never comes back last made a request two hours ago.
        contexts.Record(Present, clock.GetUtcNow().AddMinutes(-1), ReadsTasks);
        contexts.Record(Outsider, clock.GetUtcNow().AddMinutes(-1));
        contexts.Record(LosesAccess, clock.GetUtcNow().AddMinutes(-1), ReadsTasks);
        contexts.Record(Away, clock.GetUtcNow().AddHours(-2), ReadsTasks);
        contexts.Record(Gone, clock.GetUtcNow().AddHours(-2), ReadsTasks);

        // The raise plans everyone the check cannot refuse yet; only the outsider, whose fresh rights do
        // not read the task, is dropped at once.
        var raised = await RaiseAsync(host, [Present, Outsider, Away, Gone, LosesAccess]);
        Assert.Equal(4, raised.RecipientCount);
        var dropped = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped);
        Assert.Equal((Outsider, NhAssistantAlertDropReasons.NoAccess), (dropped.ActorId, dropped.ResultCode));

        // Before the delivery the person who loses access is no longer allowed to read tasks, with a
        // fresh context: no access, dropped at once. The two who are away wait.
        contexts.Record(LosesAccess, clock.GetUtcNow());
        var first = await DispatchAsync(host);
        Assert.Equal((4, 1, 1, 2), (first.Claimed, first.Sent, first.Dropped, first.Waiting));
        Assert.Equal([Present], Pushes(host).Sent.Select(push => push.Push.RecipientActorId));
        var deliveries = await DeliveriesAsync(host, raised.AlertId);
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.NoAccess), (deliveries[LosesAccess].Status, deliveries[LosesAccess].ReasonCode));
        foreach (var waiting in new[] { deliveries[Away], deliveries[Gone] })
        {
            // Still pending, still due when it fell due, and the wait is no attempt to send it.
            Assert.Equal(
                (NhAssistantAlertDeliveryStatuses.Pending, NhAssistantAlertDispatcher.AwaitingFreshContextCode, Monday, 0, (string?)null),
                (waiting.Status, waiting.ReasonCode, waiting.DueAt!.Value, waiting.Attempts, waiting.ClaimedBy));
            Assert.Equal(Monday + NhAssistantAlertDispatcher.FreshContextRetryDelay, waiting.ClaimedUntil);
        }

        // A pass before the retry leaves them alone.
        Assert.Equal(NhAssistantAlertDispatchResult.None, await DispatchAsync(host));

        // The person away comes back: the next check after the retry delay sends their alert.
        clock.Advance(TimeSpan.FromMinutes(20));
        contexts.Record(Away, clock.GetUtcNow(), ReadsTasks);
        var back = await DispatchAsync(host);
        Assert.Equal((2, 1, 0, 1), (back.Claimed, back.Sent, back.Dropped, back.Waiting));
        Assert.Equal([Present, Away], Pushes(host).Sent.Select(push => push.Push.RecipientActorId));
        deliveries = await DeliveriesAsync(host, raised.AlertId);
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Sent, (string?)null, 1), (deliveries[Away].Status, deliveries[Away].ReasonCode, deliveries[Away].Attempts));

        // The other never comes back: once the wait passed since the delivery fell due, it is dropped
        // with its own reason and audited without content.
        clock.Advance(Wait);
        var late = await DispatchAsync(host);
        Assert.Equal((1, 0, 1, 0), (late.Claimed, late.Sent, late.Dropped, late.Waiting));
        deliveries = await DeliveriesAsync(host, raised.AlertId);
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.NoFreshContext), (deliveries[Gone].Status, deliveries[Gone].ReasonCode));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped
            && evt.ActorId == Gone
            && evt.ResultCode == NhAssistantAlertDropReasons.NoFreshContext
            && evt.AlertId == raised.AlertId);
        Assert.Equal(2, Pushes(host).Sent.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_alert_that_expires_while_its_person_is_away_takes_the_waiting_delivery_back(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Monday);
        await using var host = await CreateHostAsync(provider, clock);
        host.Services.GetRequiredService<FreshContexts>().Record(Away, clock.GetUtcNow().AddHours(-2), ReadsTasks);
        var raised = await RaiseAsync(host, [Away], expiresAt: Monday.AddMinutes(30));
        Assert.Equal(1, (await DispatchAsync(host)).Waiting);

        // The alert expires before the person is back and before the wait ends.
        clock.Advance(TimeSpan.FromMinutes(31));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<NhAssistantAlertService>().MaintainAsync(CancellationToken.None)).Expired);
        }

        Assert.Equal(NhAssistantAlertDispatchResult.None, await DispatchAsync(host));
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Expired, (await DeliveriesAsync(host, raised.AlertId))[Away].Status);
        Assert.Empty(Pushes(host).Sent);
    }

    [Fact]
    public void The_wait_for_a_fresh_context_is_bounded()
    {
        new NhAssistantLimits { AlertFreshContextWait = TimeSpan.FromMinutes(1) }.Validate();
        new NhAssistantLimits { AlertFreshContextWait = TimeSpan.FromDays(30) }.Validate();
        Assert.Equal(TimeSpan.FromHours(24), new NhAssistantLimits().AlertFreshContextWait);
        Assert.Throws<InvalidOperationException>(() => new NhAssistantLimits { AlertFreshContextWait = TimeSpan.FromSeconds(59) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new NhAssistantLimits { AlertFreshContextWait = TimeSpan.FromDays(31) }.Validate());
    }

    private static DeliveryPushes Pushes(AssistantTestHost host) => host.Services.GetRequiredService<DeliveryPushes>();

    private static async Task<Dictionary<string, AssistantAlertDelivery>> DeliveriesAsync(AssistantTestHost host, Guid alertId)
    {
        var alert = await host.Services.GetRequiredService<NhAssistantAlertStore>().FindAsync(alertId, CancellationToken.None);
        Assert.NotNull(alert);
        return alert.Deliveries.ToDictionary(delivery => delivery.RecipientActorId, StringComparer.Ordinal);
    }

    private static async Task<NhAssistantAlertReceipt> RaiseAsync(AssistantTestHost host, IReadOnlyList<string> recipients, DateTimeOffset? expiresAt = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var raised = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(
            new NhAssistantAlert(Kind, new NhAiSubjectRef(FreshContextTaskResolver.Type, "task-1"))
            {
                Parameters = new Dictionary<string, string> { ["task"] = "Send the invoice" },
                Recipients = recipients,
                ExpiresAt = expiresAt
            });
        Assert.True(raised.Success);
        return raised.Data!;
    }

    private static async Task<NhAssistantAlertDispatchResult> DispatchAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAlertDispatcher>().DispatchAsync(CancellationToken.None);
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, AssistantTestClock clock)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            limits => limits.AlertFreshContextWait = Wait,
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<FreshContexts>();
                services.AddSingleton<DeliveryPushes>();
                services.Replace(ServiceDescriptor.Scoped<INhAssistantNotifier, DeliveryNotifier>());
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
                // Every principal outside a request comes from the contexts the application recorded.
                services.AddNewHeapPlatformAIAspNet(ai => ai.UsePrincipalsFromAccessSource<FreshContexts>(options => options.MaxAge = MaxAge));
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<FreshContextTaskResolver>()
                .AddAlertKind(Kind, kind => kind
                    .DisplayName("tests.alerts.fresh-context")
                    .Title((_, parameters) => $"{parameters["task"]} is due today")
                    .Recipients(NhAssistantAlertRecipients.Explicit)));
    }

    /// <summary>
    /// The application's record of its people's rights: the claims of each person's last own request and
    /// when, on the application's clock.
    /// </summary>
    public sealed class FreshContexts : INhAiAccessSource
    {
        private readonly ConcurrentDictionary<string, NhAiAccessContext> _contexts = new(StringComparer.Ordinal);

        public void Record(string actorId, DateTimeOffset validAt, params string[] permissions)
        {
            _contexts[actorId] = new NhAiAccessContext(
                permissions.Select(permission => new Claim("permission", permission)).Append(new Claim(ClaimTypes.NameIdentifier, actorId)),
                validAt);
        }

        public ValueTask<NhAiAccessContext?> GetAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_contexts.GetValueOrDefault(actorId));
        }
    }

    /// <summary>
    /// The tasks, which a person reads with the claim the application's claims transformation gives: the
    /// resolver rebuilds the person as a headless run would, as the whole application's resolver does, so
    /// without a fresh context it finds no role.
    /// </summary>
    public sealed class FreshContextTaskResolver(INhAiPrincipalFactory principals) : INhAiSubjectAccessResolver
    {
        public const string Type = "fresh-context-task";

        public string SubjectType => Type;

        public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null))
            {
                DisplayName = "Task " + subject.Id
            });
        }

        public async ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
        {
            var info = (await DescribeAsync(subject, cancellationToken))!;
            var principal = await principals.CreateAsync(actorId, info, cancellationToken);
            return principal?.HasClaim("permission", ReadsTasks) == true
                ? new NhAiSubjectAccess(NhAiSubjectRole.Reader)
                : NhAiSubjectAccess.None;
        }

        public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
            NhAiSubjectRef subject,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
