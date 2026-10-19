using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// MCP credentials beyond a stored token: secret references resolved for every connection, basic
/// authentication, client certificates, the deprecated HTTP+SSE transport, and personal servers in
/// headless runs, which need the person's consent and carry a delegated token minted per request.
/// Secrets and tokens never reach a log, an audit event or a response.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantMcpCredentialTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ServerUrl = "http://localhost/planning/mcp";
    private const string LookupFunction = "mcp_planning_lookup_order";
    private const string Member = "member-1";
    private const string SecretKey = NhAssistantMcpSecretReferences.ConfigurationSection + ":planning-token";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Secret_references_and_basic_authentication_are_stored_without_the_secret_and_resolved_for_every_connection(
        AssistantTestProvider provider)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            [SecretKey] = "first-token",
            ["ConnectionStrings:Default"] = "database-password"
        });
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, servers: servers, configuration: configuration);

        Assert.True((await CreateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "config:planning-token" })).Success);
        var stored = await FindServerAsync(host);
        Assert.Null(stored.ProtectedSecret);
        Assert.Equal("config:planning-token", stored.SecretReference);
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal("Bearer first-token", LastAuthorization(servers));

        // A rotated value applies to the next connection without any change to the server.
        configuration[SecretKey] = "rotated-token";
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal("Bearer rotated-token", LastAuthorization(servers));

        Assert.True((await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "vault:planning/token" })).Success);
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal("Bearer from-vault", LastAuthorization(servers));

        // A stored secret replaces the reference.
        Assert.True((await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { Secret = "stored-token" })).Success);
        stored = await FindServerAsync(host);
        Assert.Null(stored.SecretReference);
        Assert.NotNull(stored.ProtectedSecret);

        var withoutUser = await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Basic) with { Secret = "pass-word" });
        var userWithColon = await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Basic) with { UserName = "plan:ner", Secret = "pass-word" });
        var basic = await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Basic) with { UserName = "planner", Secret = "pass-word" });

        Assert.Equal(["userName"], Fields(withoutUser));
        Assert.Equal(["userName"], Fields(userWithColon));
        Assert.True(basic.Success);
        Assert.Equal("planner", (await FindServerAsync(host)).UserName);
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("planner:pass-word")), LastAuthorization(servers));

        // A reference never names configuration outside the dedicated section, nor climbs a path.
        foreach (var reference in new[] { "config:ConnectionStrings:Default", "config:../ConnectionStrings", "config:a/../b", "Config:planning-token", "planning-token" })
        {
            Assert.Equal(["secretReference"], Fields(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = reference })));
        }
        Assert.Equal(["secretReference"], Fields(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { Secret = "x", SecretReference = "config:planning-token" })));
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid, Code(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "env:planning-token" })));
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid, Code(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "config:missing" })));
        // A secret store that could not resolve a placeholder leaves it as text: that is no secret.
        configuration[NhAssistantMcpSecretReferences.ConfigurationSection + ":unresolved"] = "${planning-vault-token}";
        configuration[NhAssistantMcpSecretReferences.ConfigurationSection + ":blank"] = "   ";
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid, Code(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "config:unresolved" })));
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid, Code(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "config:blank" })));
        // A rotation to an unresolved value stops the stored reference from connecting.
        Assert.True((await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "config:planning-token" })).Success);
        configuration[SecretKey] = "prefix-${planning-vault-token}";
        var unresolved = await TestAsync(host);
        Assert.False(unresolved.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid, unresolved.Code);
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretReferenceInvalid, Code(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { SecretReference = "vault:missing" })));
        AssertNoSecretLogged(host, "first-token", "rotated-token", "from-vault", "stored-token", "pass-word", "database-password");
    }

    [Fact]
    public async Task A_client_certificate_is_loaded_by_name_with_its_private_key_and_presented_for_mutual_tls()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=assistant-mcp-client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var directory = Directory.CreateTempSubdirectory("nh-mcp-certificates-");
        try
        {
            var pfx = Path.Combine(directory.FullName, "client.pfx");
            var publicOnly = Path.Combine(directory.FullName, "client.cer");
            await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, "pfx-password"));
            await File.WriteAllBytesAsync(publicOnly, certificate.Export(X509ContentType.Cert));
            var configuration = Configuration(new Dictionary<string, string?>
            {
                [NhAssistantMcpClientCertificates.Section + ":planning:Path"] = pfx,
                [NhAssistantMcpClientCertificates.Section + ":planning:Password"] = "pfx-password",
                [NhAssistantMcpClientCertificates.Section + ":public-only:Path"] = publicOnly
            });
            var servers = new InMemoryMcpServers();
            servers.Register(ServerUrl, () => servers.PlanningTools());
            await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, servers: servers, configuration: configuration);

            Assert.True((await CreateAsync(host, Server(NhAssistantMcpAuthModes.None) with { ClientCertificateReference = "planning" })).Success);
            Assert.True((await TestAsync(host)).Ok);
            var plan = servers.Connections.Last();
            Assert.Equal(certificate.Thumbprint, plan.ClientCertificate!.Thumbprint);
            Assert.True(plan.ClientCertificate.HasPrivateKey);
            var factory = new NhAssistantHttpMcpClientFactory(
                host.Services.GetRequiredService<NhAssistantMcpHostGuard>(),
                host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<NhAssistantOptions>>());
            using var handler = factory.CreateSocketsHandler(plan);
            Assert.Equal(certificate.Thumbprint, Assert.IsType<X509Certificate2>(Assert.Single(handler.SslOptions.ClientCertificates!)).Thumbprint);

            // A real TLS handshake with a server that requires a client certificate: the handler of the
            // plan presents it, a handler without one is refused. Only the test trusts the server.
            var presented = new ConcurrentQueue<string>();
            await using var mutualTls = await StartMutualTlsAsync(presented);
            var endpoint = mutualTls.Urls.First().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
            using (var withCertificate = factory.CreateSocketsHandler(plan))
            {
                withCertificate.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                using var client = new HttpClient(withCertificate, disposeHandler: false);
                using var accepted = await client.GetAsync(endpoint);
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            }
            using (var withoutCertificate = factory.CreateSocketsHandler(plan with { ClientCertificate = null }))
            {
                withoutCertificate.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                using var client = new HttpClient(withoutCertificate, disposeHandler: false);
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(endpoint));
            }
            Assert.Equal([certificate.Thumbprint], presented);

            Assert.Equal(NhAssistantAdminErrorCodes.McpCertificateReferenceInvalid, Code(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.None) with { ClientCertificateReference = "public-only" })));
            Assert.Equal(NhAssistantAdminErrorCodes.McpCertificateReferenceInvalid, Code(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.None) with { ClientCertificateReference = "missing" })));
            Assert.Equal(["clientCertificateReference"], Fields(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.None) with { ClientCertificateReference = "../planning" })));
            Assert.True((await UpdateAsync(host, Server(NhAssistantMcpAuthModes.None) with { ClientCertificateReference = string.Empty })).Success);
            Assert.Null((await FindServerAsync(host)).ClientCertificateReference);
            AssertNoSecretLogged(host, "pfx-password");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_auto_transport_falls_back_to_http_with_sse_and_keeps_every_request_on_the_configured_origin()
    {
        var tools = new InMemoryMcpServers();
        await using var legacy = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools(), legacySse: true);
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);

        Assert.True((await CreateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { Url = legacy.LegacySseUrl, Secret = "legacy-token" })).Success);
        var streamableOnly = await TestAsync(host);
        Assert.True((await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { Url = legacy.LegacySseUrl, Transport = NhAssistantMcpTransports.Auto })).Success);
        var auto = await TestAsync(host);

        Assert.False(streamableOnly.Ok);
        Assert.True(auto.Ok, auto.Code);
        Assert.Equal(2, auto.ToolCount);
        Assert.Equal(NhAssistantMcpTransports.Auto, (await FindServerAsync(host)).Transport);
        Assert.Contains(legacy.Requests, request => request.Method == HttpMethods.Get && request.Path == "/planning/sse");
        Assert.Contains(legacy.Requests, request => request.Method == HttpMethods.Post && request.Path == "/planning/message");
        Assert.All(legacy.Requests, request => Assert.Equal("Bearer legacy-token", request.Authorization));

        // A server that names another origin in its endpoint event never receives the credential there.
        var elsewhere = new ConcurrentQueue<string>();
        await using var other = await StartRawAsync(app => app.Run(context =>
        {
            elsewhere.Enqueue(context.Request.Path + " " + context.Request.Headers.Authorization);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        }));
        var otherOrigin = other.Urls.First().TrimEnd('/');
        await using var redirecting = await StartRawAsync(app => app.MapGet("/planning/sse", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: endpoint\ndata: " + otherOrigin + "/message?sessionId=1\n\n");
            await context.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        }));
        var redirectingUrl = redirecting.Urls.First().Replace("127.0.0.1", "localhost", StringComparison.Ordinal).TrimEnd('/') + "/planning/sse";
        Assert.True((await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { Url = redirectingUrl, Transport = NhAssistantMcpTransports.Auto })).Success);

        var blocked = await TestAsync(host);

        Assert.False(blocked.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, blocked.Code);
        Assert.Empty(elsewhere);
        Assert.Equal(["transport"], Fields(await UpdateAsync(host, Server(NhAssistantMcpAuthModes.Bearer) with { Transport = "sse" })));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_personal_server_serves_a_headless_run_only_after_the_persons_consent_with_a_delegated_token_per_request(
        AssistantTestProvider provider)
    {
        var tools = new InMemoryMcpServers();
        await using var remote = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools());
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Nothing to look up.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-lookup")
            .RespondWithText("Order A-100 is planned for Monday.");
        var issuer = new RecordingTokenIssuer();
        await using var host = await CreateHostAsync(provider, model: model, issuer: issuer, runs: true);
        await ConnectPersonalServerAsync(host, remote.StreamableUrl);
        Assert.All(remote.Requests, request => Assert.Equal("Bearer token-of-admin-1", request.Authorization));
        var beforeRuns = remote.Requests.Count;

        var withoutConsent = await RunTurnAsync(host, await StartRunAsync(host, Member), "Project PRJ-1 was created.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, withoutConsent.Outcome);
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
        Assert.Empty(issuer.Requests);
        Assert.Equal(beforeRuns, remote.Requests.Count);

        var listed = Assert.Single(await ListConsentAsync(host, Member));
        Assert.False(listed.AllowBackgroundUse);
        Assert.Equal([AgentId], listed.AgentIds);
        var consent = await SetConsentAsync(host, Member, allow: true);
        Assert.True(consent.Success);
        Assert.True(consent.Data!.AllowBackgroundUse);
        var changed = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpBackgroundUseChanged);
        Assert.Equal(("planning", Member, NhAssistantMcpConnectionService.AllowedResult), (changed.ObjectId, changed.ActorId, changed.ResultCode));

        var withConsent = await RunTurnAsync(host, await StartRunAsync(host, Member), "When is order A-100 planned?");

        Assert.Equal(NhAssistantTurnStatuses.Completed, withConsent.Outcome);
        Assert.Contains(model.Requests[1].Options!.Tools!, tool => tool.Name == LookupFunction);
        Assert.Equal(["lookup-order"], tools.Calls);
        var call = Assert.Single(host.Audit.Records, record => record.ToolId == "mcp.planning.lookup-order");
        Assert.Equal(NhAiOutcomeKind.Succeeded, call.Outcome);
        Assert.Equal(NhAiGrantSource.Delegated, call.GrantSource);
        // Every request carried its own token of the member, bound to the server's origin.
        Assert.All(issuer.Requests, request =>
        {
            Assert.Equal(Member, request.PersonActorId);
            Assert.Equal(remote.Origin, request.Audience);
            Assert.Equal(NhAiMcpDelegatedTokenDefaults.DefaultLifetime, request.Lifetime);
        });
        Assert.Contains(issuer.Requests, request => request.Marker == call.InvocationId.ToString("D"));
        var sent = remote.Requests.Skip(beforeRuns).Select(request => request.Authorization).ToArray();
        Assert.NotEmpty(sent);
        Assert.All(sent, authorization => Assert.Contains(authorization, issuer.Issued.Select(token => "Bearer " + token)));
        Assert.Equal(sent.Length, sent.Distinct(StringComparer.Ordinal).Count());
        var issuedEvents = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.McpDelegatedTokenIssued).ToArray();
        Assert.Equal(issuer.Issued.Count, issuedEvents.Length);
        Assert.All(issuedEvents, evt => Assert.Equal(NhAssistantMcpForwardedTokenCredentialProvider.IssuedResult, evt.ResultCode));
        Assert.Contains(issuedEvents, evt => evt.ToolId == "mcp.planning.lookup-order" && evt.InvocationId == call.InvocationId);
        AssertNoSecretLogged(host, [.. issuer.Issued]);

        // Consent holds for the server as configured: another address needs consent again.
        Assert.True((await UpdateAsync(host, PersonalServer(remote.StreamableUrl + "/v2"))).Success);
        Assert.False(Assert.Single(await ListConsentAsync(host, Member)).AllowBackgroundUse);
    }

    [Fact]
    public async Task A_withdrawn_consent_refuses_the_deferred_execution_of_a_personal_server_proposal()
    {
        var tools = new InMemoryMcpServers();
        await using var remote = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools());
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("mcp_planning_reschedule", new { order = "A-100", day = "Friday" }, "call-reschedule")
            .RespondWithText("I proposed to move order A-100 to Friday.");
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, model: model, issuer: new RecordingTokenIssuer(), runs: true);
        await ConnectPersonalServerAsync(host, remote.StreamableUrl, ("reschedule", NhAssistantMcpToolEffects.Mutation));
        Assert.True((await SetConsentAsync(host, Member, allow: true)).Success);
        var run = await StartRunAsync(host, Member);
        await RunTurnAsync(host, run, "Move order A-100 to Friday.");
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        await ApproveAsync(host, approval);

        Assert.True((await SetConsentAsync(host, Member, allow: false)).Success);
        var execution = await ExecuteAsync(host, run, approval.Id);

        Assert.False(execution.Succeeded);
        Assert.Equal(NhAssistantAdminErrorCodes.McpConsentRequired, execution.ResultCode);
        Assert.Empty(tools.Calls);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpBackgroundUseChanged
            && evt.ResultCode == NhAssistantMcpConnectionService.WithdrawnResult);
    }

    [Fact]
    public async Task No_delegated_token_is_sent_when_the_issuer_refuses_misbinds_or_fails_or_the_host_could_accept_it()
    {
        var tools = new InMemoryMcpServers();
        await using var remote = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools());
        foreach (var mode in new[] { TokenMode.Refuse, TokenMode.Unbound, TokenMode.LongLived, TokenMode.Throw, TokenMode.HostAccepts })
        {
            var model = new NhAiScriptedChatClient().RespondWithText("Nothing to look up.");
            var issuer = new RecordingTokenIssuer { Mode = mode };
            await using var host = await CreateHostAsync(
                AssistantTestProvider.PostgreSql,
                model: model,
                issuer: issuer,
                runs: true,
                configure: mode == TokenMode.HostAccepts
                    ? services =>
                    {
                        // A scheme whose events come from a type cannot be restricted.
                        services.AddSingleton<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents>();
                        services.AddAuthentication().AddJwtBearer("Typed", options => options.EventsType = typeof(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents));
                    }
                    : null);
            await ConnectPersonalServerAsync(host, remote.StreamableUrl);
            Assert.True((await SetConsentAsync(host, Member, allow: true)).Success);
            var beforeRun = remote.Requests.Count;

            var turn = await RunTurnAsync(host, await StartRunAsync(host, Member), "Project PRJ-1 was created.");

            Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Outcome);
            Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
            Assert.Equal(beforeRun, remote.Requests.Count);
            var refused = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpDelegatedTokenIssued);
            Assert.Equal(NhAssistantAdminErrorCodes.McpDelegatedTokenUnavailable, refused.ResultCode);
            Assert.Equal(Member, refused.ActorId);
            Assert.Contains(host.Logs.Messages, message => message.Contains("planning was skipped for this turn (" + NhAssistantAdminErrorCodes.McpDelegatedTokenUnavailable + ")", StringComparison.Ordinal));
            Assert.Equal(mode == TokenMode.HostAccepts ? 0 : 1, issuer.Requests.Count);
            AssertNoSecretLogged(host, [.. issuer.Issued, RecordingTokenIssuer.ThrownSecret]);
        }
    }

    [Fact]
    public async Task A_delegated_token_is_sent_only_for_a_call_authorized_with_the_rights_of_its_person()
    {
        var issuer = new RecordingTokenIssuer();
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, issuer: issuer);
        await using var scope = host.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetServices<INhAssistantMcpCredentialProvider>()
            .Last(candidate => candidate.Supports(NhAssistantMcpAuthModes.ForwardUserToken));
        var server = new AssistantMcpServer
        {
            Id = "planning",
            Url = ServerUrl,
            AuthMode = NhAssistantMcpAuthModes.ForwardUserToken,
            HeadlessUse = NhAssistantMcpHeadlessUses.Personal
        };
        var inner = new RecordingHandler();
        using var client = new HttpClient(new NhAssistantMcpCredentialHandler(
            new NhAssistantMcpCredentialSource(provider, server, new NhAssistantMcpCredentialSubject(true, "accountable-1", NhAiGrantSource.Accountable, null)),
            new Uri(ServerUrl),
            host.Services.GetRequiredService<NhAssistantMcpHostGuard>())
        {
            InnerHandler = inner
        });
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

        var refused = new List<Exception?>();
        foreach (var grant in new[] { NhAiGrantSource.Delegated, NhAiGrantSource.Agent, NhAiGrantSource.None })
        {
            using (NhAiMcpCallContext.Enter("planning", "Lookup_Order", descriptor, Invocation(grant)))
            {
                refused.Add(await Record.ExceptionAsync(() => client.PostAsync(ServerUrl, new StringContent("{}"))));
            }
        }
        var accountable = Invocation(NhAiGrantSource.Accountable);
        using (NhAiMcpCallContext.Enter("planning", "Lookup_Order", descriptor, accountable))
        {
            using var allowed = await client.PostAsync(ServerUrl, new StringContent("{}"));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        Assert.All(refused, exception => Assert.Equal(NhAssistantAdminErrorCodes.McpCredentialMismatch, NhAssistantMcpFailures.Classify(exception!)));
        var request = Assert.Single(issuer.Requests);
        Assert.Equal(("accountable-1", accountable.InvocationId.ToString("D"), "http://localhost"), (request.PersonActorId, request.Marker, request.Audience));
        Assert.Equal("Bearer " + Assert.Single(issuer.Issued), Assert.Single(inner.Requests));
    }

    [Fact]
    public async Task Startup_fails_for_a_forwarded_token_lifetime_beyond_five_minutes()
    {
        await using var host = await CreateHostAsync(
            AssistantTestProvider.PostgreSql,
            mcp: mcp => mcp.ForwardedTokenLifetime = TimeSpan.FromMinutes(6));
        var validator = host.Services.GetServices<IHostedService>().Single(service => service.GetType().Name == "NhAssistantStartupValidator");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ForwardedTokenLifetime", failure.Message, StringComparison.Ordinal);
    }

    private static NhAssistantMcpServerInput Server(string authMode)
    {
        return new NhAssistantMcpServerInput("planning", "Planning", ServerUrl, authMode, null, null, null, true);
    }

    private static NhAssistantMcpServerInput PersonalServer(string url)
    {
        return Server(NhAssistantMcpAuthModes.ForwardUserToken) with
        {
            Url = url,
            HeadlessUse = NhAssistantMcpHeadlessUses.Personal
        };
    }

    private static IConfigurationRoot Configuration(Dictionary<string, string?> values)
    {
        values["NewHeap:AI:Assistant:Enabled"] = "true";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient? model = null,
        InMemoryMcpServers? servers = null,
        IConfiguration? configuration = null,
        RecordingTokenIssuer? issuer = null,
        bool runs = false,
        Action<IServiceCollection>? configure = null,
        Action<NhAssistantMcpOptions>? mcp = null)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model ?? new NhAiScriptedChatClient(),
            configure: services =>
            {
                if (servers is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers));
                }
                if (configuration is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(configuration));
                }
                if (issuer is not null)
                {
                    services.AddSingleton(issuer);
                    services.AddNewHeapPlatformAIMcpDelegatedTokens(tokens => tokens.UseIssuer<RecordingTokenIssuer.Adapter>());
                }
                if (runs)
                {
                    services.AddSingleton(new TestSubjects());
                    services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                }
                configure?.Invoke(services);
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddMcpSecretResolver<VaultResolver>("vault")
                    .ConfigureMcp(options =>
                    {
                        options.RequireHttps = true;
                        options.ForwardUserTokenHosts = ["localhost"];
                        mcp?.Invoke(options);
                    });
                if (runs)
                {
                    assistant
                        .AddSubjectType<TestSubjectResolver>()
                        .AddApprovalPolicy("project-review", policy => policy
                            .Lifetime(TimeSpan.FromDays(3))
                            .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                            .Mode(NhAiApprovalMode.Deferred)
                            .BindTo(NhAiApprovalBinding.ToolContract))
                        .ConfigureAgent(AgentId, agent => agent
                            .DelegateFromInitiator(true)
                            .UseApprovalPolicy("project-review"));
                }
            });
    }

    private static async Task ConnectPersonalServerAsync(
        AssistantTestHost host,
        string url,
        (string RemoteName, string Effect)? tool = null)
    {
        var (remoteName, effect) = tool ?? ("Lookup_Order", NhAssistantMcpToolEffects.ReadOnly);
        await AdministerAsync(host, async administration =>
        {
            Assert.True((await administration.CreateAsync(PersonalServer(url), "admin-1", CancellationToken.None)).Success);
            Assert.True((await administration.SyncAsync("planning", "admin-1", CancellationToken.None)).Success);
            var updated = await administration.UpdateToolAsync("planning", remoteName, true, effect, null, "admin-1", CancellationToken.None);
            Assert.True(updated.Success);
            return updated;
        });
        await using var scope = host.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var assigned = await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                ["planning"],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
    }

    private static Task<TaskResult<AssistantMcpServer>> CreateAsync(AssistantTestHost host, NhAssistantMcpServerInput input)
    {
        return AdministerAsync(host, administration => administration.CreateAsync(input, "admin-1", CancellationToken.None));
    }

    private static Task<TaskResult<AssistantMcpServer>> UpdateAsync(AssistantTestHost host, NhAssistantMcpServerInput input)
    {
        return AdministerAsync(host, administration => administration.UpdateAsync(input, "admin-1", CancellationToken.None));
    }

    private static async Task<NhAssistantMcpTestResult> TestAsync(AssistantTestHost host)
    {
        var result = await AdministerAsync(host, administration => administration.TestAsync("planning", CancellationToken.None));
        return result.Data!;
    }

    private static async Task<T> AdministerAsync<T>(AssistantTestHost host, Func<NhAssistantMcpAdministration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>());
    }

    private static async Task<AssistantMcpServer> FindServerAsync(AssistantTestHost host)
    {
        return (await host.Services.GetRequiredService<NhAssistantAdminStore>().FindMcpServerAsync("planning", CancellationToken.None))!;
    }

    private static async Task<IReadOnlyList<NhAssistantMcpPersonalServer>> ListConsentAsync(AssistantTestHost host, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var user = AssistantTestHost.EnterUser(scope.ServiceProvider, actorId).User;
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>().ListAsync(actorId, user, CancellationToken.None);
    }

    private static async Task<TaskResult<NhAssistantMcpPersonalServer>> SetConsentAsync(AssistantTestHost host, string actorId, bool allow)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var user = AssistantTestHost.EnterUser(scope.ServiceProvider, actorId).User;
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
            .SetBackgroundUseAsync(actorId, user, "planning", allow, CancellationToken.None);
    }

    private static string LastAuthorization(InMemoryMcpServers servers)
    {
        return servers.Connections.Last().Credentials is { } credentials
            ? Header(credentials)
            : servers.Connections.Last().Headers["Authorization"];

        static string Header(NhAssistantMcpCredentialSource credentials)
        {
            var credential = credentials.GetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return credential.Data!.Headers["Authorization"];
        }
    }

    private static string[] Fields(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems()
            .Select(item => item.Name)
            .Where(name => name.StartsWith(NhAssistantValidationErrors.FieldPrefix, StringComparison.Ordinal))
            .Select(name => name[NhAssistantValidationErrors.FieldPrefix.Length..])
            .ToArray();
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static void AssertNoSecretLogged(AssistantTestHost host, params string[] secrets)
    {
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
        {
            Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(host.Business.Events, evt => evt.ToString().Contains(secret, StringComparison.Ordinal));
        }
    }

    private static NhAiInvocationContext Invocation(NhAiGrantSource grantSource)
    {
        return new NhAiInvocationContext("assistant-agent:project-assistant", "assistant", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            GrantSource = grantSource
        };
    }

    /// <summary>
    /// An https server on loopback that requires a client certificate and records the thumbprint of
    /// each one presented.
    /// </summary>
    private static async Task<WebApplication> StartMutualTlsAsync(ConcurrentQueue<string> presented)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Reloaded from PKCS#12 so the platform's TLS stack can use the private key.
        var serverCertificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "server"), "server");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(serverCertificate, https =>
        {
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (_, _, _) => true;
        })));
        var app = builder.Build();
        app.MapGet("/", (HttpContext context) =>
        {
            presented.Enqueue(context.Connection.ClientCertificate?.Thumbprint ?? string.Empty);
            return Results.Ok();
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartRawAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    private sealed record StartedRun(AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, string initiator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subject = new NhAiSubjectRef("project", Guid.NewGuid().ToString());
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            InitiatorActorId = initiator,
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        return new StartedRun(run, new NhAssistantAgentTurnContext(
            thread,
            info,
            options,
            run.Id,
            run.TriggerId,
            initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false));
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        return await runner.RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                run.AgentTurn,
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static async Task<NhAssistantProposalExecutionResult> ExecuteAsync(AssistantTestHost host, StartedRun run, Guid approvalId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        return await runner.ExecuteProposalAsync(
            new NhAssistantProposalExecutionRequest(run.AgentTurn, approvalId),
            CancellationToken.None);
    }

    private static async Task ApproveAsync(AssistantTestHost host, AssistantApproval approval)
    {
        var approved = await host.Services.GetRequiredService<NhAssistantRunStore>().TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Approved,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = "approver-1",
                DecidedAt = DateTimeOffset.UtcNow,
                ApprovalExpiresAt = approval.ExpiresAt
            },
            CancellationToken.None);
        Assert.True(approved);
    }

    /// <summary>
    /// Answers every request with <c>200</c> and records its credential.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>A vault with one secret.</summary>
    private sealed class VaultResolver : INhAssistantMcpSecretResolver
    {
        public ValueTask<TaskResult<string>> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(name == "planning/token"
                ? TaskResult<string>.Succeeded("from-vault")
                : TaskResult<string>.Failed("vault-not-found", "No such secret."));
        }
    }

    private enum TokenMode
    {
        Issue,
        Refuse,
        Unbound,
        LongLived,
        Throw,
        HostAccepts
    }

    /// <summary>
    /// Issues unsigned JWTs with the requested binding, or misbehaves as <see cref="Mode"/> says.
    /// </summary>
    private sealed class RecordingTokenIssuer
    {
        public const string ThrownSecret = "issuer-internal-detail";

        public TokenMode Mode { get; init; } = TokenMode.Issue;

        public ConcurrentQueue<NhAiMcpDelegatedTokenRequest> Requests { get; } = new();

        public ConcurrentQueue<string> Issued { get; } = new();

        public TaskResult<NhAiMcpDelegatedToken> Issue(NhAiMcpDelegatedTokenRequest request)
        {
            Requests.Enqueue(request);
            if (Mode == TokenMode.Refuse)
            {
                return TaskResult<NhAiMcpDelegatedToken>.Failed(NhAiMcpDelegatedTokenDefaults.UnavailableCode, "The person may not sign in.");
            }
            if (Mode == TokenMode.Throw)
            {
                throw new InvalidOperationException(ThrownSecret);
            }
            var claims = new List<Claim> { new("sub", request.PersonActorId), new("jti", Guid.NewGuid().ToString("N")) };
            if (Mode != TokenMode.Unbound)
            {
                claims.Add(new Claim(NhAiMcpDelegatedTokenDefaults.MarkerClaimType, request.Marker));
                claims.Add(new Claim(NhAiMcpDelegatedTokenDefaults.AudienceClaimType, request.Audience));
            }
            var expires = DateTime.UtcNow.Add(Mode == TokenMode.LongLived ? TimeSpan.FromHours(1) : request.Lifetime);
            var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("tests", null, claims, DateTime.UtcNow, expires));
            Issued.Enqueue(token);
            return TaskResult<NhAiMcpDelegatedToken>.Succeeded(new NhAiMcpDelegatedToken(token, new DateTimeOffset(expires)));
        }

        public sealed class Adapter(RecordingTokenIssuer issuer) : INhAiMcpDelegatedTokenIssuer
        {
            public ValueTask<TaskResult<NhAiMcpDelegatedToken>> IssueAsync(NhAiMcpDelegatedTokenRequest request, CancellationToken cancellationToken)
            {
                return ValueTask.FromResult(issuer.Issue(request));
            }
        }
    }
}
