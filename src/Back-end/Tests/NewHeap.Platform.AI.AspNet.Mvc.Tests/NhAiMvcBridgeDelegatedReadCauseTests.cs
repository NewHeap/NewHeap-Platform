using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// A gateway read of a headless run that gets no usable delegated read credential names its cause:
/// the read fails with the cause as its code, which the run, the administration and the builder show,
/// and the host logs one content-free warning per cause that names the fix, however often it occurs.
/// </summary>
public sealed class NhAiMvcBridgeDelegatedReadCauseTests
{
    private const string Query = "test-gateway_query_v1";
    private const string Get = "test-gateway_get_v1";
    private const string Search = "test-gateway_search-resources_v1";
    private const string DiagnosticsCategory = "NewHeap.Platform.AI.AspNet.NhAiDelegatedReadService";

    [Fact]
    public async Task A_host_that_authenticates_before_routing_names_the_unknown_target_once()
    {
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory(authenticateBeforeRouting: true);
        await using var scope = factory.Services.CreateAsyncScope();

        string?[] codes;
        using (NhAiAgentInvocationScopes.Begin(NhAiMvcBridgeDelegatedReadTests.AgentScope("reader")))
        {
            // The person is known and the credential valid, so the read reaches the API; the API
            // refuses it because its authentication ran before routing knew the action.
            codes =
            [
                await CodeAsync(scope.ServiceProvider, Query, new { resource = "order" }),
                await CodeAsync(scope.ServiceProvider, Query, new { resource = "order", itemsPerPage = 5 }),
                await CodeAsync(scope.ServiceProvider, Get, new { resource = "order", id = 7 })
            ];
        }

        Assert.All(codes, code => Assert.Equal(NhAiDelegatedReadFailureCodes.TargetUnknown, code));
        Assert.Contains(factory.Requests, request => request.StartsWith("GET /orders", StringComparison.Ordinal));
        var warning = Assert.Single(Warnings(factory, 6));
        Assert.Contains(NhAiDelegatedReadFailureCodes.TargetUnknown, warning.Message);
        Assert.Contains("UseRouting before UseAuthentication", warning.Message);
        Assert.Contains("AuthenticateForTargetAsync", warning.Message);
        Assert.Empty(Warnings(factory, 9));
        AssertContentFree(factory);
    }

    [Fact]
    public async Task A_routed_host_reads_and_warns_about_nothing()
    {
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();

        using (NhAiAgentInvocationScopes.Begin(NhAiMvcBridgeDelegatedReadTests.AgentScope("reader")))
        {
            var query = await NhAiMvcBridgeDelegatedReadTests.InvokeAsync(scope.ServiceProvider, Query, new { resource = "order" });
            Assert.True(query.Success, query.Errors);
        }

        Assert.DoesNotContain(factory.Logs.Records, record => record.Category == DiagnosticsCategory && record.EventId is >= 6 and <= 9);
    }

