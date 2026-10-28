using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Finds the approval policy of a proposal. The most specific reference wins: the trigger of the
/// run, then the skill the run works in, then the tool within the agent, then the agent. Without a reference, chat turns use the
/// chat default and runs use the trigger default. The application's selector, when one is registered,
/// then chooses among the registered policies (<see cref="SelectAsync"/>).
/// </summary>
internal sealed class NhAssistantApprovalPolicies(
    NhAssistantRegistrationState registration,
    IServiceProvider services,
    ILogger<NhAssistantApprovalPolicies> logger)
{
    private static readonly Action<ILogger, string, string, string, Exception?> SelectorFailed =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(1, "AssistantApprovalPolicySelectorFailed"),
            "The approval policy selector {SelectorType} failed for a proposal of tool {ToolId} with {ExceptionType}; the proposal was refused.");

    private static readonly Action<ILogger, string, string, double, Exception?> SelectorTimedOut =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Warning,
            new EventId(2, "AssistantApprovalPolicySelectorTimedOut"),
            "The approval policy selector {SelectorType} did not answer for a proposal of tool {ToolId} within {TimeLimitMs} ms; the proposal was refused.");

    private static readonly Action<ILogger, string, string, Exception?> SelectorChoseUnknown =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(3, "AssistantApprovalPolicySelectorChoseUnknown"),
            "The approval policy selector {SelectorType} chose a policy that is not registered for a proposal of tool {ToolId}; the proposal was refused.");

    /// <summary>
    /// The policy of a proposal after the application's selector: the resolved one when no selector is
    /// registered or it keeps it, the registered policy it chose, or a refusal code when it chose a policy
    /// that is not registered, threw or did not answer within its time limit. The caller's cancellation
    /// ends the selection with the caller.
    /// </summary>
    /// <param name="resolved">The policy code resolved.</param>
    /// <param name="selection">The proposal, with <see cref="NhAssistantApprovalPolicySelection.ResolvedPolicyId"/>
    /// set to <paramref name="resolved"/>'s id.</param>
    public async Task<NhAssistantPolicyChoice> SelectAsync(
        NhAiApprovalPolicy resolved,
        NhAssistantApprovalPolicySelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(selection);
        var selector = services.GetService<INhAssistantApprovalPolicySelector>();
        if (selector is null)
        {
            return NhAssistantPolicyChoice.Of(resolved);
        }

        var selectorType = selector.GetType().Name;
        var timeLimit = registration.ApprovalPolicySelectorTimeLimit;
        string? chosen;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(timeLimit);
            try
            {
                // The time limit holds also for a selector that does not observe its token.
                chosen = await selector.SelectAsync(selection, limit.Token).AsTask().WaitAsync(timeLimit, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SelectorTimedOut(logger, selectorType, selection.ToolId, timeLimit.TotalMilliseconds, null);
                return NhAssistantPolicyChoice.Refusal(NhAssistantErrorCodes.ApprovalPolicySelectorTimedOut);
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                SelectorTimedOut(logger, selectorType, selection.ToolId, timeLimit.TotalMilliseconds, null);
                return NhAssistantPolicyChoice.Refusal(NhAssistantErrorCodes.ApprovalPolicySelectorTimedOut);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // Content-free: the selector's and the exception's type, never its message.
                SelectorFailed(logger, selectorType, selection.ToolId, exception.GetType().FullName ?? exception.GetType().Name, null);
                return NhAssistantPolicyChoice.Refusal(NhAssistantErrorCodes.ApprovalPolicySelectorFailed);
            }
        }

        if (chosen is null || string.Equals(chosen, resolved.Id, StringComparison.Ordinal))
        {
            return NhAssistantPolicyChoice.Of(resolved);
        }
        if (!registration.ApprovalPolicies.TryGetValue(chosen, out var policy))
        {
            SelectorChoseUnknown(logger, selectorType, selection.ToolId, null);
            return NhAssistantPolicyChoice.Refusal(NhAssistantErrorCodes.ApprovalPolicyUnknown);
        }

        return NhAssistantPolicyChoice.Of(policy);
    }

    /// <summary>
    /// A policy the approver directory can be asked about: a registered one, or one of the defaults code
    /// resolves without a registration (<c>trigger-default</c> and <c>chat-default</c>).
    /// </summary>
    public NhAiApprovalPolicy? FindKnown(string policyId)
    {
        if (Find(policyId) is { } registered)
        {
            return registered;
        }

        if (string.Equals(policyId, NhAiApprovalPolicy.TriggerDefault.Id, StringComparison.Ordinal))
        {
            return NhAiApprovalPolicy.TriggerDefault;
        }

        var chat = NhAiApprovalPolicy.ChatDefault(registration.Limits.ApprovalLifetime);
        return string.Equals(policyId, chat.Id, StringComparison.Ordinal) ? chat : null;
    }

    public NhAiApprovalPolicy Resolve(
        string agentId,
        string toolId,
        string? triggerId,
        bool isRun,
        string? skillId = null)
    {
        if (isRun
            && triggerId is not null
            && registration.Triggers.TryGetValue(triggerId, out var trigger)
            && trigger.ApprovalPolicyId is { } triggerPolicy)
        {
            return Get(triggerPolicy);
        }

        if (registration.AgentRuns.TryGetValue(agentId, out var options)
            && options.FindApprovalPolicy(toolId, skillId) is { } agentPolicy)
        {
            return Get(agentPolicy);
        }

        return isRun
            ? NhAiApprovalPolicy.TriggerDefault
            : NhAiApprovalPolicy.ChatDefault(registration.Limits.ApprovalLifetime);
    }

    /// <summary>
    /// The policy of a consultation's proposal (CP5): the consulted agent's policy for the tool
    /// itself, else the consultation's own policy, else the agent's policy, else the trigger default.
    /// </summary>
    public NhAiApprovalPolicy ResolveConsultation(string agentId, string toolId, string? consultationPolicyId)
    {
        registration.AgentRuns.TryGetValue(agentId, out var options);
        if (options?.FindToolApprovalPolicy(toolId) is { } toolPolicy)
        {
            return Get(toolPolicy);
        }
        if (consultationPolicyId is not null)
        {
            return Get(consultationPolicyId);
        }

        return options?.ApprovalPolicyId is { } agentPolicy
            ? Get(agentPolicy)
            : NhAiApprovalPolicy.TriggerDefault;
    }

    public NhAiApprovalPolicy? Find(string policyId)
    {
        return registration.ApprovalPolicies.TryGetValue(policyId, out var policy) ? policy : null;
    }

    private NhAiApprovalPolicy Get(string policyId)
    {
        return registration.ApprovalPolicies.TryGetValue(policyId, out var policy)
            ? policy
            : throw new InvalidOperationException($"Approval policy '{policyId}' is not registered.");
    }
}

