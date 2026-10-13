using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Finds the approval policy of a proposal. The most specific reference wins: the trigger of the
/// run, then the tool within the agent, then the agent. Without a reference, chat turns use the
/// chat default and runs use the trigger default.
/// </summary>
internal sealed class NhAssistantApprovalPolicies(NhAssistantRegistrationState registration)
{
    public NhAiApprovalPolicy Resolve(
        string agentId,
        string toolId,
        string? triggerId,
        bool isRun)
    {
        if (isRun
            && triggerId is not null
            && registration.Triggers.TryGetValue(triggerId, out var trigger)
            && trigger.ApprovalPolicyId is { } triggerPolicy)
        {
            return Get(triggerPolicy);
        }

        if (registration.AgentRuns.TryGetValue(agentId, out var options)
            && options.FindApprovalPolicy(toolId) is { } agentPolicy)
        {
            return Get(agentPolicy);
        }

        return isRun
            ? NhAiApprovalPolicy.TriggerDefault
            : NhAiApprovalPolicy.ChatDefault(registration.Limits.ApprovalLifetime);
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
/// Stores an approval policy on the proposal it applied to, so a later change to the registered
/// policy never alters a proposal that is waiting.
/// </summary>
/// <summary>
/// The reminder schedule of a deferred proposal: each reminder of the policy counts from the
/// moment the proposal started waiting, and no reminder falls on or after its expiry.
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
