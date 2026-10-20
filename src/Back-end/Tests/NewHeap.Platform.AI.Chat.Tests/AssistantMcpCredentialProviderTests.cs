using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The public credential provider contract (D1, follow-up F4): an application registers its own
/// authentication mode with <c>AddMcpCredentialProvider</c>, administrators choose it, the provider
/// supplies the credential of every request, and the mode's ownership decides sharing and background use.
/// </summary>
public sealed partial class AssistantMcpCredentialTests
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_application_mode_supplies_the_credential_and_its_ownership_decides_background_use(AssistantTestProvider provider)
    {
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, servers: servers, assistant: assistant => assistant
            .AddMcpCredentialProvider<SignedRequestProvider>("signed-request")
            .AddMcpCredentialProvider<ExchangedTokenProvider>("token-exchange", NhAssistantMcpCredentialOwnership.Person));

        // The organization's own mode: every caller shares it and it may serve background runs.
        Assert.True((await CreateAsync(host, Server("signed-request") with { HeadlessUse = NhAssistantMcpHeadlessUses.Organization })).Success);
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal("Signed planning", LastAuthorization(servers));
        Assert.False(await IsPerCallerAsync(host));

        // A person's mode: each person's own credential, in background runs only with consent.
        Assert.Equal(["headlessUse"], Fields(await UpdateAsync(host, Server("token-exchange") with { HeadlessUse = NhAssistantMcpHeadlessUses.Organization })));
        Assert.True((await UpdateAsync(host, Server("token-exchange") with { HeadlessUse = NhAssistantMcpHeadlessUses.Personal })).Success);
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal("Bearer exchanged-for-administrator", LastAuthorization(servers));
        Assert.True(await IsPerCallerAsync(host));

        // A mode nobody registered is refused, and a registered one takes no secret.
        Assert.Equal(["authMode"], Fields(await UpdateAsync(host, Server("magic"))));
        Assert.True((await UpdateAsync(host, Server("token-exchange") with { Secret = "ignored" })).Success);
        Assert.Null((await FindServerAsync(host)).ProtectedSecret);

        // The provider decides which calls its credential may carry: a person's only theirs.
        var exchanged = host.Services.GetRequiredService<ExchangedTokenProvider>();
        var subject = new NhAssistantMcpCredentialSubject(true, Member, NhAiGrantSource.Delegated, null);
        var server = await FindServerAsync(host);
        Assert.True(exchanged.AuthorizeCall(server, subject, Call(NhAiGrantSource.Delegated)).Success);
        Assert.Equal(NhAssistantAdminErrorCodes.McpCredentialMismatch, Code(exchanged.AuthorizeCall(server, subject, Call(NhAiGrantSource.Agent))));
        Assert.Equal(
            new[] { ("signed-request", NhAssistantMcpCredentialOwnership.Organization), ("token-exchange", NhAssistantMcpCredentialOwnership.Person) },
            host.Services.GetRequiredService<NhAssistantMcpAuthModeCatalog>().Registered.Select(item => (item.Mode, item.Ownership)));
    }

    [Fact]
    public async Task Administrators_read_the_modes_the_application_registered()
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            assistantBuilder: assistant => assistant
                .AddMcpCredentialProvider<SignedRequestProvider>("signed-request")
                .AddMcpCredentialProvider<ExchangedTokenProvider>("token-exchange", NhAssistantMcpCredentialOwnership.Person));
        using var admin = app.CreateClient(admin: true);
        using var member = app.CreateClient();

        using var listed = await admin.GetAsync("/api/assistant/admin/mcp-auth-modes");
        using var refused = await member.GetAsync("/api/assistant/admin/mcp-auth-modes");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "signed-request:organization", "token-exchange:person" },
            document.RootElement.EnumerateArray().Select(item => item.GetProperty("mode").GetString() + ":" + item.GetProperty("ownership").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public void A_mode_is_registered_once_by_a_dash_case_name_that_no_built_in_mode_has()
    {
        var services = new ServiceCollection();

        services.AddNewHeapAssistant(assistant =>
        {
            Assert.Throws<ArgumentException>(() => assistant.AddMcpCredentialProvider<SignedRequestProvider>("Signed_Request"));
            Assert.Throws<ArgumentException>(() => assistant.AddMcpCredentialProvider<SignedRequestProvider>(NhAssistantMcpAuthModes.Bearer));
            Assert.Throws<ArgumentException>(() => assistant.AddMcpCredentialProvider<SignedRequestProvider>(new string('a', 41)));
            assistant.AddMcpCredentialProvider<SignedRequestProvider>("signed-request");
            Assert.Throws<InvalidOperationException>(() => assistant.AddMcpCredentialProvider<ExchangedTokenProvider>("signed-request"));
        });
    }

    /// <summary>Whether a turn's connection to the server carries a credential of its own per caller.</summary>
    private static async Task<bool> IsPerCallerAsync(AssistantTestHost host)
    {
        var server = await FindServerAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        var plan = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionPlanner>().PlanAsync(server, CancellationToken.None);
        Assert.True(plan.Success);
        return plan.Data!.IsPerCaller;
    }

    private static NhAiMcpCallContext Call(NhAiGrantSource grantSource)
    {
        var descriptor = new NhAiToolDescriptor(
            "mcp.planning.lookup-order",
            1,
            "Looks up an order.",
            typeof(object),
            typeof(object),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Agent,
            true,
            ["app.assistant.access"]);
        var invocation = new NhAiInvocationContext("assistant-agent:project-assistant", "assistant", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            GrantSource = grantSource
        };
        using var entered = NhAiMcpCallContext.Enter("planning", "Lookup_Order", descriptor, invocation);
        return NhAiMcpCallContext.Current!;
    }

    /// <summary>A credential of the organization: a signature of the server, shared by every caller.</summary>
    public sealed class SignedRequestProvider : INhAssistantMcpCredentialProvider
    {
        public bool CanRenew => false;

        public bool Supports(string authMode)
        {
            return authMode == "signed-request";
        }

        public ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(TaskResult<NhAssistantMcpCredential>.Succeeded(new NhAssistantMcpCredential(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Signed " + server.Id })));
        }

        public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAssistantMcpChallenge challenge, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(NhAssistantMcpCredentialProviders.Failed(NhAssistantAdminErrorCodes.McpUnauthorized));
        }

        public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
        {
            return TaskResult.Succeeded();
        }
    }

    /// <summary>A person's credential, as a token exchange would return one for the person.</summary>
    public sealed class ExchangedTokenProvider : INhAssistantMcpCredentialProvider
    {
        public bool CanRenew => true;

        public bool Supports(string authMode)
        {
            return authMode == "token-exchange";
        }

        public ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(TaskResult<NhAssistantMcpCredential>.Succeeded(
                NhAssistantMcpCredential.Bearer("exchanged-for-" + (subject.PersonActorId ?? "administrator"), DateTimeOffset.UtcNow.AddMinutes(5))));
        }

        public ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAssistantMcpChallenge challenge, CancellationToken cancellationToken)
        {
            return GetAsync(server, subject, cancellationToken);
        }

        public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
        {
            return NhAssistantMcpCredentialProviders.AuthorizePersonalCall(subject, call);
        }
    }
}
