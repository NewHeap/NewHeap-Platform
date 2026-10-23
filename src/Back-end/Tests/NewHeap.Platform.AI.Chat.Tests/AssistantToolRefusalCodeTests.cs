using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A refused tool call names the refusal's own code, such as a missing capability or an effect policy
/// that denies, in the conversation's tool call, the assistant's audit and what the model reads,
/// rather than the outcome kind <c>ai-tool-authorization-denied</c>.
/// </summary>
public sealed class AssistantToolRefusalCodeTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Fact]
    public async Task A_refused_tool_call_carries_the_refusals_own_code_in_the_conversation_the_audit_and_for_the_model()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-refused")
            .RespondWithText("I may not search the projects.")
            .RespondWithText("The search was refused.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            configure: services => services.Replace(ServiceDescriptor.Scoped<INhAiEffectPolicy, DenyingEffectPolicy>()));
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Which roadmap projects exist?");

        var completed = turn.Single<NhAssistantToolCompletedEvent>();
        Assert.Equal((NhAssistantToolCallStatuses.Failed, NhAiToolFailureCodes.EffectDenied), (completed.Status, completed.ResultCode));
        Assert.Empty(host.Tools.Contexts);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var call = await context.ToolInvocations.SingleAsync(item => item.ConversationId == conversation.Id);
            Assert.Equal(NhAiToolFailureCodes.EffectDenied, call.ResultCode);
        }
        var invoked = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked);
        Assert.Equal(NhAiToolFailureCodes.EffectDenied, invoked.ResultCode);
        var audited = Assert.Single(host.Audit.Records);
        Assert.Equal((NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.EffectDenied), (audited.Outcome, audited.ResultCode));

        // The model reads the code in the summary of the earlier call on the next turn.
        await host.SendAsync(conversation.Id, "Why not?");
        var replayed = string.Join("\n", model.Requests[^1].Messages.Select(message => message.Text));
        Assert.Contains(NhAiToolFailureCodes.EffectDenied, replayed, StringComparison.Ordinal);
        Assert.DoesNotContain(NhAiToolFailureCodes.AuthorizationDenied, replayed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NhAiAgentFailureCodes.NotGranted, null, NhAiAgentFailureCodes.NotGranted)]
    [InlineData(NhAiToolFailureCodes.CapabilityDenied, null, NhAiToolFailureCodes.CapabilityDenied)]
    [InlineData(NhAiToolFailureCodes.EffectDenied, null, NhAiToolFailureCodes.EffectDenied)]
    [InlineData(NhAiToolFailureCodes.ApprovalDelegationInvalid, "approval-delegation-invalid", NhAiToolFailureCodes.ApprovalDelegationInvalid)]
    [InlineData("approval-invalid-expired-or-replayed", "approval-invalid-expired-or-replayed", "approval-invalid-expired-or-replayed")]
    [InlineData(null, null, NhAiToolFailureCodes.AuthorizationDenied)]
    [InlineData(null, "execution-evidence-invalid", NhAiToolFailureCodes.ExecutionEvidenceInvalid)]
    public void A_refusal_keeps_the_result_code_of_its_audit(string? resultCode, string? approvalCode, string expected)
    {
        var record = new NhAiAuditRecord(
            Guid.NewGuid(),
            "projects.search",
            1,
            "user-1",
            "project-assistance",
            NhAiOutcomeKind.AuthorizationDenied,
            DateTimeOffset.UtcNow)
        {
            ResultCode = resultCode,
            ApprovalCode = approvalCode
        };

        var outcome = NhAssistantToolOutcome.FromAudit(record);

        Assert.Equal((NhAssistantToolOutcomeKind.Failed, expected), (outcome.Kind, outcome.Code));
    }

    [Fact]
    public void A_missing_approval_stays_the_proposal_and_a_success_stays_a_success()
    {
        var missing = new NhAiAuditRecord(Guid.NewGuid(), "projects.change-status", 1, "user-1", "project-assistance", NhAiOutcomeKind.ApprovalRequired, DateTimeOffset.UtcNow);
        var succeeded = missing with { Outcome = NhAiOutcomeKind.Succeeded };

        Assert.Equal(NhAssistantToolOutcomeKind.ApprovalMissing, NhAssistantToolOutcome.FromAudit(missing).Kind);
        Assert.Equal((NhAssistantToolOutcomeKind.Succeeded, "succeeded"), (NhAssistantToolOutcome.FromAudit(succeeded).Kind, NhAssistantToolOutcome.FromAudit(succeeded).Code));
    }

    private sealed class DenyingEffectPolicy : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(NhAiEffectDecisionKind.Deny, "test-denied"));
        }
    }
}
