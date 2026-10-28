using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// The route of one call of a consulted agent that changes something: how its proposal is decided
/// and whose rights execute it, or why it was refused.
/// </summary>
/// <param name="Route">One of <see cref="NhAssistantConsultationRoutes"/>; <see langword="null"/> when refused.</param>
/// <param name="Policy">The approval policy the proposal stores, as the route adapts it.</param>
internal sealed record NhAssistantConsultationRoute(string? Route, NhAiApprovalPolicy Policy)
{
    /// <summary>
    /// One of <see cref="NhAssistantConsultationRefusalReasons"/> when no route applies.
    /// </summary>
    public string? RefusalReason { get; init; }

    /// <summary>
    /// Who answers for the proposal when it executes with the agent's own rights: the technical
    /// account or the agent. <see langword="null"/> keeps the asker.
    /// </summary>
    public string? AccountableOwnerId { get; init; }

    /// <summary>
    /// What allows the call when it executes with the agent's own rights: its grant or the technical
    /// account's rights. <see langword="null"/> keeps the asker's rights.
    /// </summary>
    public NhAiGrantSource? GrantSource { get; init; }

    public bool Refused => Route is null;
}

/// <summary>
/// Decides the route of a consulted agent's proposal (CP3): the asker decides it in their own
/// conversation when that applies, else it waits at the subject under the configured route, else it
/// is refused. It also moves a proposal the asker leaves for the subject's approvers (CP20) and
/// describes such a proposal for the asking conversation.
/// </summary>
internal sealed class NhAssistantConsultationProposals(
    NhAssistantRegistrationState registration,
    NhAssistantApprovalPolicies policies,
    NhAssistantRunStore runs,
    INhAssistantStore store,
    NhAssistantThreadService threads,
    NhAssistantAgentCatalog agents,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentEvents events,
    NhAssistantAgentNotifier notifier,
    INhAiProposalFactory proposalFactory,
    IEnumerable<INhAiToolCatalog> catalogs,
    IServiceProvider services)
{
    private readonly IReadOnlyList<INhAiToolCatalog> _catalogs = catalogs.ToArray();

    /// <summary>
    /// The application's clock, which a proposal's lifetime follows.
    /// </summary>
    private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// The route of one call of the consult turn. A route where the asker decides reserves the one
    /// decision of the consultation and of the asking turn; release it with
    /// <see cref="ReleaseAskerDecision"/> when the proposal is not recorded after all.
    /// </summary>
    /// <param name="chosen">The policy the application's selector chose after
    /// <see cref="NhAssistantApprovalPolicies.ResolveConsultation"/> (FB19), which the route adapts;
    /// <see langword="null"/> resolves it here without a selector.</param>
    public async Task<NhAssistantConsultationRoute> DecideAsync(
        NhAssistantConsultationScope consultation,
        NhAssistantAgentTurnContext agentTurn,
        NhAssistantAgent agent,
        NhAiToolDescriptor descriptor,
        NhAiApprovalPolicy? chosen,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        ArgumentNullException.ThrowIfNull(agentTurn);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(descriptor);

        var policy = chosen
            ?? policies.ResolveConsultation(agentTurn.Thread.AgentId, descriptor.Id, consultation.Options.ProposalPolicyId);
        var routing = consultation.Routing;
        string? askerRefusal = null;
        if (routing.AskerDecides && registration.TryGetApplicationExecution(descriptor.Id, out _))
        {
            // The application executes the tool after the approval, so its result never reaches the
            // asking turn: the proposal waits at the subject, where the application executes it.
            askerRefusal = NhAssistantConsultationRefusalReasons.ApplicationExecutes;
        }
        else if (routing.AskerDecides)
        {
            askerRefusal = await AskerRefusalAsync(consultation, agentTurn.Thread, policy, descriptor.Id, cancellationToken);
            if (askerRefusal is null)
            {
                return AskerRoute(policy);
            }
        }

        return routing.AtSubjectRights switch
        {
            NhAssistantConsultationRights.Asker => new NhAssistantConsultationRoute(
                NhAssistantConsultationRoutes.SubjectAskerRights,
                policy with { Mode = NhAiApprovalMode.Deferred }),
            NhAssistantConsultationRights.Agent => await AgentRightsRouteAsync(
                agent,
                agentTurn.Options,
                agentTurn.Subject,
                agentTurn.Thread,
                policy,
                descriptor,
                consultation.AskerActorId,
                consultation.ConsultationId,
                cancellationToken),
            _ => Refused(policy, askerRefusal ?? NhAssistantConsultationRefusalReasons.NoRoute)
        };
    }

    /// <summary>
    /// Releases the decision a route reserved for the asker when no proposal was recorded.
    /// </summary>
    public static void ReleaseAskerDecision(NhAssistantConsultationScope consultation)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        consultation.Outcome.ReleaseAskerDecision();
        consultation.Asker?.AskingTurn.ReleaseAskerDecision();
    }

    /// <summary>
    /// The routes of a consultation now: the registration within the administration's switches of the
    /// agent.
    /// </summary>
    public NhAssistantConsultationRouting RoutingOf(NhAssistantConsultationOptions options, NhAssistantAgent? agent)
    {
        ArgumentNullException.ThrowIfNull(options);
        return NhAssistantConsultationRouting.Of(options, agent?.ConsultationRoutes);
    }

    /// <summary>
    /// The registration of the consultation that recorded <paramref name="consultation"/>, or
    /// <see langword="null"/> when the code no longer allows it.
    /// </summary>
    public NhAssistantConsultationOptions? OptionsOf(AssistantConsultation consultation)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        return registration.AgentRuns.TryGetValue(consultation.AgentId, out var options)
            ? options.Consultations.FirstOrDefault(item => string.Equals(item.SubjectType, consultation.SubjectType, StringComparison.Ordinal))
            : null;
    }

    /// <summary>
    /// Describes the proposals of consultations that the asking conversation's people decide there:
    /// the consulted agent, the subject and the route they may leave it for.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, NhAssistantApprovalConsultationView>> DescribeAsync(
        IReadOnlyList<NhAssistantAskerDecisionRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var described = new Dictionary<Guid, NhAssistantApprovalConsultationView>();
        var agentsById = new Dictionary<string, NhAssistantAgent?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!agentsById.TryGetValue(row.Consultation.AgentId, out var agent))
            {
                agent = await agents.FindAsync(row.Consultation.AgentId, includeDisabled: true, cancellationToken);
                agentsById[row.Consultation.AgentId] = agent;
            }

            var options = OptionsOf(row.Consultation);
            var leaveRoute = options is null || row.Approval.Status != NhAssistantApprovalStatuses.Pending
                ? null
                : LeaveRouteOf(RoutingOf(options, agent), row.Thread, agent);
            described[row.Approval.Id] = new NhAssistantApprovalConsultationView(
                row.Consultation.Id,
                row.Consultation.AgentId,
                row.Thread.SubjectType,
                row.Thread.SubjectId,
                row.Thread.SubjectDisplayName,
                leaveRoute)
            {
                MovedToApprovalId = row.Approval.ExecutionResultCode == NhAssistantErrorCodes.ConsultProposalMoved
                    ? row.Approval.SupersededByApprovalId
                    : null
            };
        }

        return described;
    }

    /// <summary>
    /// The route at the subject a person may leave a proposal for, as far as the configuration says:
    /// the route is configured and switched on and, for the agent's rights, the agent does not answer
    /// to a person and the subject is not the whole application. The rest is checked when they leave
    /// it.
    /// </summary>
    public static string? LeaveRouteOf(NhAssistantConsultationRouting routing, AssistantThread thread, NhAssistantAgent? agent)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(thread);
        if (routing.AtSubjectRights != NhAssistantConsultationRights.Agent)
        {
            return routing.AtSubjectRoute;
        }

        return agent is null
               || string.Equals(thread.Accountability, NhAssistantAccountabilities.SubjectResponsible, StringComparison.Ordinal)
               || string.Equals(thread.SubjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal)
            ? null
            : NhAssistantConsultationRoutes.SubjectAgentRights;
    }

    /// <summary>
    /// Leaves a pending proposal the asker would decide in their own conversation for the subject's
    /// approvers (CP20): when a route at the subject applies to it now, a new proposal waits at the
    /// subject under that route's rules and the asker's proposal closes as moved, decided by the asker.
    /// Fails with <see cref="NhAssistantErrorCodes.ConsultProposalRefused"/>, whose message names the
    /// reason, when no route applies.
    /// </summary>
    public async Task<TaskResult<AssistantApproval>> MoveToSubjectAsync(
        AssistantApproval approval,
        string askerActorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(askerActorId);
        if (approval.ConsultationId is not { } consultationId
            || approval.Mode != NhAssistantApprovalModes.Inline
            || approval.Status != NhAssistantApprovalStatuses.Pending)
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalNotPending, "The proposal is no longer pending.");
        }

        var consultation = await runs.FindConsultationAsync(consultationId, cancellationToken);
        var thread = await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        var agent = thread is null ? null : await agents.FindAsync(thread.AgentId, includeDisabled: false, cancellationToken);
        var options = consultation is null ? null : OptionsOf(consultation);
        var invocation = await store.FindToolInvocationAsync(approval.ToolInvocationId, cancellationToken);
        if (consultation is null || thread is null || agent is null || options is null || invocation is null
            || !registration.AgentRuns.TryGetValue(thread.AgentId, out var runOptions))
        {
            return RefusedMove(NhAssistantConsultationRefusalReasons.NoRoute);
        }

        var routing = RoutingOf(options, agent);
        var subject = await subjects.DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), cancellationToken);
        var descriptor = FindDescriptor(invocation);
        if (routing.AtSubjectRights is null || subject is null || descriptor is null)
        {
            return RefusedMove(NhAssistantConsultationRefusalReasons.NoRoute);
        }

        // The proposal at the subject is a new proposal: code resolves its policy and the application's
        // selector may choose another registered one (FB19), as for the consulted agent's own call.
        var previous = NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        var resolved = policies.ResolveConsultation(thread.AgentId, approval.ToolId, options.ProposalPolicyId);
        var choice = await policies.SelectAsync(
            resolved,
            new NhAssistantApprovalPolicySelection(thread.AgentId, approval.ToolId, subject.Subject, previous.Arguments, resolved.Id)
            {
                Source = NhAssistantApprovalPolicySource.Consultation,
                InitiatorActorId = askerActorId,
                TenantId = subject.TenantId
            },
            cancellationToken);
        if (choice.Refused)
        {
            return TaskResult<AssistantApproval>.Failed(
                choice.RefusalCode!,
                "The approval policy of the proposal at the subject could not be chosen.");
        }

        var policy = choice.Policy!;
        var route = routing.AtSubjectRights == NhAssistantConsultationRights.Agent
            ? await AgentRightsRouteAsync(agent, runOptions, subject, thread, policy, descriptor, askerActorId, consultationId, cancellationToken)
            : new NhAssistantConsultationRoute(NhAssistantConsultationRoutes.SubjectAskerRights, policy with { Mode = NhAiApprovalMode.Deferred });
        if (route.Refused)
        {
            return RefusedMove(route.RefusalReason!);
        }

        var proposedAt = _applicationClock.GetUtcNow();
        var expiresAt = proposedAt.Add(route.Policy.Lifetime);
        var proposal = proposalFactory.Create(new NhAiProposalCreateRequest(
            Guid.NewGuid(),
            previous.RunId,
            previous.ActorKind,
            previous.ActorId,
            route.AccountableOwnerId ?? previous.AccountableOwnerId,
            descriptor,
            previous.Arguments,
            previous.Targets,
            previous.Intent,
            previous.ExpectedEffects,
            previous.Constraints,
            previous.EstimatedBudget,
            proposedAt,
            expiresAt)
        {
            ModelProfileName = previous.ModelProfileName,
            PromptVersion = previous.PromptVersion,
            PromptHash = previous.PromptHash,
            CatalogHash = previous.CatalogHash,
            ContextHash = previous.ContextHash
        });

        var guards = services.GetRequiredService<NhAssistantProposalGuards>();
        var now = DateTimeOffset.UtcNow;
        // The asker moves a proposal that a call made earlier, so no call proposes now: the context names
        // no invocation, conversation, turn or channel (FB26).
        var keys = await guards.GetKeysAsync(
            new NhAiProposalGuardContext(proposal.ProposalId, approval.ToolId, proposal.Arguments, subject.Subject, thread.ScopeKey, now),
            cancellationToken);
        var duplicate = await services.GetRequiredService<NhAssistantProposalDuplicates>()
            .CheckAsync(thread, approval.ToolId, approval.ArgumentsHash ?? string.Empty, keys, route.Policy, cancellationToken);
        if (duplicate.Outcome != NhAssistantProposeOutcome.Created)
        {
            return TaskResult<AssistantApproval>.Failed(
                duplicate.Outcome == NhAssistantProposeOutcome.RecentlyRejected
                    ? NhAssistantErrorCodes.ProposalRecentlyRejected
                    : NhAssistantErrorCodes.ProposalAlreadyOpen,
                "The same proposal already waits at the subject or was rejected recently.");
        }

        var grantSource = route.GrantSource ?? approval.GrantSource;
        var waitingCall = new AssistantToolInvocation
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = invocation.TurnId,
            MessageId = Guid.Empty,
            CallId = invocation.CallId,
            FunctionName = invocation.FunctionName,
            ToolId = invocation.ToolId,
            ToolVersion = invocation.ToolVersion,
            ContractHash = invocation.ContractHash,
            DisplayName = invocation.DisplayName,
            Status = NhAssistantToolCallStatuses.AwaitingApproval,
            ArgumentsJson = invocation.ArgumentsJson,
            DataClassification = invocation.DataClassification,
            RetentionCategory = invocation.RetentionCategory,
            StartedAt = now,
            ProposalId = proposal.ProposalId,
            GrantSource = grantSource
        };
        var moved = new AssistantApproval
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TurnId = approval.TurnId,
            ToolInvocationId = waitingCall.Id,
            ProposalId = proposal.ProposalId,
            ProposalHash = proposal.ProposalHash,
            ProposalJson = NhAssistantProposalSerializer.Serialize(proposal),
            ToolId = approval.ToolId,
            Summary = approval.Summary,
            PresentationJson = approval.PresentationJson,
            PresentationsJson = approval.PresentationsJson,
            ArgumentsPreview = approval.ArgumentsPreview,
            TargetsJson = approval.TargetsJson,
            Status = NhAssistantApprovalStatuses.Pending,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            PolicyId = route.Policy.Id,
            Mode = NhAssistantApprovalModes.Deferred,
            PolicyJson = NhAssistantApprovalPolicySnapshot.Serialize(route.Policy),
            InitiatorActorId = askerActorId,
            SubjectVersion = subject.Version,
            ParentSubjectType = approval.ParentSubjectType,
            ParentSubjectId = approval.ParentSubjectId,
            ParentDisplayName = approval.ParentDisplayName,
            GrantSource = grantSource,
            ConsultationId = consultationId,
            NextReminderAt = NhAssistantApprovalReminders.Next(route.Policy, proposedAt, 0, expiresAt),
            ArgumentsHash = approval.ArgumentsHash,
            ConcurrencyStamp = Guid.NewGuid()
        };
        waitingCall.ApprovalId = moved.Id;

        // The asker's proposal closes first, so a decision that raced ahead wins and nothing moves.
        var closed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Rejected,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = askerActorId,
                DecidedAt = proposedAt,
                ExecutionResultCode = NhAssistantErrorCodes.ConsultProposalMoved,
                SupersededByApprovalId = moved.Id
            },
            cancellationToken);
        if (!closed)
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalNotPending, "The proposal is no longer pending.");
        }

        await store.AddToolInvocationAsync(waitingCall, CancellationToken.None);
        await store.AddApprovalAsync(
            moved,
            keys.Select(key => new AssistantApprovalKey
                {
                    ApprovalId = moved.Id,
                    ScopeKey = thread.ScopeKey,
                    KeyType = key.Type,
                    KeyId = key.Id
                })
                .ToArray(),
            CancellationToken.None);

        var closedApproval = await store.FindApprovalAsync(approval.Id, CancellationToken.None) ?? approval;
        await events.ProposalAsync(
            NhAssistantAuditEventKind.ApprovalRejected,
            thread,
            closedApproval,
            askerActorId,
            NhAssistantErrorCodes.ConsultProposalMoved,
            CancellationToken.None);
        await events.ProposalAsync(
            NhAssistantAuditEventKind.ApprovalRequested,
            thread,
            moved,
            askerActorId,
            NhAiToolFailureCodes.ApprovalRequired,
            CancellationToken.None);
        await notifier.NotifyAsync(
            NhAssistantAgentNotificationKind.RunEndedWithProposals,
            thread,
            null,
            moved,
            askerActorId,
            false,
            CancellationToken.None,
            proposalCount: 1);
        return TaskResult<AssistantApproval>.Succeeded(moved);
    }

    /// <summary>
    /// Why the asker cannot decide the call in their own conversation (CP4, CP7, CP9), or
    /// <see langword="null"/> when they can; the decision is then reserved.
    /// </summary>
    private static async Task<string?> AskerRefusalAsync(
        NhAssistantConsultationScope consultation,
        AssistantThread thread,
        NhAiApprovalPolicy policy,
        string toolId,
        CancellationToken cancellationToken)
    {
        if (consultation.Asker is not { } asker)
        {
            return NhAssistantConsultationRefusalReasons.AskerNotWaiting;
        }
        if (policy.FourEyes)
        {
            // Someone else deciding inline would execute with the asker's rebuilt principal.
            return NhAssistantConsultationRefusalReasons.FourEyes;
        }
        if (!await consultation.Viewer.MayDecideAsync(policy, thread, toolId, cancellationToken))
        {
            return NhAssistantConsultationRefusalReasons.AskerNotApprover;
        }
        if (!consultation.Outcome.TryReserveAskerDecision())
        {
            return NhAssistantConsultationRefusalReasons.DecisionWaiting;
        }
        if (!asker.AskingTurn.TryReserveAskerDecision())
        {
            consultation.Outcome.ReleaseAskerDecision();
            return NhAssistantConsultationRefusalReasons.DecisionWaiting;
        }

        return null;
    }

    /// <summary>
    /// The asker decides now: the proposal waits inline for at most the application's approval
    /// lifetime, because the asking conversation waits with it (CP8).
    /// </summary>
    private NhAssistantConsultationRoute AskerRoute(NhAiApprovalPolicy policy)
    {
        var lifetime = policy.Lifetime < registration.Limits.ApprovalLifetime
            ? policy.Lifetime
            : registration.Limits.ApprovalLifetime;
        return new NhAssistantConsultationRoute(
            NhAssistantConsultationRoutes.AskerDecides,
            policy with { Mode = NhAiApprovalMode.Inline, Lifetime = lifetime });
    }

    /// <summary>
    /// The route that executes with the agent's own rights (B), or why it is refused: the agent
    /// answers to a person (CP10), the subject is the whole application (CP14), nobody but the asker
    /// would approve (CP12), the agent's own rights do not allow the call (CP11, CP13) or the asker
    /// has too many such proposals open at the subject (CP15). The policy always becomes four-eyes.
    /// </summary>
    private async Task<NhAssistantConsultationRoute> AgentRightsRouteAsync(
        NhAssistantAgent agent,
        NhAssistantAgentRunOptions options,
        NhAiSubjectInfo subject,
        AssistantThread thread,
        NhAiApprovalPolicy policy,
        NhAiToolDescriptor descriptor,
        string askerActorId,
        Guid consultationId,
        CancellationToken cancellationToken)
    {
        var fourEyes = policy with { Mode = NhAiApprovalMode.Deferred, FourEyes = true };
        if (options.Accountability == NhAiAgentAccountability.SubjectResponsible)
        {
            return Refused(fourEyes, NhAssistantConsultationRefusalReasons.AgentAnswersToPerson);
        }
        if (NhAiSystemSubjects.IsSystem(subject.Subject))
        {
            return Refused(fourEyes, NhAssistantConsultationRefusalReasons.WholeApplication);
        }
        if (policy.Approvers.All(rule => rule is NhAiInitiatorApproverRule))
        {
            return Refused(fourEyes, NhAssistantConsultationRefusalReasons.NoApprover);
        }

        var accountable = threads.ResolveAccountableOwner(agent, subject);
        var authorizer = services.GetService<INhAiAgentToolAuthorizer>();
        if (!accountable.Success || authorizer is null)
        {
            return Refused(fourEyes, NhAssistantConsultationRefusalReasons.AgentNotGranted);
        }

        // The agent's own scope as its headless runs have it, never delegated from the asker: its
        // grants, trigger-only ones included, and the technical account's rights where it acts with them.
        var scope = new NhAiAgentInvocationScope(
            agent.Id,
            agent.Definition.ActorId,
            agent.Definition.Version,
            subject,
            accountable.Data!,
            consultationId.ToString(),
            NhAssistantInvocationGate.Purpose)
        {
            Accountability = options.Accountability,
            InitiatorActorId = askerActorId,
            DelegateFromInitiator = false,
            IsRequestTurn = false,
            Grants = options.Grants,
            ElevatedReads = options.ElevatedReads,
            RequireApprovalForEffects = true,
            ActWithAccountableRights = options.ActWithAccountableRights
                && options.Accountability != NhAiAgentAccountability.Agent
        };
        var grantSource = await authorizer.AuthorizeAsync(descriptor, scope, cancellationToken);
        if (grantSource is not (NhAiGrantSource.Agent or NhAiGrantSource.Accountable))
        {
            return Refused(fourEyes, NhAssistantConsultationRefusalReasons.AgentNotGranted);
        }

        var open = await runs.CountOpenAgentRightsConsultationProposalsAsync(thread.ConversationId, askerActorId, cancellationToken);
        if (open >= registration.Limits.MaxOpenConsultationProposalsPerAsker)
        {
            return Refused(fourEyes, NhAssistantConsultationRefusalReasons.OpenLimit);
        }

        return new NhAssistantConsultationRoute(NhAssistantConsultationRoutes.SubjectAgentRights, fourEyes)
        {
            AccountableOwnerId = accountable.Data!,
            GrantSource = grantSource
        };
    }

    private NhAiToolDescriptor? FindDescriptor(AssistantToolInvocation invocation)
    {
        return _catalogs
            .SelectMany(catalog => catalog.Descriptors)
            .FirstOrDefault(descriptor => string.Equals(descriptor.Id, invocation.ToolId, StringComparison.Ordinal)
                && descriptor.Version == invocation.ToolVersion
                && string.Equals(descriptor.ContractHash, invocation.ContractHash, StringComparison.Ordinal));
    }

    private static NhAssistantConsultationRoute Refused(NhAiApprovalPolicy policy, string reason)
    {
        return new NhAssistantConsultationRoute(null, policy) { RefusalReason = reason };
    }

    private static TaskResult<AssistantApproval> RefusedMove(string reason)
    {
        return TaskResult<AssistantApproval>.Failed(
            NhAssistantErrorCodes.ConsultProposalRefused,
            string.Create(CultureInfo.InvariantCulture, $"No route at the subject applies: {reason}."));
    }

    /// <summary>
    /// The result the consulted model reads when its call is not proposed.
    /// </summary>
    public static JsonElement RefusalResult(string reason)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = NhAssistantErrorCodes.ConsultProposalRefused,
                ["reason"] = reason,
                ["message"] = reason switch
                {
                    NhAssistantConsultationRefusalReasons.AgentNotGranted =>
                        "Nothing was proposed: your own rights do not allow this change. Describe the change in your answer instead; do not retry it.",
                    NhAssistantConsultationRefusalReasons.OpenLimit =>
                        "Nothing was proposed: the person who asked has as many proposals waiting at this subject as allowed. Describe the change in your answer instead; do not retry it.",
                    NhAssistantConsultationRefusalReasons.DecisionWaiting =>
                        "Nothing was proposed: the person who asked already has a proposal of yours to decide. Describe this change in your answer instead; do not retry it.",
                    _ => "Nothing was proposed: this consultation cannot propose this change. Describe the change in your answer instead, so the person can make it themselves; do not retry it."
                }
            },
            NhAssistantContent.JsonOptions);
    }
}
