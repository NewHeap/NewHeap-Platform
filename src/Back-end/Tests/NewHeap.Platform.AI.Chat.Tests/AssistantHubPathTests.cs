using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Authentication;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// LS27: the assistant hub maps on the host's root at <c>NhAssistantOptions.HubPath</c>, outside the
/// route prefix, and the NewHeap bearer setup reads the query token for that path, so a hub moved to
/// <c>/hubs/assistant</c> connects as a browser's WebSocket does; other endpoints never take a token
/// from the query string.
/// </summary>
public sealed class AssistantHubPathTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string MovedHubPath = "/hubs/assistant";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("assistant-hub-path-tests-signing-key-2026"));

    [Fact]
    public async Task A_hub_moved_to_another_path_connects_with_a_query_token_and_the_api_does_not_read_one()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.Configure<NhAssistantOptions>(options => options.HubPath = MovedHubPath);
                // The bearer setup of a NewHeap host: its message handler reads the query token for hubs.
                services.AddAuthentication(options =>
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
                    });
            });
        var token = CreateToken("user-1");

        await using var withToken = Connect(app, MovedHubPath + "?access_token=" + token);
        await withToken.StartAsync();
        await using var withoutToken = Connect(app, MovedHubPath);
        var refused = await Assert.ThrowsAnyAsync<HttpRequestException>(() => withoutToken.StartAsync());
        // The default path is not mapped once the hub moved.
        await using var oldPath = Connect(app, "/hub/assistant?access_token=" + token);
        var notMapped = await Assert.ThrowsAnyAsync<HttpRequestException>(() => oldPath.StartAsync());
        using var client = app.Server.CreateClient();
        using var api = await client.GetAsync("/api/assistant/status?access_token=" + token);

        Assert.Equal(HubConnectionState.Connected, withToken.State);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notMapped.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Contains(
            app.Services.GetServices<INhLibraryHubPath>(),
            hub => hub.HubPath == MovedHubPath);
    }

    private static HubConnection Connect(AssistantWebApplication app, string pathAndQuery)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, pathAndQuery), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            })
            .Build();
    }

    private static string CreateToken(string userId)
    {
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("permission", "assistant")
            ]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }
}
