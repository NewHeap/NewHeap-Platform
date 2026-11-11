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

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A host with background operations on an in-memory context of its own, live updates at the default
/// hub path and the bearer setup of a NewHeap host: its message handler reads the query token for the
/// hub, and its token validation applies the registered session validator (<see cref="HubTestSessions"/>)
/// as NewHeap's bearer setup does. Shared by the hub's authentication refresh and session tests, which
/// need no relational database: no operation is stored, and the host's startup check reads an in-memory
/// context.
/// </summary>
internal static class BackgroundOperationHubTestHost
{
    public const string HubPath = "/hub/background-operations";

    /// <summary>The claim NewHeap's tokens carry the user's security stamp in.</summary>
    public const string SecurityStampClaimType = "AspNet.Identity.SecurityStamp";

    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("background-operation-hub-tests-signing-key-2026-11"));

    /// <summary>
    /// Starts the host. <paramref name="clock"/> becomes the application's clock when given; otherwise
    /// the host runs on the system clock.
    /// </summary>
    public static async Task<WebApplication> StartAsync(
        HubTestSessions sessions,
        HubTestDivisions divisions,
        Action<NhBackgroundOperationsOptions>? configure = null,
        TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (clock is not null)
        {
            builder.Services.AddSingleton(clock);
        }

        var databaseName = Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<HubTestDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        builder.Services.AddNewHeapBackgroundOperations<HubTestDbContext>(operations =>
        {
            operations.Options.DispatchWorkersEnabled = false;
            operations.Options.ReconciliationEnabled = false;
            operations.Options.CleanupEnabled = false;
            operations.Options.UserNotificationProjectionEnabled = false;
            operations.Options.LiveUpdatesEnabled = true;
            configure?.Invoke(operations.Options);
        });
        builder.Services.AddSingleton(divisions);
        builder.Services.AddNewHeapDivisionAccess<HubTestDivisionAccess>();
        builder.Services.AddSingleton<INhAuthenticationSessionValidator>(sessions);
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
                    OnTokenValidated = async context =>
                    {
                        if (!await sessions.ValidateAsync(context.Principal!, context.HttpContext.RequestAborted))
                        {
                            context.Fail("The authentication session is no longer valid.");
                        }
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

    /// <summary>A token of the user that carries <paramref name="stamp"/> as its security stamp.</summary>
    public static string CreateToken(Guid userId, TimeSpan? lifetime = null, string stamp = HubTestSessions.FirstStamp)
    {
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(SecurityStampClaimType, stamp)
            ]),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }

    /// <summary>A token of the user that expired well beyond the validation's clock skew.</summary>
    public static string CreateExpiredToken(Guid userId)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(SecurityStampClaimType, HubTestSessions.FirstStamp)
            ]),
            IssuedAt = now.AddHours(-2),
            NotBefore = now.AddHours(-2),
            Expires = now.AddHours(-1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }

    /// <summary>
    /// Negotiates and opens a WebSocket connection to the hub with a query token, as a browser does, and
    /// unless <paramref name="waitUntilJoined"/> is false waits until the hub handles its messages, so the
    /// connection is in its groups and its session is watched.
    /// </summary>
    public static async Task<HubSocketConnection> ConnectAsync(
        TestServer server,
        string token,
        Guid? divisionId = null,
        bool waitUntilJoined = true)
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
        if (waitUntilJoined)
        {
            await WaitUntilHandledAsync(connection);
        }

        return new HubSocketConnection(socket, connection, connectionToken);
    }

    /// <summary>
    /// Waits until the hub answered an unknown invocation: it handles messages only after
    /// <c>OnConnectedAsync</c>, so the connection is then in its groups.
    /// </summary>
    public static async Task WaitUntilHandledAsync(JsonHubSocket connection)
    {
        await connection.SendAsync("""{"type":1,"invocationId":"joined","target":"NotAHubMethod","arguments":[]}""");
        (await connection.ReceiveMessageAsync(type: 3)).GetProperty("invocationId").GetString().Should().Be("joined");
    }

    /// <summary>
    /// Presents <paramref name="token"/> at the hub's refresh endpoint for the connection, as the SignalR
    /// 11 clients do: a POST that names the connection in the <c>X-SignalR-Connection-Token</c> header
    /// (ASP.NET Core 11.0.0 reads it there, not from the query string), with the renewed token in the
    /// Authorization header and the <c>X-Requested-With</c> header every SignalR request carries.
    /// </summary>
    public static async Task<HttpResponseMessage> RefreshAsync(TestServer server, string connectionToken, string token)
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{HubPath}/refresh");
        request.Headers.Add("X-SignalR-Connection-Token", connectionToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(request);
    }

    public static async Task<Guid> PublishAsync(WebApplication app, Guid ownerId, Guid? divisionId)
    {
        var operationId = Guid.NewGuid();
        await app.Services.GetRequiredService<INhBackgroundOperationLiveUpdatePublisher>().PublishChangedAsync(
            ownerId,
            new NhBackgroundOperationChangedMessage(operationId, 1, 1, NhBackgroundOperationStatus.Running, divisionId));
        return operationId;
    }

    public static async Task<Guid> NextOperationIdAsync(HubSocketConnection connection)
    {
        var change = await connection.Connection.ReceiveMessageAsync(type: 1);
        change.GetProperty("target").GetString().Should().Be(nameof(INhBackgroundOperationHubClient.OperationChanged));
        return change.GetProperty("arguments")[0].GetProperty("operationId").GetGuid();
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

    private sealed class HubTestDbContext(DbContextOptions<HubTestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddNewHeapBackgroundOperations();
        }
    }

    /// <summary>The host's division access, answered from the test's <see cref="HubTestDivisions"/>.</summary>
    private sealed class HubTestDivisionAccess(HubTestDivisions divisions) : INhDivisionAccess
    {
        public ValueTask<bool> HasAccessAsync(NhActorRef actor, Guid divisionId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(divisions.HasAccess(actor, divisionId));
        }
    }
}

