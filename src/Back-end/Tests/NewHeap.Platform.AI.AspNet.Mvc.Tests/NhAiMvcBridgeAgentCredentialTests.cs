using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// In an agent turn the bridge sends the request's bearer token only inside the initiator's own request
/// turn. A turn that runs in another person's request, as an approver's decision that continues the
/// initiator's turn does, and a headless turn send nobody's token, so the API never executes a call the
/// gate authorized for the initiator as the person whose request carries the turn.
/// </summary>
public sealed class NhAiMvcBridgeAgentCredentialTests
{
    private const string ReadTool = "test-api.order.get-by-id";

    [Fact]
    public async Task An_agent_turn_sends_the_request_token_only_inside_its_initiators_own_request()
    {
        using var factory = new BridgeApiFactory();

        // The initiator's own request turn: the call carries the initiator's token.
        var own = await ExecuteAsync(factory, TestTokenHandler.Viewer, AgentScope(initiator: "viewer", requestTurn: true), NhAiGrantSource.Delegated);
        // The approver decides the initiator's proposal and the turn continues in the approver's
        // request: the approver could read the order, but the approver's token is never sent.
        var approver = await ExecuteAsync(factory, TestTokenHandler.Manager, AgentScope(initiator: "viewer", requestTurn: true), NhAiGrantSource.Delegated);
        // Nor with an agent grant, whose call would execute as the approver all the same.
        var granted = await ExecuteAsync(factory, TestTokenHandler.Manager, AgentScope(initiator: "viewer", requestTurn: true), NhAiGrantSource.Agent);
        // A headless turn that happens to run inside someone's request sends that person's token neither.
        var headless = await ExecuteAsync(factory, TestTokenHandler.Manager, AgentScope(initiator: "manager", requestTurn: false), NhAiGrantSource.Delegated);
        // Outside an agent turn the request gate bound the call to the request's person, as before.
        var chat = await ExecuteAsync(factory, TestTokenHandler.Manager, scope: null, NhAiGrantSource.None);

        Assert.True(own.Success);
        Assert.Equal(42, own.Data!.Body!.Value.GetProperty("id").GetInt32());
        Assert.Equal(NhAiBridgeFailureCodes.Unauthenticated, Code(approver));
        Assert.Equal(NhAiBridgeFailureCodes.Unauthenticated, Code(granted));
        Assert.Equal(NhAiBridgeFailureCodes.Unauthenticated, Code(headless));
        Assert.True(chat.Success);
    }

    private static Task<TaskResult<NhAiBridgeResponse>> ExecuteAsync(
        BridgeApiFactory factory,
        string token,
        NhAiAgentInvocationScope? scope,
        NhAiGrantSource grantSource)
    {
        return factory.AsUserAsync(token, async services =>
        {
            var catalog = services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
            var descriptor = catalog.Descriptors.Single(item => item.Id == ReadTool);
            Assert.True(catalog.TryGetAction(descriptor, out var action));
            using var agentScope = scope is null ? null : NhAiAgentInvocationScopes.Begin(scope);
            return await services.GetRequiredService<INhAiMvcBridgeExecutor>().ExecuteAsync(
                action,
                descriptor,
                JsonSerializer.SerializeToElement(new { id = 42 }),
                TestContexts.Create() with { GrantSource = grantSource });
        });
    }

    private static NhAiAgentInvocationScope AgentScope(string initiator, bool requestTurn)
    {
        return new NhAiAgentInvocationScope(
            "order-planner",
            "agent:order-planner",
            1,
            new NhAiSubjectInfo(new NhAiSubjectRef("order", "42"), "order:42"),
            "accountable-1",
            Guid.NewGuid().ToString(),
            "assistant")
        {
            Accountability = NhAiAgentAccountability.ServiceAccount,
            InitiatorActorId = initiator,
            DelegateFromInitiator = true,
            IsRequestTurn = requestTurn
        };
    }

    private static string? Code(TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}
