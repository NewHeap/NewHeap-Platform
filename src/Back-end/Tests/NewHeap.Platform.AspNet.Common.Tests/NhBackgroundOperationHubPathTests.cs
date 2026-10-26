using System.Net;
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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Actors;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// FB13: the live-update hub of background operations may lie at any absolute application path
/// below the root outside their own API, not only below <c>/hub</c>. A host with the hub at
/// <c>/realtime/operations</c> starts, a WebSocket connects there as a browser's does, with the
/// bearer token in the query string, and receives the owner's operation change; the default path is
/// not mapped, and the operations' API does not read a query token. The test needs no relational
/// database: no operation is stored, and the host's startup check reads an in-memory context.
/// </summary>
public sealed class NhBackgroundOperationHubPathTests
{
    private const string MovedHubPath = "/realtime/operations";
    private const char RecordSeparator = '\u001e';
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("background-operation-hub-path-tests-signing-key-2026"));
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_hub_outside_hub_starts_connects_with_a_query_token_and_receives_the_owners_changes()
    {
        await using var app = await StartAsync();
        var server = app.GetTestServer();
        var ownerId = Guid.NewGuid();
        var token = CreateToken(ownerId);
        using var client = server.CreateClient();

        using var withoutToken = await client.PostAsync($"{MovedHubPath}/negotiate?negotiateVersion=1", null);
        using var defaultPath = await client.PostAsync($"/hub/background-operations/negotiate?negotiateVersion=1&access_token={token}", null);
        using var api = await client.GetAsync($"/background-operations?access_token={token}");
        using var negotiated = await client.PostAsync($"{MovedHubPath}/negotiate?negotiateVersion=1&access_token={token}", null);

        withoutToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        defaultPath.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // The operations' API takes its token from the Authorization header only.
        api.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        negotiated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var negotiation = JsonDocument.Parse(await negotiated.Content.ReadAsStringAsync());
        var connectionToken = negotiation.RootElement.GetProperty("connectionToken").GetString();

        using var socket = await server.CreateWebSocketClient().ConnectAsync(
            new Uri(server.BaseAddress, $"{MovedHubPath}?id={connectionToken}&access_token={token}"),
            CancellationToken.None);
        var connection = new JsonHubSocket(socket);
        await connection.SendAsync("""{"protocol":"json","version":1}""");
        (await connection.ReceiveAsync()).Should().Be("{}");
        // The hub handles messages only after OnConnectedAsync, so once this unknown invocation is
        // answered, the connection is in its owner's group.
        await connection.SendAsync("""{"type":1,"invocationId":"joined","target":"NotAHubMethod","arguments":[]}""");
        (await connection.ReceiveMessageAsync(type: 3)).GetProperty("invocationId").GetString().Should().Be("joined");

        var operationId = Guid.NewGuid();
        await app.Services.GetRequiredService<INhBackgroundOperationLiveUpdatePublisher>().PublishChangedAsync(
            ownerId,
            new NhBackgroundOperationChangedMessage(operationId, 3, 7, NhBackgroundOperationStatus.Running));

        var change = await connection.ReceiveMessageAsync(type: 1);
        change.GetProperty("target").GetString().Should().Be(nameof(INhBackgroundOperationHubClient.OperationChanged));
        change.GetProperty("arguments")[0].GetProperty("operationId").GetGuid().Should().Be(operationId);
    }

    /// <summary>
    /// A host with background operations on an in-memory context of its own, live updates at
    /// <see cref="MovedHubPath"/> and the bearer setup of a NewHeap host, whose message handler reads
    /// the query token for the hubs NewHeap maps.
    /// </summary>
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        var databaseName = Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<HubPathDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        builder.Services.AddNewHeapBackgroundOperations<HubPathDbContext>(operations =>
        {
            operations.Options.DispatchWorkersEnabled = false;
            operations.Options.ReconciliationEnabled = false;
            operations.Options.CleanupEnabled = false;
            operations.Options.UserNotificationProjectionEnabled = false;
            operations.Options.LiveUpdatesEnabled = true;
            operations.Options.HubPath = MovedHubPath;
        });
        builder.Services.AddNewHeapDivisionAccess<NoDivisionAccess>();
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
                    }
                };
            })
            // The hub and the controllers also accept the NewHeap Identity cookie.
            .AddCookie("Identity.Application");
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapNewHeapBackgroundOperations();
        await app.StartAsync();
        return app;
    }

    private static string CreateToken(Guid userId)
    {
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }

    /// <summary>
    /// The JSON hub protocol over a WebSocket, as a browser client speaks it: every message is a JSON
    /// record that ends with the record separator.
    /// </summary>
    private sealed class JsonHubSocket(WebSocket socket)
    {
        private readonly Queue<string> _records = new();
        private readonly StringBuilder _pending = new();

        public Task SendAsync(string record)
        {
            return socket.SendAsync(
                Encoding.UTF8.GetBytes(record + RecordSeparator),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }

        /// <summary>
        /// The next hub message of the given type; pings are skipped.
        /// </summary>
        public async Task<JsonElement> ReceiveMessageAsync(int type)
        {
            while (true)
            {
                using var message = JsonDocument.Parse(await ReceiveAsync());
                if (message.RootElement.GetProperty("type").GetInt32() == type)
                {
                    return message.RootElement.Clone();
                }
            }
        }

        public async Task<string> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(Patience);
            var buffer = new byte[4096];
            while (_records.Count == 0)
            {
                var received = await socket.ReceiveAsync(buffer, timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("The hub closed the connection.");
                }

                _pending.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                var text = _pending.ToString();
                var end = text.LastIndexOf(RecordSeparator);
                if (end < 0)
                {
                    continue;
                }

                foreach (var record in text[..end].Split(RecordSeparator))
                {
                    _records.Enqueue(record);
                }

                _pending.Clear();
                _pending.Append(text[(end + 1)..]);
            }

            return _records.Dequeue();
        }
    }

    private sealed class NoDivisionAccess : INhDivisionAccess
    {
        public ValueTask<bool> HasAccessAsync(NhActorRef actor, Guid divisionId, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(false);
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

    private sealed class HubPathDbContext(DbContextOptions<HubPathDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddNewHeapBackgroundOperations();
        }
    }
}