/// <summary>
/// The approval policy a proposal waits under after the application's selector, or the code that refuses
/// the proposal.
/// </summary>
internal sealed record NhAssistantPolicyChoice(NhAiApprovalPolicy? Policy, string? RefusalCode)
{
    public bool Refused => RefusalCode is not null;

    public static NhAssistantPolicyChoice Of(NhAiApprovalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new NhAssistantPolicyChoice(policy, null);
    }

    public static NhAssistantPolicyChoice Refusal(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new NhAssistantPolicyChoice(null, code);
    }
}

/// <summary>
/// Stores an approval policy on the proposal it applied to, so a later change to the registered
/// policy never alters a proposal that is waiting.
/// </summary>
/// <summary>
/// The reminder schedule of a deferred proposal: each reminder of the policy counts from the
/// moment the proposal started waiting, and no reminder falls on or after its expiry. Like the
/// expiry, the schedule is on the application's clock.
/// </summary>
internal static class NhAssistantApprovalReminders
{
    public static DateTimeOffset? Next(NhAiApprovalPolicy policy, DateTimeOffset since, int index, DateTimeOffset expiresAt)
    {
        if (index < 0 || index >= policy.Reminders.Count)
        {
            return null;
        }

        var due = since.Add(policy.Reminders[index]);
        return due < expiresAt ? due : null;
    }

