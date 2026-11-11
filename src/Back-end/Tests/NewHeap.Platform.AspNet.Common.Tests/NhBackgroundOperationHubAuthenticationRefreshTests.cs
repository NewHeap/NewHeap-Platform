using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Identity.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// SignalR authentication refresh on the background operation hub
/// (<see cref="NhBackgroundOperationsOptions.HubAuthenticationRefresh"/>). On ASP.NET Core 11 with the
/// opt-in, a browser-like WebSocket connection presents a renewed token at <c>/refresh</c>: for the same
/// person it outlives the token it connected with and keeps receiving its own and its division's
/// changes; a token of another person is refused with 403 and the connection stays with its person until
/// its own token expires; an expired token and a removed user are refused with 401; and a person who lost
/// the connection's active division is disconnected after the refresh. Without the opt-in, and on
/// ASP.NET Core 10, the hub maps no refresh endpoint. The tests need no relational database: no operation
/// is stored, and the host's startup check reads an in-memory context.
/// </summary>
public sealed class NhBackgroundOperationHubAuthenticationRefreshTests
{
    private const string HubPath = "/hub/background-operations";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("background-operation-hub-refresh-tests-signing-key-2026"));

    [Fact]
    public async Task Without_the_opt_in_the_hub_maps_no_refresh_endpoint()
    {
        await using var app = await StartAsync(authenticationRefresh: false, new Divisions(), new RemovedUsers());
        var userId = Guid.NewGuid();
        using var connection = await ConnectAsync(app.GetTestServer(), CreateToken(userId));

        using var refreshed = await RefreshAsync(app.GetTestServer(), connection.ConnectionToken, CreateToken(userId));

        refreshed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

#if !NET11_0_OR_GREATER
    [Fact]
    public async Task On_ASP_NET_Core_10_the_opt_in_maps_no_refresh_endpoint()
    {
        await using var app = await StartAsync(authenticationRefresh: true, new Divisions(), new RemovedUsers());
        var userId = Guid.NewGuid();
        using var connection = await ConnectAsync(app.GetTestServer(), CreateToken(userId));

        using var refreshed = await RefreshAsync(app.GetTestServer(), connection.ConnectionToken, CreateToken(userId));

        refreshed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
#endif

    [Fact]
    public void Only_the_same_person_with_the_same_impersonation_origin_is_the_same_person()
    {
        var alice = Guid.NewGuid().ToString();
        var admin = Guid.NewGuid().ToString();

        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), Principal(alice)).Should().BeTrue();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), Principal(Guid.NewGuid().ToString())).Should().BeFalse();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), Principal(alice, admin)).Should().BeFalse();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice, admin), Principal(alice)).Should().BeFalse();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice, admin), Principal(alice, admin)).Should().BeTrue();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse();
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task A_refresh_for_the_same_person_keeps_the_connection_past_its_first_token_and_it_still_receives()
    {
        var divisions = new Divisions();
        await using var app = await StartAsync(authenticationRefresh: true, divisions, new RemovedUsers());
        var server = app.GetTestServer();
        var userId = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        divisions.Grant(userId, divisionId);

        // The refreshing connection's first token expires before the witness's; once the witness closed,
        // the hub has checked the refreshing connection after its first token expired.
        using var refreshing = await ConnectAsync(server, CreateToken(userId, TimeSpan.FromSeconds(2)), divisionId);
        using var witness = await ConnectAsync(server, CreateToken(Guid.NewGuid(), TimeSpan.FromSeconds(4)));

        using var refreshed = await RefreshAsync(server, refreshing.ConnectionToken, CreateToken(userId, TimeSpan.FromMinutes(10)));

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()))
        {
            body.RootElement.GetProperty("tokenLifetimeSeconds").GetInt64().Should().BeGreaterThan(60);
        }

        await witness.Connection.WaitForCloseAsync();

        var global = await PublishAsync(app, userId, null);
        var inDivision = await PublishAsync(app, userId, divisionId);
        (await NextOperationIdAsync(refreshing)).Should().Be(global);
        (await NextOperationIdAsync(refreshing)).Should().Be(inDivision);
        refreshing.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task A_refresh_with_another_persons_token_is_refused_and_the_connection_stays_with_its_person_until_its_token_expires()
    {
        await using var app = await StartAsync(authenticationRefresh: true, new Divisions(), new RemovedUsers());
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        using var connection = await ConnectAsync(server, CreateToken(alice, TimeSpan.FromSeconds(3)));

        // An account switch in the browser: the connection may not become Bob's.
        using var refreshed = await RefreshAsync(server, connection.ConnectionToken, CreateToken(bob, TimeSpan.FromMinutes(10)));

        refreshed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await PublishAsync(app, bob, null);
        var aliceOperation = await PublishAsync(app, alice, null);
        (await NextOperationIdAsync(connection)).Should().Be(aliceOperation);
        await connection.Connection.WaitForCloseAsync();
    }

    [Fact]
    public async Task A_refresh_with_an_expired_token_or_for_a_removed_user_is_refused_and_the_connection_still_closes_when_its_token_expires()
    {
        var removed = new RemovedUsers();
        await using var app = await StartAsync(authenticationRefresh: true, new Divisions(), removed);
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        using var connection = await ConnectAsync(server, CreateToken(alice, TimeSpan.FromSeconds(3)));

        using var expired = await RefreshAsync(server, connection.ConnectionToken, CreateExpiredToken(alice));
        removed.Remove(alice);
        using var afterRemoval = await RefreshAsync(server, connection.ConnectionToken, CreateToken(alice, TimeSpan.FromMinutes(10)));

        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        afterRemoval.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await connection.Connection.WaitForCloseAsync();
    }

    [Fact]
    public async Task After_a_refresh_the_hub_closes_the_connection_of_a_person_who_lost_its_active_division()
    {
        var divisions = new Divisions();
        await using var app = await StartAsync(authenticationRefresh: true, divisions, new RemovedUsers());
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        divisions.Grant(alice, divisionId);
        using var connection = await ConnectAsync(server, CreateToken(alice), divisionId);

        divisions.Revoke(alice, divisionId);
        using var refreshed = await RefreshAsync(server, connection.ConnectionToken, CreateToken(alice, TimeSpan.FromMinutes(20)));

        // The refresh itself is the same person's; the hub then checks the division again.
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        await connection.Connection.WaitForCloseAsync();
    }