/// <summary>A WebSocket connection to the hub, its JSON hub protocol and its SignalR connection token.</summary>
internal sealed record HubSocketConnection(WebSocket Socket, JsonHubSocket Connection, string ConnectionToken) : IDisposable
{
    public void Dispose()
    {
        Socket.Dispose();
    }
}

/// <summary>
/// The test's session validator, which the host's token validation and the hub's session check both
/// ask, as NewHeap's validator: it refuses a removed user, as a deleted one, and a principal whose
/// security stamp the user no longer has, as after a password change or a revocation of their sessions.
/// Every user starts with <see cref="FirstStamp"/>. A test waits for the next validation of a user with
/// <see cref="NextValidation"/>.
/// </summary>
internal sealed class HubTestSessions : INhAuthenticationSessionValidator
{
    public const string FirstStamp = "s1";

    private readonly object _gate = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _stamps = new(StringComparer.Ordinal);
    private readonly List<(string UserId, TaskCompletionSource<string> Validated)> _waiters = [];

    /// <summary>Refuses every principal of the user from now on, as for a deleted user.</summary>
    public void Remove(Guid userId)
    {
        lock (_gate)
        {
            _removed.Add(userId.ToString());
        }
    }

    /// <summary>
    /// The security stamps the user's principals may carry from now on; only one, except while a test
    /// lets the old stamp count until a connection refreshed.
    /// </summary>
    public void Accept(Guid userId, params string[] stamps)
    {
        lock (_gate)
        {
            _stamps[userId.ToString()] = stamps;
        }
    }

    /// <summary>
    /// Completes with the security stamp of the principal at the next validation of the user, accepted
    /// or not. Call it before what causes the validation.
    /// </summary>
    public Task<string> NextValidation(Guid userId)
    {
        var validated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add((userId.ToString(), validated));
        }

        return validated.Task;
    }

    public Task<bool> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var stamp = principal.FindFirstValue(BackgroundOperationHubTestHost.SecurityStampClaimType) ?? string.Empty;
        bool valid;
        List<TaskCompletionSource<string>> validated;
        lock (_gate)
        {
            var stamps = _stamps.TryGetValue(userId, out var accepted) ? accepted : [FirstStamp];
            valid = !_removed.Contains(userId) && stamps.Contains(stamp, StringComparer.Ordinal);
            validated = _waiters.Where(waiter => waiter.UserId == userId).Select(waiter => waiter.Validated).ToList();
            _waiters.RemoveAll(waiter => waiter.UserId == userId);
        }

        foreach (var waiter in validated)
        {
            waiter.TrySetResult(stamp);
        }

        return Task.FromResult(valid);
    }
}

/// <summary>The divisions each user may use; the test grants and revokes them.</summary>
internal sealed class HubTestDivisions
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

/// <summary>
/// The application's clock of a hub test: it stands still until the test moves it, and fires the timers
/// created on it, such as the hub's periodic session check, when the test moves past their due time. A
/// periodic timer is due again one period later. It starts at the current time, so the tokens a test
/// issues are valid on it.
/// </summary>
internal sealed class HubTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private int _timersCreated;

    /// <summary>How many timers were created on this clock, also those that are disposed by now.</summary>
    public int TimersCreated
    {
        get
        {
            lock (_gate)
            {
                return _timersCreated;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timersCreated++;
        }

        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that is due by then once.</summary>
    public void Advance(TimeSpan time)
    {
        List<ClockTimer> due;
        lock (_gate)
        {
            _now += time;
            due = _timers.Where(timer => timer.DueAt <= _now).ToList();
            foreach (var timer in due)
            {
                timer.Rearm();
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ClockTimer(HubTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset DueAt { get; private set; }

        public void Fire()
        {
            callback(state);
        }

        /// <summary>Schedules a periodic timer one period after now, and removes a one-shot timer.</summary>
        public void Rearm()
        {
            if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
            {
                clock._timers.Remove(this);
                return;
            }

            DueAt = clock._now + _period;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                _period = period;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock._now + dueTime;
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