    /// <summary>
    /// When the proposal started waiting on the application's clock: its escalation, or else the
    /// moment its proposal was generated. Its <c>CreatedAt</c> records the same moment on the
    /// system clock.
    /// </summary>
    public static DateTimeOffset Since(Entities.AssistantApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return approval.EscalatedAt
            ?? Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson).GeneratedAt;
    }
}

internal static class NhAssistantApprovalPolicySnapshot
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(NhAiApprovalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var snapshot = new Snapshot(
            policy.Id,
            (long)policy.Lifetime.TotalSeconds,
            policy.Approvers.Select(ToRule).ToArray(),
            policy.FourEyes,
            NhAssistantApprovalModes.From(policy.Mode),
            policy.OnExpiry.ToString(),
            policy.EscalationPolicyId,
            policy.Reminders.Select(reminder => (long)reminder.TotalSeconds).ToArray(),
            policy.RequireUnchangedSubject,
            policy.BindTo.ToString(),
            policy.StartFollowUpRun);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    public static NhAiApprovalPolicy Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, Options)
            ?? throw new InvalidOperationException("A stored approval policy is empty.");
        return new NhAiApprovalPolicy(snapshot.Id)
        {
            Lifetime = TimeSpan.FromSeconds(snapshot.LifetimeSeconds),
            Approvers = snapshot.Approvers.Select(FromRule).ToArray(),
            FourEyes = snapshot.FourEyes,
            Mode = snapshot.Mode == NhAssistantApprovalModes.Deferred ? NhAiApprovalMode.Deferred : NhAiApprovalMode.Inline,
            OnExpiry = Enum.Parse<NhAiApprovalExpiryAction>(snapshot.OnExpiry),
            EscalationPolicyId = snapshot.EscalationPolicyId,
            Reminders = snapshot.ReminderSeconds.Select(seconds => TimeSpan.FromSeconds(seconds)).ToArray(),
            RequireUnchangedSubject = snapshot.RequireUnchangedSubject,
            BindTo = Enum.Parse<NhAiApprovalBinding>(snapshot.BindTo),
            StartFollowUpRun = snapshot.StartFollowUpRun
        };
    }

    private static Rule ToRule(NhAiApproverRule rule)
    {
        return rule switch
        {
            NhAiInitiatorApproverRule => new Rule("initiator", null, null, null),
            NhAiSubjectRoleApproverRule subjectRole => new Rule("subject-role", (int)subjectRole.Role, subjectRole.NamedRole, null),
            NhAiPolicyApproverRule policy => new Rule("policy", null, null, policy.PolicyName),
            _ => throw new InvalidOperationException("Unknown approver rule.")
        };
    }

    private static NhAiApproverRule FromRule(Rule rule)
    {
        return rule.Kind switch
        {
            "initiator" => NhAiApprovers.Initiator,
            "subject-role" => new NhAiSubjectRoleApproverRule((NhAiSubjectRole)(rule.Role ?? 0), rule.NamedRole),
            "policy" => new NhAiPolicyApproverRule(rule.PolicyName!),
            _ => throw new InvalidOperationException("A stored approver rule is corrupt.")
        };
    }

    private sealed record Snapshot(
        string Id,
        long LifetimeSeconds,
        Rule[] Approvers,
        bool FourEyes,
        string Mode,
        string OnExpiry,
        string? EscalationPolicyId,
        long[] ReminderSeconds,
        bool RequireUnchangedSubject,
        string BindTo,
        bool StartFollowUpRun);

    private sealed record Rule(string Kind, int? Role, string? NamedRole, string? PolicyName);
}
