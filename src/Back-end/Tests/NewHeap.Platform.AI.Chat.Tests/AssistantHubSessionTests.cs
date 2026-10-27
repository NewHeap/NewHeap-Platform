using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Live;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Authentication;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// An assistant hub connection lasts as long as its session: it closes when the token it was opened
/// with expires, and when the session validator no longer accepts its principal, for example after a
/// password change changed the user's security stamp. Other people's connections stay open and keep
/// receiving updates. Over WebSockets, which keep-alives hold open, and server-sent events, on both
/// relational providers.
/// </summary>
public sealed class AssistantHubSessionTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string TenantA = "tenant-a";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_connection_closes_when_the_token_it_was_opened_with_expires(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new RevocableSessions());
        var expiring = new Dictionary<string, string>(LiveReadAuthentication.Headers("alice", TenantA, null))
        {
            [LiveReadAuthentication.ExpiresHeader] = DateTimeOffset.UtcNow.AddSeconds(2).ToString("O", CultureInfo.InvariantCulture)
        };
        await using var alice = await AssistantLiveListener.ConnectAsync(app, expiring, HttpTransportType.WebSockets);
        await using var bob = await AssistantLiveListener.ConnectAsync(app, LiveReadAuthentication.Headers("bob", TenantA, null), HttpTransportType.WebSockets);
        await alice.SyncAsync(app, ActorOf("alice"));

        await alice.Closed.WaitAsync(Patience);

        await bob.SyncAsync(app, ActorOf("bob"));
        Assert.Equal(HubConnectionState.Connected, bob.Connection.State);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Connections_close_once_the_session_validator_refuses_their_principal(AssistantTestProvider provider)
    {
        var sessions = new RevocableSessions();
        await using var app = await StartAsync(provider, sessions);
        var headers = LiveReadAuthentication.Headers("alice", TenantA, null);
        await using var socket = await AssistantLiveListener.ConnectAsync(app, headers, HttpTransportType.WebSockets);
        await using var events = await AssistantLiveListener.ConnectAsync(app, headers, HttpTransportType.ServerSentEvents);
        await using var bob = await AssistantLiveListener.ConnectAsync(app, LiveReadAuthentication.Headers("bob", TenantA, null), HttpTransportType.WebSockets);
        await socket.SyncAsync(app, ActorOf("alice"));
        await events.SyncAsync(app, ActorOf("alice"));

        // Alice changed her password: the security stamp her token carries no longer matches.
        sessions.Revoked["alice"] = true;

        await socket.Closed.WaitAsync(Patience);
        await events.Closed.WaitAsync(Patience);
        await bob.SyncAsync(app, ActorOf("bob"));
        Assert.Equal(HubConnectionState.Connected, bob.Connection.State);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, RevocableSessions sessions)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddAuthentication(LiveReadAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, LiveReadAuthentication>(LiveReadAuthentication.SchemeName, _ => { });
                services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaims(AssistantTenantBindingTests.Issuer));
                services.AddSingleton<INhAuthenticationSessionValidator>(sessions);
                // The hub validates open sessions every minute; this host does so at once.
                services.Replace(ServiceDescriptor.Singleton(provider => new NhAssistantHubSessions(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    provider.GetRequiredService<IServiceProviderIsService>(),
                    NullLogger<NhAssistantHubSessions>.Instance)
                {
                    CheckInterval = TimeSpan.FromMilliseconds(100)
                }));
            },
            provider: provider);
    }

    private static string ActorOf(string person)
    {
        return AssistantTenantBindingTests.ActorOf(person);
    }

    /// <summary>
    /// Accepts every session except those of the people in <see cref="Revoked"/>, as NewHeap's validator
    /// refuses a token whose security stamp no longer matches.
    /// </summary>
    private sealed class RevocableSessions : INhAuthenticationSessionValidator
    {
        public ConcurrentDictionary<string, bool> Revoked { get; } = new(StringComparer.Ordinal);

        public Task<bool> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            var user = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            return Task.FromResult(!Revoked.ContainsKey(user));
        }
    }
}
