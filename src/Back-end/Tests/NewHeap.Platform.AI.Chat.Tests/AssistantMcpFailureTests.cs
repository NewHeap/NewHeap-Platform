using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// How MCP failures are classified without a database: a request the credential stopped, or the
/// server refused, certainly did not run the tool, so an approved proposal ends execution-failed;
/// the renewal's own code survives a refused request; and stored secrets and secret resolvers fail
/// with codes that say whose problem it is.
/// </summary>
public sealed class AssistantMcpFailureTests
{
    private const string ServerUrl = "http://localhost/planning/mcp";

    [Fact]
    public async Task A_credential_that_cannot_be_obtained_stops_the_request_before_it_is_sent()
    {
        var provider = new ScriptedProvider
        {
            Credential = TaskResult<NhAssistantMcpCredential>.Failed(NhAssistantAdminErrorCodes.McpConnectionRequired, "Not connected.")
        };
        var inner = new ScriptedHandler(HttpStatusCode.OK);
        using var client = Client(provider, inner);

        var failure = await Assert.ThrowsAsync<NhAssistantMcpException>(() => client.PostAsync(ServerUrl, new StringContent("{}")));

        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionRequired, failure.Code);
        Assert.True(failure.ToolNotRun);
        Assert.True(NhAssistantMcpFailures.ToolNotRun(new HttpRequestException("wrapped", failure)));
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task A_refused_request_whose_renewal_fails_reports_the_renewals_code()
    {
        var provider = new ScriptedProvider
        {
            Renewable = true,
            Renewal = TaskResult<NhAssistantMcpCredential>.Failed(NhAssistantAdminErrorCodes.McpAuthorizationNotConfirmed, "Not confirmed.")
        };
        var inner = new ScriptedHandler(HttpStatusCode.Unauthorized);
        using var client = Client(provider, inner);

        var failure = await Assert.ThrowsAsync<NhAssistantMcpException>(() => client.PostAsync(ServerUrl, new StringContent("{}")));

        // The code of the failed renewal, not a generic unauthorized; the server refused the request.
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationNotConfirmed, failure.Code);
        Assert.True(failure.ToolNotRun);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task An_approved_mcp_call_that_failed_before_the_tool_ran_is_not_uncertain()
    {
        var descriptor = Descriptor("mcp.planning.lookup-order");
        var row = new AssistantToolInvocation { Id = Guid.NewGuid(), ToolId = descriptor.Id };
        using var turn = NhAssistantExecutionScope.EnterTurn(AssistantTestData.Turn("owner-1"));

        var stopped = await NhAssistantToolCallInterceptor.ExecuteAsync(
            descriptor,
            row,
            Guid.NewGuid(),
            Guid.NewGuid(),
            _ => throw new NhAssistantMcpException(NhAssistantAdminErrorCodes.McpTokenRequestFailed, "Renewal failed.") { ToolNotRun = true },
            CancellationToken.None);
        var refused = await NhAssistantToolCallInterceptor.ExecuteAsync(
            descriptor,
            row,
            Guid.NewGuid(),
            Guid.NewGuid(),
            _ => throw new HttpRequestException("Refused.", null, HttpStatusCode.Unauthorized),
            CancellationToken.None);
        var lost = await NhAssistantToolCallInterceptor.ExecuteAsync(
            descriptor,
            row,
            Guid.NewGuid(),
            Guid.NewGuid(),
            _ => throw new HttpRequestException("The connection was reset."),
            CancellationToken.None);
        var governed = await NhAssistantToolCallInterceptor.ExecuteAsync(
            Descriptor("projects.change-status"),
            row,
            Guid.NewGuid(),
            Guid.NewGuid(),
            _ => throw new NhAssistantMcpException(NhAssistantAdminErrorCodes.McpTokenRequestFailed, "Not an MCP tool.") { ToolNotRun = true },
            CancellationToken.None);

        Assert.False(stopped.Threw);
        Assert.Equal(NhAssistantAdminErrorCodes.McpTokenRequestFailed, stopped.Outcome.Code);
        Assert.False(refused.Threw);
        Assert.True(lost.Threw);
        // Only an MCP request can be known not to have reached the tool.
        Assert.True(governed.Threw);
    }

    [Fact]
    public async Task A_stored_secret_that_no_longer_decrypts_and_a_failing_resolver_have_their_own_codes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<ThrowingResolver>();
        await using var provider = services.BuildServiceProvider();
        var protector = new NhAssistantMcpSecretProtector(provider.GetRequiredService<IDataProtectionProvider>());
        var logs = new CapturingLoggerProvider();
        var secrets = new NhAssistantMcpSecretSource(
            protector,
            [new NhAssistantMcpSecretResolverRegistration("vault", typeof(ThrowingResolver))],
            provider,
            new LoggerFactory([logs]).CreateLogger<NhAssistantMcpSecretSource>());
        var stored = new AssistantMcpServer { Id = "planning", ProtectedSecret = protector.Protect("stored-token") };
        var lost = new AssistantMcpServer { Id = "planning", ProtectedSecret = "protected-with-a-lost-key" };

