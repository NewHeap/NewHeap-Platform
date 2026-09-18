using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantDurableManagerTests(AssistantDatabaseFixture database)
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Tool_reservations_fail_once_the_daily_budget_of_the_actor_is_exhausted(
        AssistantTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var manager = services.GetRequiredService<NhAssistantBudgetManager>();
        var turn = AssistantTestData.Turn("owner-1", limits => limits.DailyToolCallBudgetPerActor = 2);

        using (NhAssistantExecutionScope.EnterTurn(turn))
        {
            var results = new List<bool>();
            for (var index = 0; index < 3; index++)
            {
                using (NhAssistantExecutionScope.EnterCall(AssistantTestData.Call()))
                {
                    results.Add((await manager.ReserveAsync(ToolRequest())).Success);
                }
            }

            var modelCall = await manager.ReserveAsync(new NhAiBudgetRequest(Guid.NewGuid(), "project-chat", 1, 120, 60, null));

            Assert.Equal([true, true, false], results);
            Assert.True(modelCall.Success);
        }

        using (NhAssistantExecutionScope.EnterTurn(AssistantTestData.Turn("owner-2", limits => limits.DailyToolCallBudgetPerActor = 2)))
        using (NhAssistantExecutionScope.EnterCall(AssistantTestData.Call()))
        {
            Assert.True((await manager.ReserveAsync(ToolRequest())).Success);
        }

        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var ledger = await context.BudgetLedgers.SingleAsync(item => item.ActorId == "owner-1");
        Assert.Equal(2, ledger.ToolCalls);
        Assert.Equal(1, ledger.ModelCalls);
        Assert.Equal(120, ledger.InputTokens);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), ledger.Day);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Concurrent_reservations_never_exceed_the_daily_budget(AssistantTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var manager = services.GetRequiredService<NhAssistantBudgetManager>();
        var turn = AssistantTestData.Turn("owner-concurrent", limits => limits.DailyToolCallBudgetPerActor = 5);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using (NhAssistantExecutionScope.EnterTurn(turn))
            using (NhAssistantExecutionScope.EnterCall(AssistantTestData.Call()))
            {
                return (await manager.ReserveAsync(ToolRequest())).Success;
            }
        }));

        Assert.Equal(5, results.Count(success => success));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Budget_requests_outside_an_assistant_turn_use_the_previous_manager(
        AssistantTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var manager = services.GetRequiredService<NhAssistantBudgetManager>();
        var fallback = services.GetRequiredKeyedService<INhAiBudgetManager>(NhAssistantFallbacks.ServiceKey);

        var result = await manager.ReserveAsync(ToolRequest());

        Assert.True(result.Success);
        Assert.Single(Assert.IsType<NhAiTestBudgetManager>(fallback).Requests);
        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Equal(0, await context.BudgetLedgers.CountAsync());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_duplicate_lease_is_refused_and_a_completed_key_is_a_duplicate(
        AssistantTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var manager = services.GetRequiredService<NhAssistantIdempotencyManager>();
        var request = LeaseRequest("key-1", "hash-a");

        var first = await manager.AcquireAsync(request);
        var concurrent = await manager.AcquireAsync(request);
        await manager.CompleteAsync(first, NhAiOutcomeKind.Succeeded);
        var replay = await manager.AcquireAsync(request);
        var changedArguments = await manager.AcquireAsync(LeaseRequest("key-1", "hash-b"));
        var otherKey = await manager.AcquireAsync(LeaseRequest("key-2", "hash-a"));

        Assert.Equal(NhAiIdempotencyDecisionKind.Acquired, first.Decision);
        Assert.Equal(NhAiIdempotencyDecisionKind.Conflict, concurrent.Decision);
        Assert.Equal("idempotency-in-progress", concurrent.Code);
        Assert.Equal(NhAiIdempotencyDecisionKind.Duplicate, replay.Decision);
        Assert.Equal(NhAiIdempotencyDecisionKind.Conflict, changedArguments.Decision);
        Assert.Equal(NhAiIdempotencyDecisionKind.Acquired, otherKey.Decision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CompleteAsync(first, NhAiOutcomeKind.Succeeded).AsTask());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_expired_lease_can_be_taken_over_and_fences_out_the_previous_holder(
        AssistantTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var manager = services.GetRequiredService<NhAssistantIdempotencyManager>();
        var request = LeaseRequest("key-expired", "hash-a");
        var first = await manager.AcquireAsync(request);
        await using (var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.IdempotencyLeases
                .Where(lease => lease.LeaseId == first.LeaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    lease => lease.ExpiresAt,
                    DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var second = await manager.AcquireAsync(request);

        Assert.Equal(NhAiIdempotencyDecisionKind.Acquired, second.Decision);
        Assert.Equal("acquired-after-expiry", second.Code);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CompleteAsync(first, NhAiOutcomeKind.Succeeded).AsTask());
        await manager.CompleteAsync(second, NhAiOutcomeKind.Succeeded);
        await using var verify = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var stored = await verify.IdempotencyLeases.SingleAsync(lease => lease.IdempotencyKey == "key-expired");
        Assert.Equal(2, stored.Generation);
        Assert.Equal(NhAssistantLeaseStatuses.Completed, stored.Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Evidence_is_returned_only_for_the_matching_proposal_hash_and_actor(
        AssistantTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var provider1 = services.GetRequiredService<NhAssistantApprovalEvidenceProvider>();
        var factory = services.GetRequiredService<INhAiProposalFactory>();
        var arguments = new AssistantTestData.StatusInput(Guid.NewGuid(), "Active");
        var generatedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var proposal = factory.Create(new NhAiProposalCreateRequest(
            Guid.NewGuid(),
            Guid.NewGuid().ToString(),
            NhAiActorKind.Agent,
            "assistant-agent:project-assistant",
            "owner-1",
            AssistantTestData.MutationDescriptor,
            arguments,
            [new NhAiProposalTarget("division", "d-1")],
            "Change one project status.",
            ["idempotent-mutation"],
            new Dictionary<string, string>(),
            new NhAiActionBudget(1),
            generatedAt,
            generatedAt.AddMinutes(15)));
        var approved = await SeedApprovalAsync(services, proposal, NhAssistantApprovalStatuses.Approved, "owner-1");
        var pending = await SeedApprovalAsync(services, proposal with { ProposalId = Guid.NewGuid() }, NhAssistantApprovalStatuses.Pending, null);
        var tampered = await SeedApprovalAsync(
            services,
            proposal,
            NhAssistantApprovalStatuses.Approved,
            "owner-1",
            proposalHash: new string('0', 64));
        var selfApproved = await SeedApprovalAsync(
            services,
            proposal,
            NhAssistantApprovalStatuses.Approved,
            proposal.ActorId);
        var context = new NhAiInvocationContext(proposal.ActorId, "assistant", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = "owner-1"
        };

        var evidence = await provider1.GetAsync(
            AssistantTestData.MutationDescriptor,
            WithIds(context, approved),
            arguments);
        var otherActor = await provider1.GetAsync(
            AssistantTestData.MutationDescriptor,
            WithIds(context with { AccountableOwnerId = "owner-2" }, approved),
            arguments);
        var notDecided = await provider1.GetAsync(AssistantTestData.MutationDescriptor, WithIds(context, pending), arguments);
        var wrongHash = await provider1.GetAsync(AssistantTestData.MutationDescriptor, WithIds(context, tampered), arguments);
        var agentSelfApproval = await provider1.GetAsync(
            AssistantTestData.MutationDescriptor,
            WithIds(context, selfApproved),
            arguments);

        Assert.NotNull(evidence);
        Assert.Equal(proposal.ProposalHash, evidence.Approval.ProposalHash);
        Assert.Equal("owner-1", evidence.Approval.ApprovingActorId);
        Assert.Null(otherActor);
        Assert.Null(notDecided);
        Assert.Null(wrongHash);
        Assert.Null(agentSelfApproval);
        var validation = new NhAiApprovalValidator(factory).Validate(
            AssistantTestData.MutationDescriptor,
            WithIds(context, approved) with { RunId = proposal.RunId },
            arguments,
            evidence,
            DateTimeOffset.UtcNow);
        Assert.True(validation.Succeeded, validation.Code);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_call_without_evidence_captures_the_exact_arguments_for_the_proposal(
        AssistantTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var evidenceProvider = services.GetRequiredService<NhAssistantApprovalEvidenceProvider>();
        var arguments = new AssistantTestData.StatusInput(Guid.NewGuid(), "Closed");
        var context = new NhAiInvocationContext("assistant-agent:project-assistant", "assistant", new Dictionary<string, string>());
        var call = AssistantTestData.Call();

        NhAiApprovalEvidence? evidence;
        using (NhAssistantExecutionScope.EnterTurn(AssistantTestData.Turn("owner-1")))
        using (NhAssistantExecutionScope.EnterCall(call))
        {
            evidence = await evidenceProvider.GetAsync(AssistantTestData.MutationDescriptor, context, arguments);
        }

        Assert.Null(evidence);
        Assert.Same(arguments, call.CapturedArguments);
        Assert.Same(context, call.CapturedContext);
    }

    private static NhAiInvocationContext WithIds(NhAiInvocationContext context, AssistantApproval approval)
    {
        return context with
        {
            ProposalId = approval.ProposalId.ToString(),
            ApprovalId = approval.Id.ToString()
        };
    }

    private static async Task<AssistantApproval> SeedApprovalAsync(
        ServiceProvider services,
        NhAiProposal proposal,
        string status,
        string? decidedBy,
        string? proposalHash = null)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = "owner-1",
            AgentId = "project-assistant",
            AgentVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        var approval = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TurnId = Guid.NewGuid(),
            ToolInvocationId = Guid.NewGuid(),
            ProposalId = proposal.ProposalId,
            ProposalHash = proposalHash ?? proposal.ProposalHash,
            ProposalJson = NhAssistantProposalSerializer.Serialize(proposal),
            ToolId = proposal.ToolId,
            Summary = proposal.Intent,
            ArgumentsPreview = "{}",
            Status = status,
            CreatedAt = now.AddSeconds(-4),
            ExpiresAt = proposal.ExpiresAt,
            DecidedByActorId = decidedBy,
            DecidedAt = decidedBy is null ? null : now.AddSeconds(-1),
            ApprovalExpiresAt = decidedBy is null ? null : now.AddMinutes(5),
            ConcurrencyStamp = Guid.NewGuid()
        };
        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.Conversations.Add(conversation);
        context.Approvals.Add(approval);
        await context.SaveChangesAsync();
        return approval;
    }

    private static NhAiBudgetRequest ToolRequest()
    {
        return new NhAiBudgetRequest(Guid.NewGuid(), "project-chat", 1, 0, 0, null);
    }

    private static NhAiIdempotencyRequest LeaseRequest(string key, string argumentHash)
    {
        return new NhAiIdempotencyRequest(
            Guid.NewGuid(),
            "projects.change-status",
            1,
            "assistant-agent:project-assistant",
            key,
            argumentHash,
            null);
    }

    private Task<ServiceProvider> CreateServicesAsync(AssistantTestProvider provider)
    {
        return database.CreateMigratedStorageAsync(provider, configure: services =>
        {
            services.AddSingleton(new NhAssistantRegistrationState());
            services.AddKeyedSingleton<INhAiBudgetManager>(
                NhAssistantFallbacks.ServiceKey,
                new NhAiTestBudgetManager());
            services.AddKeyedSingleton<INhAiApprovalEvidenceProvider>(
                NhAssistantFallbacks.ServiceKey,
                new NoEvidenceProvider());
            services.AddSingleton<INhAiProposalFactory, NhAiProposalFactory>();
            services.AddSingleton<NhAssistantBudgetManager>();
            services.AddSingleton<NhAssistantIdempotencyManager>();
            services.AddSingleton<NhAssistantApprovalEvidenceProvider>();
        });
    }

    private sealed class NoEvidenceProvider : INhAiApprovalEvidenceProvider
    {
        public ValueTask<NhAiApprovalEvidence?> GetAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiApprovalEvidence?>(null);
        }
    }
}
