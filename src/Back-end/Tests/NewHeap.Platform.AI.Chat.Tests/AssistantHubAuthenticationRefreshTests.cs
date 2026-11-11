using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Live;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Authentication;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// SignalR authentication refresh on the assistant hub (<see cref="NhAssistantOptions.HubAuthenticationRefresh"/>).
/// On ASP.NET Core 11 with the opt-in, the .NET client's <c>RefreshAuthenticationAsync</c> presents a
/// renewed token: for the same caller the connection outlives the token it connected with and keeps
/// receiving, and the session check follows the refreshed principal; a token of another person is refused
/// with 403 and the connection stays with its caller; a token of the same person in another tenant is
/// accepted by SignalR and the hub then closes the connection; an expired token and a removed user are
/// refused with 401, and the session check closes the removed user's connection. On ASP.NET Core 10 the
/// opt-in maps no refresh endpoint. The hub needs no relational behavior, so the host runs on PostgreSQL.
/// </summary>
public sealed class AssistantHubAuthenticationRefreshTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

#if NET11_0_OR_GREATER
    [Fact]
    public async Task A_refresh_for_the_same_caller_keeps_the_connection_past_its_first_token_and_it_still_receives()
    {
        var tokens = new HubTokens();
        var sessions = new StampSessions();
        await using var app = await StartAsync(tokens, sessions);
        var current = tokens.Issue("alice", TenantA, TimeSpan.FromSeconds(2));
        var witnessToken = tokens.Issue("bob", TenantA, TimeSpan.FromSeconds(4));
        await using var alice = await ConnectAsync(app, () => current);
        await using var witness = await ConnectAsync(app, () => witnessToken);

        // Alice's security stamp changes, as after a password change that issued her a new session:
        // both stamps count until the connection refreshed, then only the new one.
        sessions.Stamps["alice"] = ["s1", "s2"];
        current = tokens.Issue("alice", TenantA, TimeSpan.FromMinutes(10), stamp: "s2");
        var lifetime = await alice.Connection.RefreshAuthenticationAsync();
        sessions.Stamps["alice"] = ["s2"];
        var validated = sessions.NextValidation("alice", "s2");

        Assert.NotNull(lifetime);
        Assert.True(lifetime.Value > TimeSpan.FromMinutes(1));
        // Alice's first token expired before the witness's, which the hub then closed.
        await witness.Closed.WaitAsync(Patience);
        // The session check validates the refreshed principal, which only the new stamp accepts.
        await validated.WaitAsync(Patience);
        await alice.SyncAsync(app, ActorOf("alice"));
        Assert.Equal(HubConnectionState.Connected, alice.Connection.State);
    }

    [Fact]
    public async Task A_refresh_with_another_persons_token_is_refused_and_the_connection_stays_with_its_caller()
    {
        var tokens = new HubTokens();
        await using var app = await StartAsync(tokens, new StampSessions());
        var current = tokens.Issue("alice", TenantA, TimeSpan.FromMinutes(10));
        await using var alice = await ConnectAsync(app, () => current);

        // An account switch in the browser: the connection may not become Bob's.
        current = tokens.Issue("bob", TenantA, TimeSpan.FromMinutes(20));
        var refused = await Assert.ThrowsAnyAsync<HttpRequestException>(() => alice.Connection.RefreshAuthenticationAsync());

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await alice.SyncAsync(app, ActorOf("alice"));
        Assert.Equal(HubConnectionState.Connected, alice.Connection.State);
    }

    [Fact]
    public async Task A_refresh_into_another_tenant_closes_the_connection()
    {
        var tokens = new HubTokens();
        await using var app = await StartAsync(tokens, new StampSessions());
        var current = tokens.Issue("alice", TenantA, TimeSpan.FromMinutes(10));
        await using var alice = await ConnectAsync(app, () => current);

        // The same person, signed in for another tenant: SignalR accepts the subject, the hub does not
        // keep the groups of tenant A for it.
        current = tokens.Issue("alice", TenantB, TimeSpan.FromMinutes(20));
        await alice.Connection.RefreshAuthenticationAsync();

        await alice.Closed.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_refresh_with_an_expired_token_or_for_a_removed_user_is_refused_and_the_removed_users_connection_closes()
    {
        var tokens = new HubTokens();
        var sessions = new StampSessions();
        await using var app = await StartAsync(tokens, sessions);
        var current = tokens.Issue("alice", TenantA, TimeSpan.FromMinutes(10));
        await using var alice = await ConnectAsync(app, () => current);

        current = tokens.Issue("alice", TenantA, TimeSpan.FromMinutes(-1));
        var expired = await Assert.ThrowsAnyAsync<HttpRequestException>(() => alice.Connection.RefreshAuthenticationAsync());
        await alice.SyncAsync(app, ActorOf("alice"));
        current = tokens.Issue("alice", TenantA, TimeSpan.FromMinutes(20));
        tokens.Removed["alice"] = true;
        var removed = await Assert.ThrowsAnyAsync<HttpRequestException>(() => alice.Connection.RefreshAuthenticationAsync());
        // The session validator refuses the removed user as well, also the principal the connection keeps.
        sessions.Removed["alice"] = true;

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        await alice.Closed.WaitAsync(Patience);
    }
#else
    [Fact]
    public async Task On_ASP_NET_Core_10_the_opt_in_maps_no_refresh_endpoint()
    {
        var tokens = new HubTokens();
        await using var app = await StartAsync(tokens, new StampSessions());
        var token = tokens.Issue("alice", TenantA, TimeSpan.FromMinutes(10));
        using var client = app.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hub/assistant/refresh");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
#endif

    private static Task<AssistantLiveListener> ConnectAsync(AssistantWebApplication app, Func<string> accessToken)
    {
        return AssistantLiveListener.ConnectWithTokenAsync(
            app,
            accessToken,
#if NET11_0_OR_GREATER
            // The test refreshes by hand, when it renewed the token.
            builder => builder.WithAuthenticationRefresh(options => options.EnableAutoRefresh = false));
#else
            null);
#endif
    }

    private Task<AssistantWebApplication> StartAsync(HubTokens tokens, StampSessions sessions)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Configure<NhAssistantOptions>(options => options.HubAuthenticationRefresh = true);
                services.AddSingleton(tokens);
                services.AddAuthentication(HubTokenAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HubTokenAuthentication>(HubTokenAuthentication.SchemeName, _ => { });
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
            });
    }

    private static string ActorOf(string person)
    {
        return AssistantTenantBindingTests.ActorOf(person);
    }

    /// <summary>
    /// The access tokens the test hands out: each names a person, a tenant, an expiry and a security
    /// stamp. People in <see cref="Removed"/> no longer authenticate, as a deleted user does not.
    /// </summary>
    private sealed class HubTokens
    {
        private readonly ConcurrentDictionary<string, HubToken> _tokens = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, bool> Removed { get; } = new(StringComparer.Ordinal);

        public string Issue(string person, string tenant, TimeSpan lifetime, string stamp = "s1")
        {
            var token = Guid.NewGuid().ToString("N");
            _tokens[token] = new HubToken(person, tenant, DateTimeOffset.UtcNow.Add(lifetime), stamp);
            return token;
        }

        public bool TryGet(string token, out HubToken found)
        {
            return _tokens.TryGetValue(token, out found!);
        }
    }

    private sealed record HubToken(string Person, string Tenant, DateTimeOffset Expires, string Stamp);

    /// <summary>
    /// Signs the bearer of a token from <see cref="HubTokens"/> in at
    /// <see cref="AssistantTenantBindingTests.Issuer"/> with the token's tenant and stamp, until the
    /// token expires; an unknown or expired token, or one of a removed person, fails.
    /// </summary>
    private sealed class HubTokenAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        HubTokens tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "hub-token-test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (!tokens.TryGet(header["Bearer ".Length..], out var token)
                || token.Expires <= DateTimeOffset.UtcNow
                || tokens.Removed.ContainsKey(token.Person))
            {
                return Task.FromResult(AuthenticateResult.Fail("The token is not valid."));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("iss", AssistantTenantBindingTests.Issuer),
                    new Claim("sub", token.Person),
                    new Claim(ClaimTypes.NameIdentifier, token.Person),
                    new Claim("name", token.Person),
                    new Claim("tenant_id", token.Tenant),
                    new Claim("permission", "assistant"),
                    new Claim(StampSessions.ClaimType, token.Stamp)
                ],
                SchemeName));
            var properties = new AuthenticationProperties { ExpiresUtc = token.Expires };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, SchemeName)));
        }
    }

    /// <summary>
    /// Accepts a principal while its stamp is one of the person's current ones (<c>s1</c> unless
    /// <see cref="Stamps"/> says otherwise) and the person is not removed, as NewHeap's validator refuses a
    /// token whose security stamp changed; it tells when it next accepted a person with a stamp.
    /// </summary>
    private sealed class StampSessions : INhAuthenticationSessionValidator
    {
        public const string ClaimType = "test-stamp";

        private readonly ConcurrentDictionary<(string Person, string Stamp), TaskCompletionSource> _next = new();

        public ConcurrentDictionary<string, string[]> Stamps { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, bool> Removed { get; } = new(StringComparer.Ordinal);

        /// <summary>Completes when the validator next accepts the person with the stamp.</summary>
        public Task NextValidation(string person, string stamp)
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _next[(person, stamp)] = next;
            return next.Task;
        }

        public Task<bool> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            var person = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var stamp = principal.FindFirstValue(ClaimType) ?? string.Empty;
            var valid = !Removed.ContainsKey(person)
                && Stamps.GetValueOrDefault(person, ["s1"]).Contains(stamp, StringComparer.Ordinal);
            if (valid && _next.TryRemove((person, stamp), out var next))
            {
                next.TrySetResult();
            }

            return Task.FromResult(valid);
        }
    }
}