    [Fact]
    public async Task Gateway_reads_without_delegated_reads_name_the_missing_issuer_once()
    {
        // The bridge's gateway, but AddNewHeapPlatformAIDelegatedReads was never called.
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory(delegatedReads: _ => { });
        await using var scope = factory.Services.CreateAsyncScope();

        using (NhAiAgentInvocationScopes.Begin(NhAiMvcBridgeDelegatedReadTests.AgentScope("reader")))
        {
            Assert.Equal(NhAiDelegatedReadFailureCodes.NoIssuer, await CodeAsync(scope.ServiceProvider, Query, new { resource = "order" }));
            Assert.Equal(NhAiDelegatedReadFailureCodes.NoIssuer, await CodeAsync(scope.ServiceProvider, Search, new { query = "" }));
        }

        var warning = Assert.Single(Warnings(factory, 7));
        Assert.Contains("order-planner", warning.Message);
        Assert.Contains("AddNewHeapPlatformAIDelegatedReads", warning.Message);
        Assert.DoesNotContain(factory.Requests, request => request.StartsWith("GET /orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_built_in_issuer_on_a_host_without_the_NewHeap_authentication_service_names_the_missing_issuer_once()
    {
        // Delegated reads with the built-in issuer, on a host that signs in elsewhere.
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory(
            delegatedReads: services => services.AddNewHeapPlatformAIDelegatedReads());
        await using var scope = factory.Services.CreateAsyncScope();

        using (NhAiAgentInvocationScopes.Begin(NhAiMvcBridgeDelegatedReadTests.AgentScope("reader")))
        {
            Assert.Equal(NhAiDelegatedReadFailureCodes.NoIssuer, await CodeAsync(scope.ServiceProvider, Query, new { resource = "order" }));
            Assert.Equal(NhAiDelegatedReadFailureCodes.NoIssuer, await CodeAsync(scope.ServiceProvider, Get, new { resource = "order", id = 7 }));
        }

        var warning = Assert.Single(Warnings(factory, 7));
        Assert.Contains("UseIssuer", warning.Message);
        Assert.DoesNotContain(factory.Requests, request => request.StartsWith("GET /orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_issuer_that_refuses_or_fails_names_the_refusal_once_and_logs_each_occurrence()
    {
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();

        foreach (var person in new[] { "removed", "failing", "unbound", "removed" })
        {
            using (NhAiAgentInvocationScopes.Begin(NhAiMvcBridgeDelegatedReadTests.AgentScope(person)))
            {
                Assert.Equal(NhAiDelegatedReadFailureCodes.IssuerRefused, await CodeAsync(scope.ServiceProvider, Query, new { resource = "order" }));
            }
        }

        var warning = Assert.Single(Warnings(factory, 8));
        Assert.Contains(NhAiDelegatedReadFailureCodes.IssuerRefused, warning.Message);
        // Each occurrence keeps its own warning: a failure (event 1) and three refusals (event 2).
        Assert.Single(Warnings(factory, 1));
        Assert.Equal(3, Warnings(factory, 2).Count);
        Assert.DoesNotContain(factory.Requests, request => request.StartsWith("GET /orders", StringComparison.Ordinal));
        AssertContentFree(factory);
    }

    [Fact]
    public async Task A_credential_the_hosts_scheme_rejects_names_the_rejection_once()
    {
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();

        // The issuer signs with a key the host's bearer scheme does not trust.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            using (NhAiAgentInvocationScopes.Begin(NhAiMvcBridgeDelegatedReadTests.AgentScope("foreign")))
            {
                Assert.Equal(NhAiDelegatedReadFailureCodes.Rejected, await CodeAsync(scope.ServiceProvider, Query, new { resource = "order" }));
            }
        }

        var warning = Assert.Single(Warnings(factory, 9));
        Assert.Contains("UseScheme", warning.Message);
        Assert.NotEmpty(Warnings(factory, 3));
        Assert.DoesNotContain(factory.Requests, request => request.StartsWith("GET /orders", StringComparison.Ordinal));
        AssertContentFree(factory);
    }

    [Fact]
    public async Task A_read_the_API_answers_with_401_for_no_cause_it_recorded_is_rejected()
    {
        // The host authenticates its own default scheme, not the bearer scheme the credential is for,
        // so its authorization answers 401 before the bridge's MVC filter runs.
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory(bearerIsDefault: false);
        await using var scope = factory.Services.CreateAsyncScope();

        using (NhAiAgentInvocationScopes.Begin(NhAiMvcBridgeDelegatedReadTests.AgentScope("reader")))
        {
            Assert.Equal(NhAiDelegatedReadFailureCodes.Rejected, await CodeAsync(scope.ServiceProvider, Query, new { resource = "order" }));
            Assert.Equal(NhAiDelegatedReadFailureCodes.Rejected, await CodeAsync(scope.ServiceProvider, Get, new { resource = "order", id = 7 }));
        }

        Assert.Contains(factory.Requests, request => request.StartsWith("GET /orders", StringComparison.Ordinal));
        Assert.Single(Warnings(factory, 9));
        Assert.Empty(Warnings(factory, 6));
    }

    [Fact]
    public async Task A_person_who_may_not_lend_rights_now_keeps_the_unauthenticated_code_without_a_cause_warning()
    {
        await using var factory = NhAiMvcBridgeDelegatedReadTests.CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var changed = NhAiMvcBridgeDelegatedReadTests.AgentScope("reader");

        using (NhAiAgentInvocationScopes.Begin(changed with { Subject = changed.Subject with { ResponsibleActorId = "someone-else" } }))
        {
            Assert.Equal(NhAiBridgeFailureCodes.Unauthenticated, await CodeAsync(scope.ServiceProvider, Query, new { resource = "order" }));
        }

        Assert.DoesNotContain(factory.Logs.Records, record => record.Category == DiagnosticsCategory && record.EventId is >= 6 and <= 9);
    }

    private static async Task<string?> CodeAsync(IServiceProvider services, string exportName, object input)
    {
        var result = await NhAiMvcBridgeDelegatedReadTests.InvokeAsync(services, exportName, input);
        Assert.False(result.Success, result.Errors);
        Assert.Contains("message", result.Errors, StringComparison.Ordinal);
        return result.Property("code").GetString();
    }

    private static IReadOnlyList<CapturedLogRecord> Warnings(BridgeApiFactory factory, int eventId)
    {
        return factory.Logs.Records
            .Where(record => record.Category == DiagnosticsCategory && record.EventId == eventId && record.Level == LogLevel.Warning)
            .ToArray();
    }

    /// <summary>No warning carries a token, a claim value or what an issuer's exception said.</summary>
    private static void AssertContentFree(BridgeApiFactory factory)
    {
        Assert.All(
            factory.Logs.Records.Where(record => record.Category == DiagnosticsCategory),
            record =>
            {
                Assert.DoesNotContain("eyJ", record.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("@example.test", record.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(TestPolicies.OrderView, record.Message, StringComparison.Ordinal);
            });
    }
}
