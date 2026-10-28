using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AI.Chat.AspNet;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Organization OAuth connections of MCP servers against a real authorization server and a protected
/// MCP server on loopback: discovery and pinning, client credentials with a secret or
/// <c>private_key_jwt</c>, step-up, the authorization code flow with its state, issuer and starter
/// checks, single-flight refresh with rotation, reauthorization, issuer changes and revocation. No
/// secret, code or token reaches a log or an audit event.
/// </summary>
public sealed partial class AssistantMcpOAuthTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string RedirectUri = "https://assistant.example/api/assistant/mcp/oauth/callback";
    private const string ClientSecret = "test-cc-secret-4711";
    private const string WebClientSecret = "test-web-secret-0815";

    /// <summary>
    /// Marks the async flow of the test that listens to the MCP counters.
    /// </summary>
    private static readonly AsyncLocal<object?> MeasuringTest = new();

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Client_credentials_connect_on_first_use_after_the_administrator_confirmed_discovery_and_step_up_widens_the_scopes(
        AssistantTestProvider provider)
    {
        // The meter is static and other test classes use MCP at the same time: count only what this
        // test's own flow records.
        var thisTest = new object();
        MeasuringTest.Value = thisTest;
        var measured = new System.Collections.Concurrent.ConcurrentQueue<(string Instrument, string Tags)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meters) =>
        {
            if (instrument.Meter.Name == "NewHeap.Platform.AI" && instrument.Name.StartsWith("nh.ai.mcp.", StringComparison.Ordinal))
            {
                meters.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (ReferenceEquals(MeasuringTest.Value, thisTest))
            {
                measured.Enqueue((instrument.Name, string.Join(',', tags.ToArray().Select(tag => tag.Key + "=" + tag.Value))));
            }
        });
        listener.Start();
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options => options.IssuerPath = "/tenants/a");
        authorization.RegisterClient("assistant-cc", ClientSecret);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization, resource => resource.ChallengeScope = "mcp:read");
        await using var host = await CreateHostAsync(provider);

        var discovered = await AdministerAsync(host, administration => administration.DiscoverAsync(mcp.Url, CancellationToken.None));
        Assert.True(discovered.Success);
        Assert.Equal(mcp.Url, discovered.Data!.Resource);
        Assert.Equal(authorization.Issuer, discovered.Data.AuthorizationServer.Issuer);
        Assert.Equal(["mcp:read"], discovered.Data.ChallengedScopes);
        Assert.True(discovered.Data.AuthorizationServer.SupportsS256);

        // Nothing is pinned without the administrator's confirmation, and a confidential client needs a secret.
        Assert.Equal(["oauth.confirmAuthorization"], Fields(await CreateAsync(host, ClientCredentials(mcp.Url, confirm: false))));
        Assert.Equal(["oauth.clientSecret"], Fields(await CreateAsync(host, ClientCredentials(mcp.Url) with
        {
            OAuth = new NhAssistantMcpOAuthInput("assistant-cc", null, null, null, null, ConfirmAuthorization: true)
        })));
        Assert.True((await CreateAsync(host, ClientCredentials(mcp.Url))).Success);
        var stored = await FindServerAsync(host);
        Assert.Equal(authorization.Issuer, stored.AuthorizationServerIssuer);
        Assert.Equal(mcp.Url, stored.ResourceUri);
        Assert.Equal("admin-1", stored.AuthorizationConfirmedBy);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerAuthorizationConfirmed && evt.ObjectId == "planning");
        Assert.NotNull(stored.ProtectedOAuthClientSecret);
        Assert.DoesNotContain(ClientSecret, stored.ProtectedOAuthClientSecret, StringComparison.Ordinal);

        // The first use obtains a token for the resource; the next uses reuse it.
        Assert.True((await TestAsync(host)).Ok);
        Assert.True((await TestAsync(host)).Ok);
        var request = Assert.Single(authorization.TokenRequests);
        Assert.Equal("client_credentials", request.GrantType);
        Assert.Equal(mcp.Url, request.Resource);
        Assert.Equal("client_secret_basic", request.ClientAuthentication);
        Assert.All(mcp.Resource.Requests, item => Assert.Equal("assistant-cc", item.Subject));
        var connection = (await FindOrganizationConnectionAsync(host))!;
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, connection.Status);
        Assert.Equal(NhAssistantMcpOAuthConnections.ClientCredentialsConnector, connection.ConnectedBy);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionConnected && evt.ObjectId == "planning");

        // Step-up: the server now asks for a scope the token lacks; a new token with the union follows.
        mcp.Resource.RequiredScopes = ["mcp:write"];
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal("mcp:write", authorization.TokenRequests.Last().Scope);
        Assert.Equal("mcp:write", (await FindOrganizationConnectionAsync(host))!.GrantedScopes);

        // Limiting the scopes is a change of the connection: its tokens are revoked and a scope
        // outside the administrator's list is never requested.
        var revocationsBefore = authorization.Revocations.Count;
        Assert.True((await UpdateAsync(host, ClientCredentials(mcp.Url, confirm: false) with
        {
            OAuth = new NhAssistantMcpOAuthInput(null, null, null, "mcp:read", null, ConfirmAuthorization: false)
        })).Success);
        Assert.Null(await FindOrganizationConnectionAsync(host));
        Assert.True(authorization.Revocations.Count > revocationsBefore);
        var tokenRequests = authorization.TokenRequests.Count;
        var refused = await TestAsync(host);
        Assert.False(refused.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpScopeNotAllowed, refused.Code);
        Assert.Equal("mcp:read", authorization.TokenRequests[tokenRequests].Scope);
        Assert.Equal(tokenRequests + 1, authorization.TokenRequests.Count);

        // Counted with outcome codes only.
        Assert.Contains(("nh.ai.mcp.token_requests", "grant=client_credentials,outcome=ok"), measured);
        Assert.Contains(("nh.ai.mcp.requests", "outcome=ok"), measured);
        Assert.Contains(("nh.ai.mcp.requests", "outcome=renewed"), measured);
        Assert.Contains(("nh.ai.mcp.requests", "outcome=" + NhAssistantAdminErrorCodes.McpScopeNotAllowed), measured);
        AssertNoSecretLogged(host, ClientSecret);
    }

    [Fact]
    public async Task Client_credentials_authenticate_with_a_private_key_jwt_signed_by_the_named_certificate()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=assistant-oauth-client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var directory = Directory.CreateTempSubdirectory("nh-mcp-oauth-");
        try
        {
            var pfx = Path.Combine(directory.FullName, "client.pfx");
            await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, "pfx-password"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NewHeap:AI:Assistant:Enabled"] = "true",
                    [NhAssistantMcpClientCertificates.Section + ":oauth-client:Path"] = pfx,
                    [NhAssistantMcpClientCertificates.Section + ":oauth-client:Password"] = "pfx-password"
                })
                .Build();
            await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
            authorization.RegisterClient("assistant-jwt", new RsaSecurityKey(certificate.GetRSAPublicKey()!));
            await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
            await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, configuration: configuration);

            var input = ClientCredentials(mcp.Url) with
            {
                ClientCertificateReference = "oauth-client",
                OAuth = new NhAssistantMcpOAuthInput("assistant-jwt", null, null, "mcp:read", null, ConfirmAuthorization: true)
            };
            Assert.True((await CreateAsync(host, input)).Success);
            Assert.True((await TestAsync(host)).Ok);

            var request = Assert.Single(authorization.TokenRequests);
            Assert.Equal("private_key_jwt", request.ClientAuthentication);
            Assert.Equal("assistant-jwt", request.ClientId);
            Assert.All(mcp.Resource.Requests, item => Assert.Equal("assistant-jwt", item.ClientId));
            AssertNoSecretLogged(host, "pfx-password");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_organization_authorization_completes_only_for_its_starter_and_each_callback_is_checked(
        AssistantTestProvider provider)
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider);
        Assert.Equal(["oauth.organizationAccountLabel"], Fields(await CreateAsync(host, Organization(mcp.Url) with
        {
            OAuth = new NhAssistantMcpOAuthInput("assistant-web", WebClientSecret, null, "mcp:read", null, ConfirmAuthorization: true)
        })));
        Assert.True((await CreateAsync(host, Organization(mcp.Url))).Success);

        // Without a connection the server cannot be used.
        var unconnected = await TestAsync(host);
        Assert.False(unconnected.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionRequired, unconnected.Code);

        var start = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        var query = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query);
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal(mcp.Url, query["resource"].ToString());
        Assert.Equal("mcp:read", query["scope"].ToString());
        Assert.Equal(RedirectUri, query["redirect_uri"].ToString());
        var state = query["state"].ToString();
        var redirect = await AuthorizeAsync(start.AuthorizationUrl);

        var outcome = await CallbackAsync(host, redirect);
        Assert.Null(outcome.FailureCode);
        Assert.Equal(start.FlowId, outcome.FlowId);
        var completionCode = outcome.CompletionCode!;
        var flow = await FindFlowAsync(host, start.FlowId);
        Assert.Equal(NhAssistantMcpOAuthStore.FlowExchanged, flow.Status);
        Assert.NotEqual(completionCode, flow.CompletionCodeHash);
        Assert.DoesNotContain(state, flow.StateHash, StringComparison.Ordinal);

        // A replayed callback, another person or a wrong code complete nothing; a non-administrator
        // cannot complete an organization connection.
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationExpired, (await CallbackAsync(host, redirect)).FailureCode);
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationInvalid, Code(await CompleteAsync(host, start.FlowId, completionCode, "admin-2")));
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationInvalid, Code(await CompleteAsync(host, start.FlowId, "guessed", "admin-1")));
        Assert.Equal(NhAssistantAdminErrorCodes.Forbidden, Code(await CompleteAsync(host, start.FlowId, completionCode, "admin-1", administrator: false)));
        var completed = await CompleteAsync(host, start.FlowId, completionCode, "admin-1");
        Assert.True(completed.Success);
        Assert.Equal(NhAssistantMcpConnectionOwners.Organization, completed.Data!.OwnerKind);
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationExpired, Code(await CompleteAsync(host, start.FlowId, completionCode, "admin-1")));
        flow = await FindFlowAsync(host, start.FlowId);
        Assert.Equal(NhAssistantMcpOAuthStore.FlowCompleted, flow.Status);
        Assert.Null(flow.ProtectedTokenResponse);
        Assert.Equal(string.Empty, flow.ProtectedCodeVerifier);

        var connection = (await FindOrganizationConnectionAsync(host))!;
        Assert.Equal("admin-1", connection.ConnectedBy);
        Assert.Equal("mcp:read", connection.GrantedScopes);
        Assert.NotNull(connection.ProtectedRefreshToken);
        Assert.True((await TestAsync(host)).Ok);
        Assert.All(mcp.Resource.Requests, item => Assert.Equal("remote-user", item.Subject));
        var accessToken = await AccessTokenAsync(host);

        // The iss of the redirect must name the flow's issuer, and an issuer that announces it must send it.
        authorization.Options.ResponseIssuerOverride = "https://attacker.example";
        var mixedUp = await StartAndCallBackAsync(host, authorization);
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationInvalid, mixedUp.FailureCode);
        authorization.Options.ResponseIssuerOverride = null;
        var withoutIssuer = await StartAndCallBackAsync(host, authorization, removeIssuer: true);
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationInvalid, withoutIssuer.FailureCode);
        authorization.Options.NextOutcome = NhAiTestAuthorizationOutcome.Deny;
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationFailed, (await StartAndCallBackAsync(host, authorization)).FailureCode);
        authorization.Options.NextOutcome = NhAiTestAuthorizationOutcome.Approve;

        // An expired flow is refused at the callback.
        var late = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        var lateRedirect = await AuthorizeAsync(late.AuthorizationUrl);
        await UpdateFlowAsync(host, late.FlowId, row => row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationExpired, (await CallbackAsync(host, lateRedirect)).FailureCode);
        // The failed flows did not touch the connection.
        Assert.Equal(accessToken, await AccessTokenAsync(host));

        // Too many unfinished authorizations of one person are refused.
        for (var index = 0; index < 5; index++)
        {
            await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-3", null, CancellationToken.None));
        }
        Assert.Equal(
            NhAssistantAdminErrorCodes.McpTooManyAuthorizations,
            Code(await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-3", null, CancellationToken.None))));

        // Disconnecting removes the connection and revokes both tokens at the authorization server.
        Assert.True((await AdministerAsync(host, administration => administration.DisconnectOrganizationAsync("planning", "admin-1", CancellationToken.None))).Success);
        Assert.Null(await FindOrganizationConnectionAsync(host));
        Assert.Contains("refresh_token", authorization.Revocations);
        Assert.Contains("access_token", authorization.Revocations);
        Assert.Null(authorization.ValidateAccessToken(accessToken, mcp.Url));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionDisconnected && evt.ObjectId == "planning");
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionRequired, (await TestAsync(host)).Code);

        AssertNoSecretLogged(host, WebClientSecret, state, completionCode, accessToken, QueryHelpers.ParseQuery(redirect.Query)["code"].ToString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Refresh_is_single_flight_rotates_the_refresh_token_and_an_invalid_grant_requires_a_new_authorization(
        AssistantTestProvider provider)
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider);
        // No pre-registered client: the host registers itself dynamically, once per issuer.
        Assert.True((await CreateAsync(host, Organization(mcp.Url) with
        {
            OAuth = new NhAssistantMcpOAuthInput(null, null, null, "mcp:read", "Planning service account", ConfirmAuthorization: true)
        })).Success);
        await ConnectOrganizationAsync(host);
        Assert.Single(authorization.DynamicClients);
        var connection = (await FindOrganizationConnectionAsync(host))!;
        var registration = await FindRegistrationAsync(host);
        Assert.Equal(Assert.Single(authorization.DynamicClients), registration.ClientId);
        Assert.NotNull(registration.ProtectedClientSecret);

        // Concurrent requests with an expiring token renew once; every request gets the new token.
        await ExpireAccessTokenAsync(host, connection.Id);
        authorization.Options.TokenResponseDelay = TimeSpan.FromMilliseconds(300);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => GetCredentialAsync(host)));
        authorization.Options.TokenResponseDelay = TimeSpan.Zero;
        Assert.All(concurrent, result => Assert.True(result.Success));
        Assert.Single(concurrent.Select(result => result.Data!.Headers["Authorization"]).Distinct());
        Assert.Equal(1, RefreshCount(authorization));
        var afterFirst = (await FindOrganizationConnectionAsync(host))!;
        Assert.NotEqual(connection.ProtectedRefreshToken, afterFirst.ProtectedRefreshToken);
        Assert.NotNull(afterFirst.LastRefreshedAt);

        // The rotated refresh token was stored: the next renewal works (reusing the old one would
        // have revoked the family).
        await ExpireAccessTokenAsync(host, connection.Id);
        Assert.True((await GetCredentialAsync(host)).Success);
        Assert.Equal(2, RefreshCount(authorization));
        Assert.True((await TestAsync(host)).Ok);

        // Another instance holds the lease: this one waits and uses the tokens it stores.
        await ExpireAccessTokenAsync(host, connection.Id);
        var other = authorization.IssueAccessToken("remote-user", mcp.Url, "mcp:read", registration.ClientId);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NhAssistantMcpOAuthStore>();
            var now = DateTimeOffset.UtcNow;
            Assert.True(await store.TryTakeRefreshLeaseAsync(connection.Id, "other-instance", now, now.AddSeconds(30), CancellationToken.None));
            var waiting = GetCredentialAsync(host);
            await Task.Delay(400);
            Assert.False(waiting.IsCompleted);
            var protector = host.Services.GetRequiredService<NhAssistantMcpTokenProtector>();
            Assert.True(await store.StoreRenewedTokensAsync(
                connection.Id,
                "other-instance",
                protector.ProtectConnection("planning", connection.Id, other),
                now.AddMinutes(5),
                null,
                null,
                null,
                now,
                CancellationToken.None));
            Assert.Equal("Bearer " + other, (await waiting).Data!.Headers["Authorization"]);
        }
        Assert.Equal(2, RefreshCount(authorization));

        // A failure of the authorization server keeps the connection; an invalid grant ends it.
        await ExpireAccessTokenAsync(host, connection.Id);
        authorization.Options.FailTokenRequestsWith = "temporarily_unavailable";
        Assert.Equal(NhAssistantAdminErrorCodes.McpTokenRequestFailed, Code(await GetCredentialAsync(host)));
        var kept = (await FindOrganizationConnectionAsync(host))!;
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, kept.Status);
        Assert.Equal(NhAssistantAdminErrorCodes.McpTokenRequestFailed, kept.LastFailureCode);
        Assert.Null(kept.RefreshLeaseOwner);
        authorization.Options.FailTokenRequestsWith = "invalid_grant";
        Assert.Equal(NhAssistantAdminErrorCodes.McpReauthorizationRequired, Code(await GetCredentialAsync(host)));
        var ended = (await FindOrganizationConnectionAsync(host))!;
        Assert.Equal(NhAssistantMcpConnectionStatuses.ReauthorizationRequired, ended.Status);
        Assert.Null(ended.ProtectedAccessToken);
        Assert.Null(ended.ProtectedRefreshToken);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionReauthorizationRequired
            && evt.ObjectId == "planning"
            && evt.ResultCode == NhAssistantAdminErrorCodes.McpReauthorizationRequired);
        Assert.Equal(NhAssistantAdminErrorCodes.McpReauthorizationRequired, (await TestAsync(host)).Code);

        // The opt-in health check reports the connection that waits for an administrator.
        var degraded = await CheckHealthAsync(host);
        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Equal(1, degraded.Data["organizationConnectionsNeedingAction"]);
        Assert.Equal(["planning"], (string[])degraded.Data["failingServers"]);

        // Connecting again restores it with the same registration.
        authorization.Options.FailTokenRequestsWith = null;
        await ConnectOrganizationAsync(host);
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, (await FindOrganizationConnectionAsync(host))!.Status);
        Assert.Single(authorization.DynamicClients);
        Assert.True((await TestAsync(host)).Ok);
        Assert.Equal(HealthStatus.Healthy, (await CheckHealthAsync(host)).Status);

        // A refresh token that no key can read any more (a lost key ring) also asks for a new authorization.
        connection = (await FindOrganizationConnectionAsync(host))!;
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.McpConnections
                .Where(row => row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ProtectedRefreshToken, "unreadable"));
        }
        await ExpireAccessTokenAsync(host, connection.Id);
        Assert.Equal(NhAssistantAdminErrorCodes.McpReauthorizationRequired, Code(await GetCredentialAsync(host)));
        AssertNoSecretLogged(host, other);
    }

    [Fact]
    public async Task A_server_that_names_another_authorization_server_is_suspended_until_an_administrator_confirms_it()
    {
        await using var first = await NhAiTestAuthorizationServer.StartAsync();
        await using var second = await NhAiTestAuthorizationServer.StartAsync();
        first.RegisterClient("assistant-cc", ClientSecret);
        second.RegisterClient("assistant-cc", ClientSecret);
        await using var mcp = await ProtectedMcpServer.StartAsync(first);
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        Assert.True((await CreateAsync(host, ClientCredentials(mcp.Url))).Success);
        Assert.True((await TestAsync(host)).Ok);

        // The resource now trusts another authorization server: tokens are not sent there on the
        // server's word; the connection waits for the administrator.
        mcp.Switch(second);
        var suspended = await TestAsync(host);
        Assert.False(suspended.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpIssuerChanged, suspended.Code);
        Assert.Equal(NhAssistantMcpConnectionStatuses.IssuerChanged, (await FindOrganizationConnectionAsync(host))!.Status);
        Assert.Empty(second.TokenRequests);
        Assert.Equal(NhAssistantAdminErrorCodes.McpIssuerChanged, (await TestAsync(host)).Code);

        // Confirming pins the new issuer and revokes what the old one issued.
        Assert.True((await UpdateAsync(host, ClientCredentials(mcp.Url))).Success);
        Assert.Equal(second.Issuer, (await FindServerAsync(host)).AuthorizationServerIssuer);
        Assert.Null(await FindOrganizationConnectionAsync(host));
        Assert.NotEmpty(first.Revocations);
        Assert.True((await TestAsync(host)).Ok);
        Assert.Single(second.TokenRequests);
    }

    [Fact]
    public async Task Discovery_refuses_metadata_of_another_issuer_or_resource_servers_without_pkce_and_blocked_addresses()
    {
        await using var withoutPkce = await NhAiTestAuthorizationServer.StartAsync(options => options.SupportsPkce = false);
        await using var mcp = await ProtectedMcpServer.StartAsync(withoutPkce);
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);

        Assert.Equal(NhAssistantAdminErrorCodes.McpPkceUnsupported, Code(await CreateAsync(host, Organization(mcp.Url))));
        Assert.Null(await host.Services.GetRequiredService<NhAssistantAdminStore>().FindMcpServerAsync("planning", CancellationToken.None));

        // Hand-made metadata: a resource that names another resource, an issuer whose metadata names
        // another issuer, and an authorization server on a blocked address.
        string? metadataIssuer = null;
        string? namedResource = null;
        string authorizationServer = string.Empty;
        var openIdOnly = false;
        await using var forged = await RawServer.StartAsync(async context =>
        {
            var origin = context.Request.Scheme + "://" + context.Request.Host;
            var metadataPath = openIdOnly ? "/.well-known/openid-configuration" : "/.well-known/oauth-authorization-server";
            switch (context.Request.Path.Value)
            {
                case "/.well-known/oauth-protected-resource/mcp":
                    await context.Response.WriteAsJsonAsync(new
                    {
                        resource = namedResource ?? origin.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/mcp",
                        authorization_servers = new[] { authorizationServer.Length > 0 ? authorizationServer : origin }
                    });
                    return;
                case var path when path == metadataPath:
                    await context.Response.WriteAsJsonAsync(new
                    {
                        issuer = metadataIssuer ?? origin,
                        authorization_endpoint = origin + "/authorize",
                        token_endpoint = origin + "/token",
                        code_challenge_methods_supported = new[] { "S256" }
                    });
                    return;
                default:
                    context.Response.StatusCode = 401;
                    return;
            }
        });
        var url = forged.Origin + "/mcp";
        // Without a challenge the well-known address of the path is read, then OAuth or OpenID metadata.
        Assert.True((await DiscoverAsync(host, url)).Success);
        openIdOnly = true;
        Assert.True((await DiscoverAsync(host, url)).Success);
        openIdOnly = false;
        metadataIssuer = "https://other-issuer.example";
        Assert.Equal(NhAssistantAdminErrorCodes.McpDiscoveryFailed, Code(await DiscoverAsync(host, url)));
        metadataIssuer = null;
        namedResource = "https://elsewhere.example/mcp";
        Assert.Equal(NhAssistantAdminErrorCodes.McpDiscoveryFailed, Code(await DiscoverAsync(host, url)));
        namedResource = null;
        authorizationServer = "http://169.254.169.254";
        Assert.NotNull(Code(await DiscoverAsync(host, url)));
        Assert.Equal(["url"], Fields(await DiscoverAsync(host, "not a url")));
    }

    [Fact]
    public async Task The_discovery_probe_is_an_initialize_post_without_a_credential_and_a_get_follows_only_a_405()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);

        // The endpoint answers as a stateless Streamable HTTP server, an HTTP+SSE endpoint, an endpoint
        // that takes neither method or a server without authorization; the metadata is always served.
        var behavior = "stateless";
        var probes = new System.Collections.Concurrent.ConcurrentQueue<(string Method, string? RpcMethod, string Session)>();
        var credentials = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var server = await RawServer.StartAsync(async context =>
        {
            var origin = context.Request.Scheme + "://" + context.Request.Host;
            var metadata = origin + "/.well-known/oauth-protected-resource/mcp";
            switch (context.Request.Path.Value)
            {
                case "/.well-known/oauth-protected-resource/mcp":
                    await context.Response.WriteAsJsonAsync(new
                    {
                        resource = origin + "/mcp",
                        authorization_servers = new[] { origin },
                        scopes_supported = new[] { "mcp:read" }
                    });
                    return;
                case "/.well-known/oauth-authorization-server":
                    await context.Response.WriteAsJsonAsync(new
                    {
                        issuer = origin,
                        authorization_endpoint = origin + "/authorize",
                        token_endpoint = origin + "/token",
                        code_challenge_methods_supported = new[] { "S256" }
                    });
                    return;
                case "/mcp":
                    break;
                default:
                    context.Response.StatusCode = 404;
                    return;
            }

            string? rpcMethod = null;
            if (HttpMethods.IsPost(context.Request.Method))
            {
                using var request = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
                rpcMethod = request.RootElement.GetProperty("method").GetString();
            }
            probes.Enqueue((context.Request.Method, rpcMethod, context.Request.Headers["Mcp-Session-Id"].ToString()));
            credentials.Enqueue(context.Request.Headers.Authorization.ToString() + context.Request.Headers.Cookie.ToString());

            var post = HttpMethods.IsPost(context.Request.Method);
            switch (behavior)
            {
                case "stateless" when post:
                case "sse" when !post:
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{metadata}\", scope=\"{behavior}:read\"";
                    return;
                case "open" when post:
                    context.Response.Headers["Mcp-Session-Id"] = "probe-session-1";
                    await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-11-25" } });
                    return;
                case "open":
                    context.Response.StatusCode = 204;
                    return;
                default:
                    context.Response.StatusCode = 405;
                    return;
            }
        });
        var url = server.Origin + "/mcp";

        // A stateless server answers GET with 405, but its challenge answers the initialize POST.
        var stateless = await DiscoverAsync(host, url);
        Assert.True(stateless.Success);
        Assert.Equal(["stateless:read"], stateless.Data!.ChallengedScopes);
        Assert.Equal([("POST", "initialize")], probes.Select(probe => (probe.Method, probe.RpcMethod)));

        // An HTTP+SSE endpoint answers the POST with 405 and challenges the GET that follows.
        probes.Clear();
        behavior = "sse";
        var sse = await DiscoverAsync(host, url);
        Assert.True(sse.Success);
        Assert.Equal(["sse:read"], sse.Data!.ChallengedScopes);
        Assert.Equal(["POST", "GET"], probes.Select(probe => probe.Method));

        // With 405 on both methods there is no challenge: the well-known metadata decides, as before.
        probes.Clear();
        behavior = "neither";
        var neither = await DiscoverAsync(host, url);
        Assert.True(neither.Success);
        Assert.Empty(neither.Data!.ChallengedScopes);
        Assert.Equal(["mcp:read"], neither.Data.ResourceScopes);
        Assert.Equal(["POST", "GET"], probes.Select(probe => probe.Method));

        // A server without authorization initializes a session: the probe ends it and reads the metadata.
        probes.Clear();
        behavior = "open";
        var open = await DiscoverAsync(host, url);
        Assert.True(open.Success);
        Assert.Empty(open.Data!.ChallengedScopes);
        Assert.Equal([("POST", ""), ("DELETE", "probe-session-1")], probes.Select(probe => (probe.Method, probe.Session)));

        // No probe request carried a credential.
        Assert.Equal(7, credentials.Count);
        Assert.All(credentials, credential => Assert.Equal(string.Empty, credential));
    }

    [Fact]
    public async Task Without_a_client_id_the_host_identifies_itself_by_its_metadata_document_before_registering()
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options => options.SupportsClientIdMetadataDocuments = true);
        var documentUrl = string.Empty;
        await using var document = await RawServer.StartAsync(async context =>
        {
            if (context.Request.Path.Value != "/client.json")
            {
                context.Response.StatusCode = 404;
                return;
            }
            // What the host serves at <prefix>/mcp/oauth/client-metadata.json.
            await context.Response.WriteAsJsonAsync(new
            {
                client_id = documentUrl,
                client_name = "NewHeap assistant",
                redirect_uris = new[] { RedirectUri },
                token_endpoint_auth_method = "none"
            });
        });
        documentUrl = document.Origin + "/client.json";
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(
            AssistantTestProvider.PostgreSql,
            mcp: options => options.OAuth.ClientMetadataDocumentUri = documentUrl);
        Assert.True((await CreateAsync(host, Organization(mcp.Url) with
        {
            OAuth = new NhAssistantMcpOAuthInput(null, null, null, "mcp:read", "Planning service account", ConfirmAuthorization: true)
        })).Success);

        await ConnectOrganizationAsync(host);

        Assert.Empty(authorization.DynamicClients);
        var exchange = Assert.Single(authorization.TokenRequests);
        Assert.Equal(documentUrl, exchange.ClientId);
        Assert.Equal("none", exchange.ClientAuthentication);
        Assert.True((await TestAsync(host)).Ok);
        Assert.All(mcp.Resource.Requests, request => Assert.Equal(documentUrl, request.ClientId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Maintenance_removes_expired_authorizations(AssistantTestProvider provider)
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider);
        Assert.True((await CreateAsync(host, Organization(mcp.Url))).Success);
        var expired = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        var current = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        await UpdateFlowAsync(host, expired.FlowId, row => row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));

        await new NhAssistantMcpMaintenance(
                host.Services.GetRequiredService<IServiceScopeFactory>(),
                host.Services.GetRequiredService<NhAssistantRegistrationState>(),
                NullLogger<NhAssistantMcpMaintenance>.Instance)
            .RunOnceAsync(CancellationToken.None);

        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal([current.FlowId], await context.McpAuthorizationFlows.Select(flow => flow.Id).ToListAsync());
    }

    [Fact]
    public async Task The_endpoints_hand_the_completion_code_only_to_the_allowed_origin_and_complete_with_the_starters_session()
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization, resource => resource.ChallengeScope = "mcp:read");
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            assistantBuilder: assistant => assistant.ConfigureMcp(options =>
            {
                options.OAuth.RedirectUri = RedirectUri;
                options.OAuth.ClientMetadataDocumentUri = "https://assistant.example/api/assistant/mcp/oauth/client-metadata.json";
            }));
        using var admin = app.CreateClient(admin: true);
        using var member = app.CreateClient();
        using var otherAdmin = app.CreateClient("user-2", admin: true);
        using var anonymous = app.CreateClient(user: null, access: false);

        using var discovered = await admin.PostAsync("/api/assistant/admin/mcp-servers/discover", Json(new { url = mcp.Url }));
        Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
        var discovery = await ReadJsonAsync(discovered);
        AssertNames(discovery, "resource", "issuer", "authorizationServers", "challengedScopes", "scopesSupported", "grantTypesSupported",
            "pkceSupported", "clientIdMetadataDocumentSupported", "dynamicRegistrationSupported", "tokenEndpointAuthMethods");
        Assert.Equal(authorization.Issuer, discovery.GetProperty("issuer").GetString());
        Assert.Equal("mcp:read", discovery.GetProperty("challengedScopes")[0].GetString());
        using var missingUrl = await admin.PostAsync("/api/assistant/admin/mcp-servers/discover", Json(new { }));
        Assert.Equal(HttpStatusCode.BadRequest, missingUrl.StatusCode);

        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = mcp.Url,
            authMode = "oauth",
            isEnabled = true,
            connectionScope = "organization",
            oauth = new
            {
                clientId = "assistant-web",
                clientSecret = WebClientSecret,
                scopes = "mcp:read",
                organizationAccountLabel = "Planning service account",
                confirmAuthorization = true
            }
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(WebClientSecret, createdText, StringComparison.Ordinal);
        var server = await ReadJsonAsync(created);
        AssertNames(server.GetProperty("oauth"), "clientId", "hasClientSecret", "clientSecretReference", "scopes", "organizationAccountLabel");
        Assert.True(server.GetProperty("oauth").GetProperty("hasClientSecret").GetBoolean());
        AssertNames(server.GetProperty("authorization"), "issuer", "resource", "confirmedAt", "confirmedBy");
        Assert.Equal(mcp.Url, server.GetProperty("authorization").GetProperty("resource").GetString());
        AssertNames(server.GetProperty("organizationConnection"), "status", "connectedAt", "connectedBy", "grantedScopes", "requiredScopes",
            "accessTokenExpiresAt", "lastFailureCode");
        Assert.Equal("not-connected", server.GetProperty("organizationConnection").GetProperty("status").GetString());

        // Popup mode: the callback page posts to the opener at the allowed origin only.
        using var started = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/connection/authorize", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var start = await ReadJsonAsync(started);
        AssertNames(start, "flowId", "authorizationUrl", "expiresAt", "callbackOrigin");
        Assert.Equal("https://assistant.example", start.GetProperty("callbackOrigin").GetString());
        var flowId = start.GetProperty("flowId").GetString()!;
        var redirect = await AuthorizeAsync(start.GetProperty("authorizationUrl").GetString()!);
        using var callback = await anonymous.GetAsync("/api/assistant/mcp/oauth/callback" + redirect.Query);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Equal("text/html", callback.Content.Headers.ContentType!.MediaType);
        var csp = callback.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'nonce-", csp, StringComparison.Ordinal);
        Assert.Contains("no-store", callback.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal("no-referrer", callback.Headers.GetValues("Referrer-Policy").Single());
        var page = await callback.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"nh-assistant-mcp-authorization\"", page, StringComparison.Ordinal);
        Assert.Contains("\"flowId\":\"" + flowId + "\"", page, StringComparison.Ordinal);
        Assert.Contains("o=[\"https://assistant.example\"]", page, StringComparison.Ordinal);
        var completionCode = System.Text.RegularExpressions.Regex.Match(page, "\"completionCode\":\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(completionCode);
        Assert.DoesNotContain(QueryHelpers.ParseQuery(redirect.Query)["code"].ToString(), page, StringComparison.Ordinal);

        // Completing needs the starter's own session, and the admin policy for an organization connection.
        using var withoutAdmin = await member.PostAsync("/api/assistant/mcp-connections/complete", Json(new { flowId, completionCode }));
        using var byAnother = await otherAdmin.PostAsync("/api/assistant/mcp-connections/complete", Json(new { flowId, completionCode }));
        using var missingCode = await admin.PostAsync("/api/assistant/mcp-connections/complete", Json(new { flowId }));
        using var notSignedIn = await anonymous.PostAsync("/api/assistant/mcp-connections/complete", Json(new { flowId, completionCode }));
        using var completed = await admin.PostAsync("/api/assistant/mcp-connections/complete", Json(new { flowId, completionCode }));
        using var again = await admin.PostAsync("/api/assistant/mcp-connections/complete", Json(new { flowId, completionCode }));
        Assert.Equal(HttpStatusCode.Forbidden, withoutAdmin.StatusCode);
        Assert.Equal("assistant-forbidden", (await ReadJsonAsync(withoutAdmin)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, byAnother.StatusCode);
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationInvalid, (await ReadJsonAsync(byAnother)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missingCode.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, notSignedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var completion = await ReadJsonAsync(completed);
        AssertNames(completion, "serverId", "ownerKind", "status", "grantedScopes");
        Assert.Equal("organization", completion.GetProperty("ownerKind").GetString());
        Assert.Equal("active", completion.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationExpired, (await ReadJsonAsync(again)).GetProperty("code").GetString());

        var connection = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/connection"));
        Assert.Equal("active", connection.GetProperty("status").GetString());
        Assert.Equal("user-1", connection.GetProperty("connectedBy").GetString());
        Assert.Equal("mcp:read", connection.GetProperty("grantedScopes").GetString());
        var listText = await (await admin.GetAsync("/api/assistant/admin/mcp-servers")).Content.ReadAsStringAsync();
        Assert.Contains("\"organizationConnection\":{\"status\":\"active\"", listText, StringComparison.Ordinal);
        using var tested = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/test", null);
        Assert.True((await ReadJsonAsync(tested)).GetProperty("ok").GetBoolean());

        // Redirect mode returns to an allowed origin with the flow in the fragment; other origins are refused.
        using var foreign = await admin.PostAsync(
            "/api/assistant/admin/mcp-servers/planning/connection/authorize",
            Json(new { returnUrl = "https://attacker.example/settings" }));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("invalid", (await ReadJsonAsync(foreign)).GetProperty("errors").GetProperty("returnUrl")[0].GetString());
        using var redirectStart = await admin.PostAsync(
            "/api/assistant/admin/mcp-servers/planning/connection/authorize",
            Json(new { returnUrl = "https://assistant.example/settings/assistant" }));
        var redirectFlow = (await ReadJsonAsync(redirectStart)).GetProperty("flowId").GetString()!;
        var secondRedirect = await AuthorizeAsync((await ReadJsonAsync(redirectStart)).GetProperty("authorizationUrl").GetString()!);
        using var returned = await anonymous.GetAsync("/api/assistant/mcp/oauth/callback" + secondRedirect.Query);
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        var location = returned.Headers.Location!.ToString();
        Assert.StartsWith("https://assistant.example/settings/assistant#nh-mcp-flow=" + redirectFlow + "&nh-mcp-code=", location, StringComparison.Ordinal);
        using var replayed = await anonymous.GetAsync("/api/assistant/mcp/oauth/callback" + secondRedirect.Query);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replayedPage = await replayed.Content.ReadAsStringAsync();
        Assert.Contains("\"completionCode\":null", replayedPage, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"" + NhAssistantAdminErrorCodes.McpAuthorizationExpired + "\"", replayedPage, StringComparison.Ordinal);
        // The failure carries its localization key, as every assistant error does, and the page says why.
        Assert.Contains("\"messageKey\":\"nh-assistant.errors." + NhAssistantAdminErrorCodes.McpAuthorizationExpired + "\"", replayedPage, StringComparison.Ordinal);
        Assert.Contains("expired or was already used", replayedPage, StringComparison.Ordinal);

        // The host's client metadata document.
        var metadata = await ReadJsonAsync(await anonymous.GetAsync("/api/assistant/mcp/oauth/client-metadata.json"));
        Assert.Equal("https://assistant.example/api/assistant/mcp/oauth/client-metadata.json", metadata.GetProperty("client_id").GetString());
        Assert.Equal(RedirectUri, metadata.GetProperty("redirect_uris")[0].GetString());
        Assert.Equal("none", metadata.GetProperty("token_endpoint_auth_method").GetString());

        // An organization server has no personal connections to count or revoke; disconnecting the
        // organization revokes its tokens.
        foreach (var personal in new[]
        {
            await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/connections/summary"),
            await admin.DeleteAsync("/api/assistant/admin/mcp-servers/planning/connections")
        })
        {
            using (personal)
            {
                Assert.Equal(HttpStatusCode.Conflict, personal.StatusCode);
                Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionNotSupported, (await ReadJsonAsync(personal)).GetProperty("code").GetString());
            }
        }
        using var disconnected = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/planning/connection");
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Contains("refresh_token", authorization.Revocations);
        var after = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/mcp-servers/planning/connection"));
        Assert.Equal("not-connected", after.GetProperty("status").GetString());
        using var unknown = await admin.GetAsync("/api/assistant/admin/mcp-servers/elsewhere/connection");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var notAdmin = await member.GetAsync("/api/assistant/admin/mcp-servers/planning/connection");
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_dynamic_registration_that_expired_or_that_the_authorization_server_forgot_is_registered_again(
        AssistantTestProvider provider)
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider);
        Assert.True((await CreateAsync(host, Organization(mcp.Url) with
        {
            OAuth = new NhAssistantMcpOAuthInput(null, null, null, "mcp:read", "Planning service account", ConfirmAuthorization: true)
        })).Success);
        await ConnectOrganizationAsync(host);
        var first = Assert.Single(authorization.DynamicClients);

        // A client secret that expired is of no use: the next authorization registers again.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
            await context.McpClientRegistrations
                .Where(row => row.ServerId == "planning")
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ClientSecretExpiresAt, expired));
        }
        var start = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        Assert.Equal(2, authorization.DynamicClients.Count);
        var second = (await FindRegistrationAsync(host)).ClientId;
        Assert.NotEqual(first, second);
        Assert.Equal(second, QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["client_id"].ToString());

        // A registration the authorization server no longer knows ends the connection whose refresh
        // token belonged to it, and the next authorization registers again.
        var connection = (await FindOrganizationConnectionAsync(host))!;
        await ExpireAccessTokenAsync(host, connection.Id);
        authorization.Options.FailTokenRequestsWith = "invalid_client";
        Assert.Equal(NhAssistantAdminErrorCodes.McpReauthorizationRequired, Code(await GetCredentialAsync(host)));
        authorization.Options.FailTokenRequestsWith = null;
        var ended = (await FindOrganizationConnectionAsync(host))!;
        Assert.Equal(NhAssistantMcpConnectionStatuses.ReauthorizationRequired, ended.Status);
        Assert.Equal(NhAssistantAdminErrorCodes.McpClientRegistrationRequired, ended.LastFailureCode);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.False(await context.McpClientRegistrations.AnyAsync(row => row.ServerId == "planning"));
        }
        await ConnectOrganizationAsync(host);
        Assert.Equal(3, authorization.DynamicClients.Count);
        Assert.True((await TestAsync(host)).Ok);
    }

    [Fact]
    public async Task A_completion_checks_the_server_before_it_uses_up_the_flow_and_callbacks_answer_one_code_per_cause()
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        Assert.True((await CreateAsync(host, Organization(mcp.Url))).Success);
        var start = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        var outcome = await CallbackAsync(host, await AuthorizeAsync(start.AuthorizationUrl));
        Assert.Null(outcome.FailureCode);

        // The server names another authorization server before the completion: the flow ends failed
        // with that code and its token response is gone; it was not completed.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.McpServers
                .Where(row => row.Id == "planning")
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AuthorizationServerIssuer, "https://other-issuer.example"));
        }
        Assert.Equal(NhAssistantAdminErrorCodes.McpIssuerChanged, Code(await CompleteAsync(host, start.FlowId, outcome.CompletionCode!, "admin-1")));
        var flow = await FindFlowAsync(host, start.FlowId);
        Assert.Equal((NhAssistantMcpOAuthStore.FlowFailed, NhAssistantAdminErrorCodes.McpIssuerChanged), (flow.Status, flow.FailureCode));
        Assert.Null(flow.ProtectedTokenResponse);
        Assert.Null(await FindOrganizationConnectionAsync(host));

        // A callback without a state does not belong to any flow.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var flows = scope.ServiceProvider.GetRequiredService<NhAssistantMcpAuthorizationFlows>();
            Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationInvalid, (await flows.CallbackAsync(null, "code", null, null, CancellationToken.None)).FailureCode);
            Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationExpired, (await flows.CallbackAsync("unknown-state", "code", null, null, CancellationToken.None)).FailureCode);
        }
    }

    [Fact]
    public async Task A_stored_client_secret_that_no_longer_decrypts_is_the_hosts_problem_and_a_cancelled_renewal_releases_its_lease()
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-cc", ClientSecret);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        Assert.True((await CreateAsync(host, ClientCredentials(mcp.Url))).Success);
        Assert.True((await TestAsync(host)).Ok);
        var connection = (await FindOrganizationConnectionAsync(host))!;

        // A renewal that is cancelled while it waits for the token endpoint leaves no lease behind.
        await ExpireAccessTokenAsync(host, connection.Id);
        authorization.Options.TokenResponseDelay = TimeSpan.FromSeconds(3);
        var server = await FindServerAsync(host);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            await using var scope = host.Services.CreateAsyncScope();
            var connections = scope.ServiceProvider.GetRequiredService<NhAssistantMcpOAuthConnections>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                connections.GetCredentialAsync(server, NhAssistantMcpConnectionOwner.Organization, cancellation.Token));
        }
        authorization.Options.TokenResponseDelay = TimeSpan.Zero;
        Assert.Null((await FindOrganizationConnectionAsync(host))!.RefreshLeaseOwner);
        Assert.True((await GetCredentialAsync(host)).Success);

        // The client secret no longer decrypts, as after the data protection keys changed.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.McpServers
                .Where(row => row.Id == "planning")
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ProtectedOAuthClientSecret, "protected-with-a-lost-key"));
        }
        await ExpireAccessTokenAsync(host, connection.Id);
        var unreadable = await TestAsync(host);
        Assert.False(unreadable.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpSecretUnreadable, unreadable.Code);
        Assert.Contains(host.Logs.Messages, message => message.Contains("can no longer be decrypted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Servers_without_oauth_have_no_connections_to_authorize_read_disconnect_or_revoke()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql);
        Assert.True((await CreateAsync(host, new NhAssistantMcpServerInput(
            "planning",
            "Planning",
            "https://planning.example/mcp",
            NhAssistantMcpAuthModes.Bearer,
            null,
            "bearer-secret",
            null,
            true))).Success);

        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionNotSupported, Code(await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))));
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionNotSupported, Code(await AdministerAsync(host, administration => administration.GetConnectionAsync("planning", "admin-1", CancellationToken.None))));
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionNotSupported, Code(await AdministerAsync(host, administration => administration.DisconnectOrganizationAsync("planning", "admin-1", CancellationToken.None))));
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionNotSupported, Code(await AdministerAsync(host, administration => administration.SummarizePersonalConnectionsAsync("planning", CancellationToken.None))));
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionNotSupported, Code(await AdministerAsync(host, administration => administration.RevokePersonalConnectionsAsync("planning", "admin-1", CancellationToken.None))));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpConnectionsRevoked);
    }

    [Fact]
    public async Task Oauth_urls_never_reach_private_networks_and_mcp_endpoints_only_when_they_are_blocked()
    {
        string[] privateAddresses = ["10.1.2.3", "172.16.5.4", "172.31.255.1", "192.168.1.1", "100.64.0.1", "fd00::1", "::ffff:10.0.0.1"];
        string[] publicAddresses = ["8.8.8.8", "172.32.0.1", "100.128.0.1", "2001:db8::1"];
        Assert.All(privateAddresses, address => Assert.True(NhAssistantMcpHostGuard.IsPrivate(IPAddress.Parse(address))));
        Assert.All(publicAddresses, address => Assert.False(NhAssistantMcpHostGuard.IsPrivate(IPAddress.Parse(address))));

        var open = Guard(blockPrivateNetworks: false);
        var blocked = Guard(blockPrivateNetworks: true);
        var allowed = Guard(blockPrivateNetworks: true, "10.1.2.3");
        var cancellation = CancellationToken.None;

        // An administrator may type an intranet MCP server; a remote document may not point there.
        Assert.Single(await open.ResolveAllowedAsync("10.1.2.3", cancellation));
        Assert.Empty(await open.ResolveAllowedForOAuthAsync("10.1.2.3", cancellation));
        Assert.Empty(await blocked.ResolveAllowedAsync("10.1.2.3", cancellation));
        Assert.Single(await allowed.ResolveAllowedAsync("10.1.2.3", cancellation));
        Assert.Single(await allowed.ResolveAllowedForOAuthAsync("10.1.2.3", cancellation));
        Assert.Single(await blocked.ResolveAllowedForOAuthAsync("8.8.8.8", cancellation));
        // Link-local and metadata addresses stay blocked for both.
        Assert.Empty(await allowed.ResolveAllowedAsync("169.254.169.254", cancellation));

        static NhAssistantMcpHostGuard Guard(bool blockPrivateNetworks, params string[] allowedHosts)
        {
            var services = new ServiceCollection();
            services.AddOptions<NhAssistantOptions>().Configure(options =>
            {
                options.Mcp.BlockPrivateNetworks = blockPrivateNetworks;
                foreach (var host in allowedHosts)
                {
                    options.Mcp.AllowedHosts.Add(host);
                }
            });
            return new NhAssistantMcpHostGuard(
                services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<NhAssistantOptions>>(),
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" });
        }
    }

    [Fact]
    public void The_oauth_options_are_checked_when_the_host_starts()
    {
        new NhAssistantMcpOAuthOptions().Validate(allowLoopbackHttp: false);
        new NhAssistantMcpOAuthOptions
        {
            RedirectUri = "https://app.example/api/assistant/mcp/oauth/callback",
            ClientMetadataDocumentUri = "https://app.example/api/assistant/mcp/oauth/client-metadata.json",
            ReturnOrigins = ["https://app.example", "https://portal.example/"]
        }.Validate(allowLoopbackHttp: false);
        // Development hosts may use http on loopback and .localhost names.
        new NhAssistantMcpOAuthOptions
        {
            RedirectUri = "https://api.dev.localhost:5281/api/assistant/mcp/oauth/callback",
            ReturnOrigins = ["http://management.dev.localhost:4210", "http://localhost:4200"]
        }.Validate(allowLoopbackHttp: true);

        NhAssistantMcpOAuthOptions[] invalid =
        [
            new() { RedirectUri = "http://app.example/callback" },
            new() { RedirectUri = "http://localhost/callback" },
            new() { RedirectUri = "https://app.example/callback#fragment" },
            new() { RedirectUri = "https://app.example/callback", ReturnOrigins = ["https://app.example/settings"] },
            new() { ClientMetadataDocumentUri = "https://app.example/client.json" },
            new() { FlowLifetime = TimeSpan.FromHours(2) },
            new() { RefreshLeaseDuration = TimeSpan.FromSeconds(1) },
            new() { MaxPendingFlowsPerActor = 0 },
            new() { ClientName = " " }
        ];
        foreach (var options in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => options.Validate(allowLoopbackHttp: false));
        }
    }

    // ----- Helpers -----

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        IConfiguration? configuration = null,
        Action<NhAssistantMcpOptions>? mcp = null,
        NhAiScriptedChatClient? model = null)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model ?? new NhAiScriptedChatClient(),
            configure: services =>
            {
                if (configuration is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(configuration));
                }
            },
            assistantBuilder: assistant => assistant.ConfigureMcp(options =>
            {
                options.RequireHttps = true;
                options.OAuth.RedirectUri = RedirectUri;
                mcp?.Invoke(options);
            }));
    }

    private static NhAssistantMcpServerInput ClientCredentials(string url, bool confirm = true)
    {
        return new NhAssistantMcpServerInput("planning", "Planning", url, NhAssistantMcpAuthModes.OAuthClientCredentials, null, null, null, true)
        {
            OAuth = new NhAssistantMcpOAuthInput("assistant-cc", ClientSecret, null, null, null, confirm)
        };
    }

    private static NhAssistantMcpServerInput Organization(string url)
    {
        return new NhAssistantMcpServerInput("planning", "Planning", url, NhAssistantMcpAuthModes.OAuth, null, null, null, true)
        {
            ConnectionScope = NhAssistantMcpConnectionScopes.Organization,
            OAuth = new NhAssistantMcpOAuthInput("assistant-web", WebClientSecret, null, "mcp:read", "Planning service account", ConfirmAuthorization: true)
        };
    }

    private static Task<TaskResult<AssistantMcpServer>> CreateAsync(AssistantTestHost host, NhAssistantMcpServerInput input)
    {
        return AdministerAsync(host, administration => administration.CreateAsync(input, "admin-1", CancellationToken.None));
    }

    private static Task<TaskResult<AssistantMcpServer>> UpdateAsync(AssistantTestHost host, NhAssistantMcpServerInput input)
    {
        return AdministerAsync(host, administration => administration.UpdateAsync(input, "admin-1", CancellationToken.None));
    }

    private static Task<TaskResult<NhAssistantMcpDiscoveryResult>> DiscoverAsync(AssistantTestHost host, string url)
    {
        return AdministerAsync(host, administration => administration.DiscoverAsync(url, CancellationToken.None));
    }

    private static async Task<NhAssistantMcpTestResult> TestAsync(AssistantTestHost host)
    {
        return (await AdministerAsync(host, administration => administration.TestAsync("planning", CancellationToken.None))).Data!;
    }

    private static async Task<T> AdministerAsync<T>(AssistantTestHost host, Func<NhAssistantMcpAdministration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>());
    }

    /// <summary>Runs the organization authorization the way an administrator's browser does.</summary>
    private static async Task ConnectOrganizationAsync(AssistantTestHost host)
    {
        var start = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        var outcome = await CallbackAsync(host, await AuthorizeAsync(start.AuthorizationUrl));
        Assert.Null(outcome.FailureCode);
        Assert.True((await CompleteAsync(host, start.FlowId, outcome.CompletionCode!, "admin-1")).Success);
    }

    private static async Task<NhAssistantMcpCallbackOutcome> StartAndCallBackAsync(
        AssistantTestHost host,
        NhAiTestAuthorizationServer authorization,
        bool removeIssuer = false)
    {
        var start = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        var redirect = await AuthorizeAsync(start.AuthorizationUrl);
        if (removeIssuer)
        {
            var query = QueryHelpers.ParseQuery(redirect.Query);
            redirect = new Uri(QueryHelpers.AddQueryString(
                redirect.GetLeftPart(UriPartial.Path),
                query.Where(item => item.Key != "iss").ToDictionary(item => item.Key, item => (string?)item.Value.ToString())));
        }
        return await CallbackAsync(host, redirect);
    }

    /// <summary>Opens the authorization URL and returns the redirect the authorization server answers.</summary>
    private static async Task<Uri> AuthorizeAsync(string authorizationUrl)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(authorizationUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(RedirectUri, location.ToString(), StringComparison.Ordinal);
        return location;
    }

    private static async Task<NhAssistantMcpCallbackOutcome> CallbackAsync(AssistantTestHost host, Uri redirect)
    {
        var query = QueryHelpers.ParseQuery(redirect.Query);
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpAuthorizationFlows>().CallbackAsync(
            query.TryGetValue("state", out var state) ? state.ToString() : null,
            query.TryGetValue("code", out var code) ? code.ToString() : null,
            query.TryGetValue("iss", out var issuer) ? issuer.ToString() : null,
            query.TryGetValue("error", out var error) ? error.ToString() : null,
            CancellationToken.None);
    }

    private static async Task<TaskResult<NhAssistantMcpCompletion>> CompleteAsync(
        AssistantTestHost host,
        Guid flowId,
        string completionCode,
        string actorId,
        bool administrator = true)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpAuthorizationFlows>().CompleteAsync(
            flowId,
            completionCode,
            actorId,
            () => Task.FromResult(administrator),
            impersonating: false,
            CancellationToken.None);
    }

    private static async Task<TaskResult<NhAssistantMcpCredential>> GetCredentialAsync(AssistantTestHost host)
    {
        var server = await FindServerAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpOAuthConnections>()
            .GetCredentialAsync(server, NhAssistantMcpConnectionOwner.Organization, CancellationToken.None);
    }

    private static async Task<string> AccessTokenAsync(AssistantTestHost host)
    {
        var credential = await GetCredentialAsync(host);
        Assert.True(credential.Success);
        return credential.Data!.Headers["Authorization"]["Bearer ".Length..];
    }

    /// <summary>Makes the stored access token expire, as if its lifetime passed.</summary>
    private static async Task ExpireAccessTokenAsync(AssistantTestHost host, Guid connectionId)
    {
        host.Services.GetRequiredService<NhAssistantMcpTokenCache>().Remove(connectionId);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
        await context.McpConnections
            .Where(connection => connection.Id == connectionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(connection => connection.AccessTokenExpiresAt, expired));
    }

    /// <summary>Runs the health check as the host registers it with AddNewHeapAssistantMcpHealthCheck.</summary>
    private static async Task<HealthCheckResult> CheckHealthAsync(AssistantTestHost host)
    {
        await using var registrations = new ServiceCollection()
            .AddHealthChecks()
            .AddNewHeapAssistantMcpHealthCheck(tags: ["ready"])
            .Services
            .BuildServiceProvider();
        var registration = registrations.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value
            .Registrations
            .Single(item => item.Name == "nh-assistant-mcp");
        Assert.Contains("ready", registration.Tags);
        return await registration.Factory(host.Services).CheckHealthAsync(new HealthCheckContext { Registration = registration });
    }

    private static int RefreshCount(NhAiTestAuthorizationServer authorization)
    {
        return authorization.TokenRequests.Count(request => request.GrantType == "refresh_token");
    }

    private static async Task<AssistantMcpServer> FindServerAsync(AssistantTestHost host)
    {
        return (await host.Services.GetRequiredService<NhAssistantAdminStore>().FindMcpServerAsync("planning", CancellationToken.None))!;
    }

    private static async Task<AssistantMcpConnection?> FindOrganizationConnectionAsync(AssistantTestHost host)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.McpConnections.AsNoTracking().SingleOrDefaultAsync(connection =>
            connection.ServerId == "planning"
            && connection.OwnerKind == NhAssistantMcpConnectionOwners.Organization
            && connection.Status != null);
    }

    private static async Task<AssistantMcpAuthorizationFlow> FindFlowAsync(AssistantTestHost host, Guid flowId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.McpAuthorizationFlows.AsNoTracking().SingleAsync(flow => flow.Id == flowId);
    }

    private static async Task<AssistantMcpClientRegistration> FindRegistrationAsync(AssistantTestHost host)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.McpClientRegistrations.AsNoTracking().SingleAsync(registration => registration.ServerId == "planning");
    }

    private static async Task UpdateFlowAsync(AssistantTestHost host, Guid flowId, Action<AssistantMcpAuthorizationFlow> apply)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var flow = await context.McpAuthorizationFlows.SingleAsync(item => item.Id == flowId);
        apply(flow);
        await context.SaveChangesAsync();
    }

    private static StringContent Json(object value)
    {
        return new StringContent(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
    }

    private static async Task<System.Text.Json.JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertNames(System.Text.Json.JsonElement element, params string[] expected)
    {
        Assert.Equal(expected.Order(StringComparer.Ordinal), element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
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

    /// <summary>
    /// An MCP server over HTTP behind an <see cref="NhAiTestProtectedResource"/> of an authorization
    /// server; <see cref="Switch"/> makes it trust another one.
    /// </summary>
    private sealed class ProtectedMcpServer : IAsyncDisposable
    {
        private readonly HttpMcpTestServer _server;

        private ProtectedMcpServer(HttpMcpTestServer server)
        {
            _server = server;
        }

        public string Url => _server.StreamableUrl;

        public NhAiTestProtectedResource Resource { get; private set; } = null!;

        public static async Task<ProtectedMcpServer> StartAsync(
            NhAiTestAuthorizationServer authorization,
            Action<NhAiTestProtectedResource>? configure = null)
        {
            var tools = new InMemoryMcpServers();
            ProtectedMcpServer? created = null;
            var server = await HttpMcpTestServer.StartAsync(
                () => tools.PlanningTools(),
                guard: (context, next) => created!.Resource.InvokeAsync(context, next));
            created = new ProtectedMcpServer(server);
            created.Resource = new NhAiTestProtectedResource(authorization, server.StreamableUrl, "/planning");
            configure?.Invoke(created.Resource);
            return created;
        }

        public void Switch(NhAiTestAuthorizationServer authorization)
        {
            Resource = new NhAiTestProtectedResource(authorization, Url, "/planning");
        }

        public ValueTask DisposeAsync()
        {
            return _server.DisposeAsync();
        }
    }

    /// <summary>A loopback server that answers every request with a handler.</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private RawServer(WebApplication app)
        {
            _app = app;
            Origin = LocalhostTestServer.Origin(app);
        }

        public string Origin { get; }

        public static async Task<RawServer> StartAsync(Func<HttpContext, Task> handle)
        {
            var app = await LocalhostTestServer.StartPlainAsync(server => server.Run(context => handle(context)));
            return new RawServer(app);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