        var readable = await secrets.ResolveAsync(stored, CancellationToken.None);
        var unreadable = await secrets.ResolveAsync(lost, CancellationToken.None);
        var unavailable = await secrets.ResolveReferenceAsync("vault:planning/token", CancellationToken.None);

        Assert.Equal("stored-token", readable.Data);
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretUnreadable, NhAssistantResults.CodeOf(unreadable));
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretUnavailable, NhAssistantResults.CodeOf(unavailable));
        // The operators learn why: the lost key and the resolver's exception are logged as warnings.
        Assert.Contains(logs.Messages, message => message.Contains("can no longer be decrypted", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, message => message.Contains(ThrowingResolver.Failure, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("stored-token", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_callback_returns_with_its_code_and_localization_key_in_the_fragment()
    {
        var flowId = Guid.NewGuid();

        var failed = NhAssistantMcpOAuthEndpoints.Fragment(
            new NhAssistantMcpCallbackOutcome(flowId, null, NhAssistantAdminErrorCodes.McpAuthorizationFailed, "https://app.example/settings"));
        var received = NhAssistantMcpOAuthEndpoints.Fragment(
            new NhAssistantMcpCallbackOutcome(flowId, "one-time code", null, "https://app.example/settings"));

        Assert.Equal(
            "nh-mcp-error=" + NhAssistantAdminErrorCodes.McpAuthorizationFailed
                + "&nh-mcp-message-key=nh-assistant.errors." + NhAssistantAdminErrorCodes.McpAuthorizationFailed,
            failed);
        Assert.Equal($"nh-mcp-flow={flowId:D}&nh-mcp-code=one-time%20code", received);
    }

    private static HttpClient Client(ScriptedProvider provider, ScriptedHandler inner)
    {
        var server = new AssistantMcpServer
        {
            Id = "planning",
            Url = ServerUrl,
            AuthMode = NhAssistantMcpAuthModes.OAuth
        };
        var options = new OptionsMonitorStub(new NhAssistantOptions());
        var guard = new NhAssistantMcpHostGuard(options, new DevelopmentEnvironment());
        return new HttpClient(new NhAssistantMcpCredentialHandler(
            new NhAssistantMcpCredentialSource(provider, server, NhAssistantMcpCredentialSubject.Request),
            new Uri(ServerUrl),
            guard)
        {
            InnerHandler = inner
        });
    }

    private static NhAiToolDescriptor Descriptor(string id)
    {
        return new NhAiToolDescriptor(
            id,
            1,
            "A tool.",
            typeof(object),
            typeof(object),
            NhAiToolEffect.Mutation,
            NhAiToolExposure.Agent,
            true,
            ["app.assistant.access"]);
    }

    private sealed class ScriptedProvider : INhAssistantMcpCredentialProvider
    {
        public bool Renewable { get; init; }

        public TaskResult<NhAssistantMcpCredential> Credential { get; init; } = TaskResult<NhAssistantMcpCredential>.Succeeded(
            new NhAssistantMcpCredential(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer first" }));

        public TaskResult<NhAssistantMcpCredential> Renewal { get; init; } = TaskResult<NhAssistantMcpCredential>.Failed(
            NhAssistantAdminErrorCodes.McpUnauthorized,
            "No renewal.");

        public bool CanRenew => Renewable;

        public bool Supports(string authMode)
        {
            return true;
        }

        public ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(
            AssistantMcpServer server,
            NhAssistantMcpCredentialSubject subject,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Credential);
        }

        public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(
            AssistantMcpServer server,
            NhAssistantMcpCredentialSubject subject,
            NhAssistantMcpChallenge challenge,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Renewal);
        }

        public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
        {
            return TaskResult.Succeeded();
        }
    }

    private sealed class ScriptedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<AuthenticationHeaderValue?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers.Authorization);
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Unauthorized)
            {
                response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", "error=\"invalid_token\""));
            }
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingResolver : INhAssistantMcpSecretResolver
    {
        public const string Failure = "the vault is down";

        public ValueTask<TaskResult<string>> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            throw new TimeoutException(Failure);
        }
    }

    private sealed class OptionsMonitorStub(NhAssistantOptions value) : IOptionsMonitor<NhAssistantOptions>
    {
        public NhAssistantOptions CurrentValue => value;

        public NhAssistantOptions Get(string? name)
        {
            return value;
        }

        public IDisposable? OnChange(Action<NhAssistantOptions, string?> listener)
        {
            return null;
        }
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