#endif

    /// <summary>
    /// Presents <paramref name="token"/> at the hub's refresh endpoint for the connection, as the SignalR
    /// 11 clients do: a POST that names the connection in the <c>X-SignalR-Connection-Token</c> header
    /// (ASP.NET Core 11.0.0 reads it there, not from the query string), with the renewed token in the
    /// Authorization header and the <c>X-Requested-With</c> header every SignalR request carries.
    /// </summary>
    private static async Task<HttpResponseMessage> RefreshAsync(TestServer server, string connectionToken, string token)
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{HubPath}/refresh");
        request.Headers.Add("X-SignalR-Connection-Token", connectionToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(request);
    }

    private static async Task<Guid> PublishAsync(WebApplication app, Guid ownerId, Guid? divisionId)
    {
        var operationId = Guid.NewGuid();
        await app.Services.GetRequiredService<INhBackgroundOperationLiveUpdatePublisher>().PublishChangedAsync(
            ownerId,
            new NhBackgroundOperationChangedMessage(operationId, 1, 1, NhBackgroundOperationStatus.Running, divisionId));
        return operationId;
    }

    private static async Task<Guid> NextOperationIdAsync(HubSocketConnection connection)
    {
        var change = await connection.Connection.ReceiveMessageAsync(type: 1);
        change.GetProperty("target").GetString().Should().Be(nameof(INhBackgroundOperationHubClient.OperationChanged));
        return change.GetProperty("arguments")[0].GetProperty("operationId").GetGuid();
    }

    /// <summary>
    /// Negotiates and opens a WebSocket connection to the hub with a query token, as a browser does, and
    /// waits until the hub handles its messages, so the connection is in its groups.
    /// </summary>
    private static async Task<HubSocketConnection> ConnectAsync(TestServer server, string token, Guid? divisionId = null)
    {
        var division = divisionId.HasValue ? $"&{NhHubQueryTokens.DivisionParameter}={divisionId.Value}" : string.Empty;
        using var client = server.CreateClient();
        using var negotiated = await client.PostAsync($"{HubPath}/negotiate?negotiateVersion=1&access_token={token}{division}", null);
        negotiated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var negotiation = JsonDocument.Parse(await negotiated.Content.ReadAsStringAsync());
        var connectionToken = negotiation.RootElement.GetProperty("connectionToken").GetString()!;

        var socket = await server.CreateWebSocketClient().ConnectAsync(
            new Uri(server.BaseAddress, $"{HubPath}?id={Uri.EscapeDataString(connectionToken)}&access_token={token}{division}"),
            CancellationToken.None);
        var connection = new JsonHubSocket(socket);
        await connection.SendAsync("""{"protocol":"json","version":1}""");
        (await connection.ReceiveAsync()).Should().Be("{}");
        await connection.SendAsync("""{"type":1,"invocationId":"joined","target":"NotAHubMethod","arguments":[]}""");
        (await connection.ReceiveMessageAsync(type: 3)).GetProperty("invocationId").GetString().Should().Be("joined");

        return new HubSocketConnection(socket, connection, connectionToken);
    }

    private sealed record HubSocketConnection(WebSocket Socket, JsonHubSocket Connection, string ConnectionToken) : IDisposable
    {
        public void Dispose()
        {
            Socket.Dispose();
        }
    }

    /// <summary>
    /// A host with background operations on an in-memory context of its own, live updates at the default
    /// hub path and the bearer setup of a NewHeap host: its message handler reads the query token for the
    /// hub, and its token validation refuses removed users as NewHeap's session validator does.
    /// </summary>
    private static async Task<WebApplication> StartAsync(bool authenticationRefresh, Divisions divisions, RemovedUsers removed)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var databaseName = Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<RefreshDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        builder.Services.AddNewHeapBackgroundOperations<RefreshDbContext>(operations =>
        {
            operations.Options.DispatchWorkersEnabled = false;
            operations.Options.ReconciliationEnabled = false;
            operations.Options.CleanupEnabled = false;
            operations.Options.UserNotificationProjectionEnabled = false;
            operations.Options.LiveUpdatesEnabled = true;
            operations.Options.HubAuthenticationRefresh = authenticationRefresh;
        });
        builder.Services.AddSingleton(divisions);
        builder.Services.AddNewHeapDivisionAccess<DivisionAccess>();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    IssuerSigningKey = SigningKey,
                    ValidateIssuer = false,
                    ValidateAudience = false
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        NhHubQueryTokens.Read(context);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        if (removed.Contains(context.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)))
                        {
                            context.Fail("The authentication session is no longer valid.");
                        }

                        return Task.CompletedTask;
                    }
                };
            })
            .AddCookie("Identity.Application");
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNewHeapBackgroundOperations();
        await app.StartAsync();
        return app;
    }

    private static string CreateToken(Guid userId, TimeSpan? lifetime = null)
    {
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())]),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }

    /// <summary>A token of the user that expired well beyond the validation's clock skew.</summary>
    private static string CreateExpiredToken(Guid userId)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())]),
            IssuedAt = now.AddHours(-2),
            NotBefore = now.AddHours(-2),
            Expires = now.AddHours(-1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }

    private static ClaimsPrincipal Principal(string userId, string? impersonationOrigin = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (impersonationOrigin is not null)
        {
            claims.Add(new Claim(NhPlatformClaimTypes.ImpersonateOriginUserId, impersonationOrigin));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>The users the test host's token validation refuses, as if they were deleted.</summary>
    private sealed class RemovedUsers
    {
        private readonly ConcurrentDictionary<string, bool> _removed = new(StringComparer.Ordinal);

        public void Remove(Guid userId)
        {
            _removed[userId.ToString()] = true;
        }

        public bool Contains(string? userId)
        {
            return userId is not null && _removed.ContainsKey(userId);
        }
    }

    /// <summary>The divisions each user may use; the test grants and revokes them.</summary>
    private sealed class Divisions
    {
        private readonly ConcurrentDictionary<(NhActorRef Actor, Guid Division), bool> _granted = new();

        public void Grant(Guid userId, Guid divisionId)
        {
            _granted[(NhActorRef.ForUser(userId), divisionId)] = true;
        }

        public void Revoke(Guid userId, Guid divisionId)
        {
            _granted.TryRemove((NhActorRef.ForUser(userId), divisionId), out _);
        }

        public bool HasAccess(NhActorRef actor, Guid divisionId)
        {
            return _granted.ContainsKey((actor, divisionId));
        }
    }

    /// <summary>The host's division access, answered from the test's <see cref="Divisions"/>.</summary>
    private sealed class DivisionAccess(Divisions divisions) : INhDivisionAccess
    {
        public ValueTask<bool> HasAccessAsync(NhActorRef actor, Guid divisionId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(divisions.HasAccess(actor, divisionId));
        }
    }

    private sealed class NoOpScheduler : INhBackgroundOperationScheduler
    {
        public Task<NhBackgroundOperationScheduleResult> EnqueueAsync(
            Guid operationId,
            int dispatchGeneration,
            string queue,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NhBackgroundOperationScheduleResult($"job-{operationId:N}-{dispatchGeneration}"));
        }

        public Task<bool> DeleteAsync(string schedulerJobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<NhBackgroundOperationExecutionState?> GetStateAsync(
            string schedulerJobId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NhBackgroundOperationExecutionState?>(null);
        }
    }

    private sealed class RefreshDbContext(DbContextOptions<RefreshDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddNewHeapBackgroundOperations();
        }
    }
}
