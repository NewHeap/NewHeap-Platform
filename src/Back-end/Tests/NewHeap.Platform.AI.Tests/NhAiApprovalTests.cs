using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiApprovalTests
{
    [Fact]
    public void Proposal_hash_is_canonical_and_independent_of_dictionary_order()
    {
        var factory = new NhAiProposalFactory();
        var first = factory.Create(CreateRequest(new Dictionary<string, string>
        {
            ["reason"] = "approved-maintenance",
            ["window"] = "business-hours"
        }));
        var second = factory.Create(CreateRequest(new Dictionary<string, string>
        {
            ["window"] = "business-hours",
            ["reason"] = "approved-maintenance"
        }));

        Assert.Equal(first.ProposalHash, second.ProposalHash);
        Assert.Equal(64, first.ProposalHash.Length);
    }

    [Fact]
    public void Approval_binds_exact_arguments_targets_constraints_budget_and_expiry()
    {
        var factory = new NhAiProposalFactory();
        var proposal = factory.Create(CreateRequest(new Dictionary<string, string>
        {
            ["reason"] = "approved-maintenance"
        }));
        var approval = CreateApproval(proposal);
        var context = CreateContext(proposal, approval);
        var validator = new NhAiApprovalValidator(factory);

        var valid = validator.Validate(
            Descriptor,
            context,
            new StatusChangeInput(ProjectId, "active"),
            new NhAiApprovalEvidence(proposal, approval),
            GeneratedAt.AddMinutes(2));
        var changedArguments = validator.Validate(
            Descriptor,
            context,
            new StatusChangeInput(ProjectId, "archived"),
            new NhAiApprovalEvidence(proposal, approval),
            GeneratedAt.AddMinutes(2));
        var changedTarget = validator.Validate(
            Descriptor,
            context,
            new StatusChangeInput(ProjectId, "active"),
            new NhAiApprovalEvidence(
                proposal,
                approval with
                {
                    AllowedTargets = [new NhAiProposalTarget("project", Guid.NewGuid().ToString())]
                }),
            GeneratedAt.AddMinutes(2));
        var changedCatalog = validator.Validate(
            Descriptor,
            context with { CatalogHash = "catalog-v2" },
            new StatusChangeInput(ProjectId, "active"),
            new NhAiApprovalEvidence(proposal, approval),
            GeneratedAt.AddMinutes(2));

        Assert.True(valid.Succeeded);
        Assert.Equal("arguments-changed", changedArguments.Code);
        Assert.Equal("target-not-approved", changedTarget.Code);
        Assert.Equal("catalog-hash-mismatch", changedCatalog.Code);
    }

    [Fact]
    public void Agent_cannot_approve_its_own_proposal()
    {
        var factory = new NhAiProposalFactory();
        var proposal = factory.Create(CreateRequest(new Dictionary<string, string>()));
        var approval = CreateApproval(proposal) with
        {
            ApprovingActorId = proposal.ActorId
        };

        var result = new NhAiApprovalValidator(factory).Validate(
            Descriptor,
            CreateContext(proposal, approval),
            new StatusChangeInput(ProjectId, "active"),
            new NhAiApprovalEvidence(proposal, approval),
            GeneratedAt.AddMinutes(2));

        Assert.False(result.Succeeded);
        Assert.Equal("agent-self-approval-denied", result.Code);
    }

    [Fact]
    public void Empty_approval_identity_is_rejected()
    {
        var factory = new NhAiProposalFactory();
        var proposal = factory.Create(CreateRequest(new Dictionary<string, string>()));
        var approval = CreateApproval(proposal) with { ApprovalId = Guid.Empty };

        var result = new NhAiApprovalValidator(factory).Validate(
            Descriptor,
            CreateContext(proposal, approval),
            new StatusChangeInput(ProjectId, "active"),
            new NhAiApprovalEvidence(proposal, approval),
            GeneratedAt.AddMinutes(2));

        Assert.False(result.Succeeded);
        Assert.Equal("approval-identity-invalid", result.Code);
    }

    [Fact]
    public async Task Mutation_invoker_requires_valid_approval_before_execution()
    {
        var executed = false;
        var contextWithoutEvidence = new NhAiInvocationContext(
            "agent-1",
            "project-maintenance",
            new Dictionary<string, string>());
        var deniedInvoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(contextWithoutEvidence));

        var denied = await deniedInvoker.InvokeAsync(
            Descriptor,
            new StatusChangeInput(ProjectId, "active"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(denied.Success);
        Assert.False(executed);

        var factory = new NhAiProposalFactory();
        var proposal = factory.Create(CreateRequest(
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow));
        var approval = CreateApproval(proposal);
        var context = CreateContext(proposal, approval);
        var allowedInvoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context),
            [],
            new RequireApprovalPolicy(),
            new FixedEvidenceProvider(new NhAiApprovalEvidence(proposal, approval)),
            new NhAiApprovalValidator(factory),
            new AcquireIdempotencyManager(),
            [new SuccessfulVerifier()],
            new NhAiTestBudgetManager());

        var allowed = await allowedInvoker.InvokeAsync(
            Descriptor,
            new StatusChangeInput(ProjectId, "active"),
            (_, _) => Task.FromResult(TaskResult<string>.Succeeded("executed")));

        Assert.True(allowed.Success);
        Assert.Equal("executed", allowed.Data);
    }

    private static readonly Guid ProposalId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ApprovalId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid ProjectId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset GeneratedAt = DateTimeOffset.Parse("2026-08-25T10:00:00Z");
    private static readonly NhAiToolDescriptor Descriptor = new(
        "projects.change-status",
        1,
        "Change a project status after approval.",
        typeof(StatusChangeInput),
        typeof(string),
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Local,
        true,
        ["project-manage"])
    {
        Approval = NhAiApprovalRequirement.Required,
        ContractHash = "contract-hash-v1",
        Idempotency = NhAiIdempotencySupport.Required,
        VerifierId = "project-status"
    };

    private static NhAiProposalCreateRequest CreateRequest(
        IReadOnlyDictionary<string, string> constraints,
        DateTimeOffset? generatedAt = null)
    {
        var timestamp = generatedAt ?? GeneratedAt;
        return new NhAiProposalCreateRequest(
            ProposalId,
            "run-1",
            NhAiActorKind.Agent,
            "agent-1",
            "owner-1",
            Descriptor,
            new StatusChangeInput(ProjectId, "active"),
            [new NhAiProposalTarget("project", ProjectId.ToString())],
            "Move the project into active delivery.",
            ["project-status-change"],
            constraints,
            new NhAiActionBudget(1, 0.01m),
            timestamp,
            timestamp.AddMinutes(10))
        {
            ModelProfileName = "project-assistant",
            PromptVersion = "project-status-v1",
            PromptHash = "prompt-hash-v1",
            CatalogHash = "catalog-v1",
            ContextHash = "context-v1"
        };
    }

    private static NhAiApproval CreateApproval(NhAiProposal proposal)
    {
        return new NhAiApproval(
            ApprovalId,
            proposal.ProposalId,
            proposal.ProposalHash,
            "approver-1",
            proposal.Targets,
            proposal.Constraints,
            proposal.GeneratedAt,
            proposal.GeneratedAt.AddMinutes(5),
            new NhAiActionBudget(1, 0.01m));
    }

    private static NhAiInvocationContext CreateContext(
        NhAiProposal proposal,
        NhAiApproval approval)
    {
        return new NhAiInvocationContext(
            proposal.ActorId,
            "project-maintenance",
            new Dictionary<string, string>())
        {
            ActorKind = proposal.ActorKind,
            RunId = proposal.RunId,
            AccountableOwnerId = proposal.AccountableOwnerId,
            ModelProfileName = proposal.ModelProfileName,
            PromptVersion = proposal.PromptVersion,
            PromptHash = proposal.PromptHash,
            CatalogHash = proposal.CatalogHash,
            ContextHash = proposal.ContextHash,
            ProposalId = proposal.ProposalId.ToString(),
            ApprovalId = approval.ApprovalId.ToString(),
            IdempotencyKey = "proposal-1-project-status"
        };
    }

    private sealed record StatusChangeInput(
        Guid ProjectId,
        string Status);

    private sealed class RequireApprovalPolicy : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.RequireApproval,
                "test-approval-required"));
        }
    }

    private sealed class FixedEvidenceProvider(
        NhAiApprovalEvidence evidence) : INhAiApprovalEvidenceProvider
    {
        public ValueTask<NhAiApprovalEvidence?> GetAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiApprovalEvidence?>(evidence);
        }
    }

    private sealed class AcquireIdempotencyManager : INhAiIdempotencyManager
    {
        public ValueTask<NhAiIdempotencyLease> AcquireAsync(
            NhAiIdempotencyRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiIdempotencyLease(
                NhAiIdempotencyDecisionKind.Acquired,
                "acquired",
                "lease-1"));
        }

        public ValueTask CompleteAsync(
            NhAiIdempotencyLease lease,
            NhAiOutcomeKind outcome,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SuccessfulVerifier : INhAiToolVerifier
    {
        public string Id => "project-status";

        public ValueTask<NhAiVerificationResult> VerifyAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            object? executionResult,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiVerificationResult(
                true,
                "verified",
                "project-status:30000000-0000-0000-0000-000000000003"));
        }
    }
}
