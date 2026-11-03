using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.RuleScripts;

/// <summary>
/// The rule scripts in effect for a rule turn of an agent or skill: the live version, which takes the place
/// of a code rule, and the candidate that runs in shadow next to the live rules, within the agent's ceiling.
/// </summary>
internal sealed record NhAssistantRuleScriptsInEffect(
    AssistantRuleScriptVersion? Live,
    AssistantRuleScriptVersion? Shadow,
    NhAssistantRuleScriptAgentCeiling? Ceiling)
{
    public static NhAssistantRuleScriptsInEffect None { get; } = new(null, null, null);

    public bool Any => Live is not null || Shadow is not null;
}

/// <summary>
/// What rule scripts share in a rule turn, a shadow run and a dry run: the writes a version may call
/// within its ceiling, the tools a script names, what a write would become without effects, the rule
/// result of a script's outcome and the record of what a version did on one subject.
/// </summary>
internal static class NhAssistantRuleScriptRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The trigger a dry run names in the script's <c>run</c>.</summary>
    public const string DryRunTriggerId = "dry-run";

    /// <summary>The most characters of a recorded write's or proposal's input.</summary>
    public const int MaxRecordedInputCharacters = 4_000;

    /// <summary>
    /// The ceiling of the agent: its own, otherwise the one of agents created in the administration.
    /// </summary>
    public static NhAssistantRuleScriptAgentCeiling? CeilingOf(NhAssistantRegistrationState registration, string agentId, string source)
    {
        return registration.RuleScriptsAsData?.For(agentId, string.Equals(source, NhAssistantAgentSources.Admin, StringComparison.Ordinal));
    }

    /// <summary>The writes a version stored, by tool id.</summary>
    public static IReadOnlyList<string> WritesOf(AssistantRuleScriptVersion version)
    {
        return JsonSerializer.Deserialize<string[]>(version.WritesJson, Json) ?? [];
    }

    /// <summary>
    /// The writes the version may call now: those it opted in to that the ceiling still allows.
    /// </summary>
    public static IReadOnlySet<string> WritesInEffect(AssistantRuleScriptVersion version, NhAssistantRuleScriptAgentCeiling ceiling)
    {
        return WritesOf(version).Where(ceiling.Writes.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The tools a script of the run may name, of the governed functions its rules may use: never a
    /// composition, the script tool or a consultation tool.
    /// </summary>
    public static IReadOnlyList<NhAiToolDescriptor> ToolsOf(IReadOnlyDictionary<string, AIFunction> functions)
    {
        var descriptors = functions.Values
            .OfType<INhAiGovernedAIFunction>()
            .Select(function => function.Descriptor);
        return NhAssistantToolChains.StepToolsOf(descriptors, NhAssistantRuleScriptTool.Descriptor).ToArray();
    }

    /// <summary>
    /// What a write would become without effects: run when it needs no approval and the agent executes
    /// with a person or account answering for it, a proposal when it needs one, the agent only proposes or
    /// answers for itself, otherwise the approval policy decides.
    /// </summary>
    public static string Would(NhAiToolDescriptor descriptor, NhAssistantAgentDefinition agent, NhAiAgentAccountability accountability, bool shadowThread)
    {
        if (!agent.CanMutate || shadowThread || accountability == NhAiAgentAccountability.Agent)
        {
            return NhAssistantRuleScriptWouldOutcomes.Propose;
        }

        return descriptor.Approval switch
        {
            NhAiApprovalRequirement.NotRequired => NhAssistantRuleScriptWouldOutcomes.Run,
            NhAiApprovalRequirement.PolicyControlled => NhAssistantRuleScriptWouldOutcomes.Decide,
            _ => NhAssistantRuleScriptWouldOutcomes.Propose
        };
    }

    /// <summary>
    /// The rule result of a completed script (RB-1, item 4): its end with its alerts raised and resolved,
    /// as a code rule returns it, so alerts, proposals, revalidation and the run history work unchanged.
    /// </summary>
    public static NhAssistantRuleResult ToRuleResult(NhAssistantRuleScriptOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var end = outcome.End ?? new NhAssistantRuleScriptEnd(NhAssistantRuleScriptEndKind.Done, "The rule script ran.");
        var result = end.Kind switch
        {
            NhAssistantRuleScriptEndKind.HandOver => NhAssistantRuleResult.HandOver(end.Text),
            NhAssistantRuleScriptEndKind.UseSkill => NhAssistantRuleResult.UseSkill(end.SkillId!, end.Text),
            _ => NhAssistantRuleResult.Done(end.Text)
        };
        if (outcome.Alerts.Count > 0)
        {
            result = result.WithAlerts([.. outcome.Alerts]);
        }
        if (outcome.Resolves.Count > 0)
        {
            result = result.ResolveAlerts([.. outcome.Resolves]);
        }

        return result;
    }

    /// <summary>
    /// The record of what a version did on one subject without effects.
    /// </summary>
    public static NhAssistantRuleScriptSubjectRunDto RecordOf(
        NhAiSubjectInfo subject,
        NhAssistantRuleScriptOutcome outcome,
        NhAssistantRuleScriptRecording recording)
    {
        return new NhAssistantRuleScriptSubjectRunDto(
            subject.Subject.Type,
            subject.Subject.Id,
            subject.DisplayName,
            outcome.Completed ? "completed" : "stopped",
            outcome.Code,
            outcome.Message,
            outcome.Line,
            outcome.Column,
            outcome.End is { } end ? new NhAssistantRuleScriptEndDto(EndName(end.Kind), end.Text, end.SkillId) : null,
            outcome.Calls
                .Select(call => new NhAssistantRuleScriptCallDto(call.Sequence, call.Tool, call.ToolId, call.Outcome, call.Code, call.Write))
                .ToArray(),
            recording.Writes,
            recording.Proposals,
            outcome.Alerts
                .Select(alert => new NhAssistantRuleScriptAlertDto(
                    alert.KindId,
                    alert.Subject.Type,
                    alert.Subject.Id,
                    alert.Key,
                    alert.Urgency is { } urgency ? urgency.ToString().ToLowerInvariant() : null,
                    alert.Parameters,
                    alert.Recipients,
                    alert.ExpiresAt))
                .ToArray(),
            outcome.Resolves
                .Select(key => new NhAssistantRuleScriptAlertKeyDto(key.KindId, key.Subject.Type, key.Subject.Id, key.Key))
                .ToArray(),
            outcome.Logs);
    }

    /// <summary>
    /// The record of a subject the dry run could not run on, such as one that does not exist.
    /// </summary>
    public static NhAssistantRuleScriptSubjectRunDto FailedRecord(NhAiSubjectRef subject, string code, string message)
    {
        return new NhAssistantRuleScriptSubjectRunDto(
            subject.Type,
            subject.Id,
            null,
            "failed",
            code,
            message,
            null,
            null,
            null,
            [],
            [],
            [],
            [],
            [],
            []);
    }

    public static string EndName(NhAssistantRuleScriptEndKind kind)
    {
        return kind switch
        {
            NhAssistantRuleScriptEndKind.HandOver => "hand-over",
            NhAssistantRuleScriptEndKind.UseSkill => "use-skill",
            _ => "done"
        };
    }

    /// <summary>
    /// The stored record of a dry run or shadow run, bounded: subjects that do not fit are left out from the
    /// end, and the record says how many there were.
    /// </summary>
    public static string Serialize(IReadOnlyList<NhAssistantRuleScriptSubjectRunDto> subjects)
    {
        var kept = subjects.ToList();
        while (true)
        {
            var json = JsonSerializer.Serialize(kept, Json);
            if (json.Length <= NhAssistantRuleScriptLimits.MaxRecordCharacters || kept.Count <= 1)
            {
                return json.Length <= NhAssistantRuleScriptLimits.MaxRecordCharacters
                    ? json
                    : JsonSerializer.Serialize(
                        kept.Select(subject => subject with { Calls = [], Writes = [], Proposals = [], Logs = [] }).ToArray(),
                        Json);
            }

            kept.RemoveAt(kept.Count - 1);
        }
    }

    public static NhAssistantRuleScriptRunRecordDto ToDto(AssistantRuleScriptRunRecord record)
    {
        var subjects = JsonSerializer.Deserialize<NhAssistantRuleScriptSubjectRunDto[]>(record.RecordJson, Json) ?? [];
        return new NhAssistantRuleScriptRunRecordDto(
            record.Id,
            record.AgentId,
            record.SkillId.Length == 0 ? null : record.SkillId,
            record.Version,
            record.Kind,
            record.RunId,
            record.RanBy,
            record.RanAt,
            record.SubjectCount,
            record.CompletedCount,
            subjects);
    }

    /// <summary>
    /// The input of a recorded write or proposal as JSON, bounded.
    /// </summary>
    public static string Bounded(JsonElement input)
    {
        var json = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();
        return NhAssistantContent.Bound(json, MaxRecordedInputCharacters);
    }

    /// <summary>
    /// Runs a governed read outside the model's loop, as an approved proposal executes: in the current
    /// turn and agent scope, with a call of its own that the thread does not keep, marked with the step's
    /// origin.
    /// </summary>
    public static async Task<NhAssistantCallOutcome> ReadAsync(
        NhAiToolDescriptor descriptor,
        AIFunction function,
        JsonElement arguments,
        NhAssistantStepCall step,
        IServiceProvider services,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        var functionArguments = new AIFunctionArguments(values) { Services = services };
        var turn = NhAssistantExecutionScope.CurrentTurn;
        var row = new AssistantToolInvocation
        {
            Id = Guid.NewGuid(),
            ConversationId = turn?.ConversationId ?? Guid.Empty,
            TurnId = turn?.TurnId ?? Guid.Empty,
            MessageId = Guid.Empty,
            CallId = step.CallId,
            FunctionName = NhAssistantContent.Bound(function.Name, 256),
            ToolId = descriptor.Id,
            ToolVersion = descriptor.Version,
            ContractHash = NhAssistantContent.Bound(descriptor.ContractHash, 128),
            DisplayName = descriptor.Id,
            Status = NhAssistantToolCallStatuses.Running,
            ArgumentsJson = NhAssistantContent.SerializeArguments(values),
            DataClassification = descriptor.DataClassification,
            RetentionCategory = descriptor.RetentionCategory,
            StartedAt = DateTimeOffset.UtcNow
        };
        var execution = await NhAssistantToolCallInterceptor.ExecuteAsync(
            descriptor,
            row,
            null,
            null,
            async token =>
            {
                using (NhAiToolCallOrigins.Begin(step.Origin))
                {
                    return await function.InvokeAsync(functionArguments, token);
                }
            },
            cancellationToken,
            logger);
        return execution.Outcome.Kind == NhAssistantToolOutcomeKind.Succeeded
            ? new NhAssistantCallOutcome(execution.Result, NhAssistantToolStepStatus.Succeeded)
            : new NhAssistantCallOutcome(execution.Result, NhAssistantToolStepStatus.Failed)
            {
                Code = execution.Outcome.Code
            };
    }

    /// <summary>
    /// The result a write returns to the script in shadow and in a dry run: recorded, not done.
    /// </summary>
    public static JsonElement DryRunWriteResult()
    {
        return NhAiToolResult.Failed(
            NhAssistantRuleScriptCodes.DryRunWrite,
            "A run without effects makes no changes; the call was recorded with what it would become.");
    }

    public static string Format(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// What a version would write and propose on one subject without effects, in order.
/// </summary>
internal sealed class NhAssistantRuleScriptRecording
{
    private readonly List<NhAssistantRuleScriptWriteDto> _writes = [];
    private readonly List<NhAssistantRuleScriptProposalDto> _proposals = [];

    public IReadOnlyList<NhAssistantRuleScriptWriteDto> Writes => _writes;

    public IReadOnlyList<NhAssistantRuleScriptProposalDto> Proposals => _proposals;

    public void AddWrite(int call, NhAiToolDescriptor descriptor, string outcome, JsonElement input)
    {
        _writes.Add(new NhAssistantRuleScriptWriteDto(
            call,
            string.IsNullOrWhiteSpace(descriptor.ExportName) ? descriptor.Id : descriptor.ExportName,
            descriptor.Id,
            outcome,
            NhAssistantRuleScriptRuntime.Bounded(input)));
    }

    public void AddProposal(string tool, string? toolId, JsonElement input, string? rationale)
    {
        _proposals.Add(new NhAssistantRuleScriptProposalDto(tool, toolId, NhAssistantRuleScriptRuntime.Bounded(input), rationale));
    }
}

/// <summary>
/// The steps of a script without effects (RB-5), in shadow next to a run or in a dry run: a read runs
/// through its governed function in the current scope with the run's rights, a write never reaches its
/// tool and is recorded with what it would become.
/// </summary>
internal sealed class NhAssistantRuleScriptDryHost(
    IReadOnlyDictionary<string, AIFunction> functions,
    NhAssistantRuleScriptRecording recording,
    NhAssistantAgentDefinition agent,
    NhAiAgentAccountability accountability,
    bool shadowThread,
    IServiceProvider services,
    ILogger? logger) : INhAssistantStepHost
{
    public async Task<NhAssistantCallOutcome> RunAsync(
        NhAiToolDescriptor descriptor,
        JsonElement arguments,
        NhAssistantStepCall step,
        CancellationToken cancellationToken)
    {
        if (descriptor.Effect != NhAiToolEffect.ReadOnly)
        {
            recording.AddWrite(step.Index + 1, descriptor, NhAssistantRuleScriptRuntime.Would(descriptor, agent, accountability, shadowThread), arguments);
            return new NhAssistantCallOutcome(NhAssistantRuleScriptRuntime.DryRunWriteResult(), NhAssistantToolStepStatus.Failed)
            {
                Code = NhAssistantRuleScriptCodes.DryRunWrite
            };
        }
        if (!functions.TryGetValue(descriptor.Id, out var function))
        {
            return NhAssistantCallOutcome.Refused(
                NhAiToolResult.Failed(NhAssistantToolStepCodes.ToolUnavailable, "No tool by that name is available here."),
                NhAssistantToolStepCodes.ToolUnavailable);
        }

        return await NhAssistantRuleScriptRuntime.ReadAsync(descriptor, function, arguments, step, services, logger, cancellationToken);
    }

    public ValueTask CompleteAsync(NhAssistantToolStepSession session, JsonElement result, CancellationToken cancellationToken)
    {
        // A run without effects keeps its record; the reads it made are audited one by one.
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The steps of the live version in its rule turn: each call through the turn's interceptor, as a step of
/// the rule script's call, with the decision the same call gets on its own. A write runs in the agent's
/// scope as the agent's own run has it: a tool that needs approval becomes a deferred proposal, one that
/// does not runs, and an agent that only proposes, or a run in a shadow thread, proposes every write.
/// </summary>
internal sealed class NhAssistantRuleScriptTurnHost(
    NhAssistantToolCallInterceptor interceptor,
    IReadOnlyDictionary<string, AIFunction> functions,
    bool proposeWrites,
    IServiceProvider services) : INhAssistantStepHost
{
    public async Task<NhAssistantCallOutcome> RunAsync(
        NhAiToolDescriptor descriptor,
        JsonElement arguments,
        NhAssistantStepCall step,
        CancellationToken cancellationToken)
    {
        if (!functions.TryGetValue(descriptor.Id, out var function))
        {
            return NhAssistantCallOutcome.Refused(
                NhAiToolResult.Failed(NhAssistantToolStepCodes.ToolUnavailable, "No tool by that name is available here."),
                NhAssistantToolStepCodes.ToolUnavailable);
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        var functionArguments = new AIFunctionArguments(values) { Services = services };
        if (descriptor.Effect == NhAiToolEffect.ReadOnly || NhAiAgentInvocationScopes.Current is not { } scope)
        {
            return await interceptor.RunStepAsync(descriptor, function, functionArguments, step, cancellationToken);
        }

        // A rule turn requires approval for every effect; a write the version opted in to gets the decision
        // the same call gets in the agent's own run instead.
        using (NhAiAgentInvocationScopes.Begin(scope with { RequireApprovalForEffects = proposeWrites }))
        {
            return await interceptor.RunStepAsync(descriptor, function, functionArguments, step, cancellationToken);
        }
    }

    public ValueTask CompleteAsync(NhAssistantToolStepSession session, JsonElement result, CancellationToken cancellationToken)
    {
        // The rule script's call completes with the script's outcome in the rule turn.
        return ValueTask.CompletedTask;
    }
}
