using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.RuleScripts;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Rule scripts in rule turns (RB-1, RB-4, RB-6): the live version of an agent's or skill's rule script
/// runs instead of its code rule, without a model, and calls the tools the run may use through the step
/// runtime with the run's grants and accountability; its end is the rule result of today. A candidate in
/// shadow runs next to the live rules without effects and records what it would do.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    /// <summary>
    /// The rule scripts in effect for the agent's own rules (<paramref name="skillId"/>
    /// <see langword="null"/>) or for one skill: nothing unless the ceiling lets the agent have them, so an
    /// application without <c>UseRuleScriptsAsData</c> reads nothing.
    /// </summary>
    private async Task<NhAssistantRuleScriptsInEffect> RuleScriptsOfAsync(
        NhAssistantTurnState state,
        string? skillId,
        CancellationToken cancellationToken)
    {
        if (registration.RuleScriptsAsData is null || state.AgentTurn is not { } agentTurn || agentTurn.IsRequestTurn)
        {
            return NhAssistantRuleScriptsInEffect.None;
        }

        var ceiling = NhAssistantRuleScriptRuntime.CeilingOf(registration, agentTurn.Thread.AgentId, state.EffectiveAgent.Source);
        if (ceiling is null)
        {
            return NhAssistantRuleScriptsInEffect.None;
        }

        var (live, shadow) = await services.GetRequiredService<NhAssistantRuleScriptStore>()
            .FindInEffectAsync(agentTurn.Thread.AgentId, skillId ?? string.Empty, cancellationToken);
        return new NhAssistantRuleScriptsInEffect(live, shadow, ceiling);
    }

    /// <summary>
    /// Runs the live <paramref name="version"/> as the rules of the turn. The script's call is a call of
    /// the turn, with the version's number and hash, under which every tool call runs as a step with the
    /// decision it gets on its own. Returns the rule result of its end, or the script's code when it stopped.
    /// </summary>
    private async Task<(NhAssistantRuleResult? Result, string? Code)> RunLiveRuleScriptAsync(
        NhAssistantTurnState state,
        IReadOnlyList<NhAssistantAgentTurnInput> inputs,
        NhAssistantToolCallInterceptor interceptor,
        IReadOnlyDictionary<string, AIFunction> tools,
        AssistantRuleScriptVersion version,
        NhAssistantRuleScriptAgentCeiling ceiling,
        CancellationToken cancellationToken)
    {
        var agentTurn = state.AgentTurn!;
        var engine = services.GetService<INhAssistantRuleScriptEngine>();
        if (engine is null)
        {
            logger.LogWarning(
                "Agent {AgentId} has a live rule script, but no rule script engine is registered (AddNewHeapPlatformAIScripting).",
                agentTurn.Thread.AgentId);
            return (null, NhAssistantErrorCodes.TurnFailed);
        }

        var descriptor = NhAssistantRuleScriptTool.Descriptor;
        var row = await interceptor.BeginToolCallAsync(
            descriptor,
            "rule-script-" + Guid.NewGuid().ToString("N"),
            NhAssistantRuleScriptTool.FunctionName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = version.Version,
                ["hash"] = version.ScriptHash
            },
            "Rule script, version " + version.Version.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        var origin = new NhAiToolCallOrigin(NhAiToolCallOriginKind.Script, NhAssistantRuleScriptTool.ToolId, version.ScriptHash)
        {
            ParentInvocationId = row.Id,
            RuleScriptVersion = version.Version
        };
        var writes = NhAssistantRuleScriptRuntime.WritesInEffect(version, ceiling);
        var scriptTools = NhAssistantRuleScriptRuntime.ToolsOf(tools);
        var host = new NhAssistantRuleScriptTurnHost(
            interceptor,
            tools,
            // The agent's own run proposes every effect of an agent that only proposes, and of a shadow thread.
            proposeWrites: !state.Scope.Agent.CanMutate || IsShadowRunTurn(agentTurn),
            services);
        var session = StepsOf(version, writes, ceiling, scriptTools, origin, host);
        var run = new NhAssistantRuleScriptRun((tool, input, rationale, token) =>
            ProposeFromScriptAsync(state, interceptor, tools, origin, tool, input, rationale, token))
        {
            AgentId = agentTurn.Thread.AgentId,
            SkillId = version.SkillId.Length == 0 ? null : version.SkillId,
            Version = version.Version,
            Script = version.Script,
            Mode = NhAssistantRuleScriptMode.Live,
            Subject = agentTurn.Subject,
            TriggerId = agentTurn.TriggerId,
            RunId = agentTurn.RunId,
            Now = _applicationClock.GetUtcNow(),
            Inputs = RuleInputsOf(inputs),
            AlertKinds = agentTurn.Options.AlertKinds,
            Tools = scriptTools,
            Writes = writes,
            Destructive = ceiling.Destructive.Where(writes.Contains).ToHashSet(StringComparer.Ordinal),
            MaxWrites = writes.Count == 0 ? 0 : ceiling.MaxWritesPerRun,
            Steps = session
        };

        NhAssistantRuleScriptOutcome outcome;
        try
        {
            outcome = await engine.RunAsync(run, cancellationToken);
        }
        catch (Exception)
        {
            // The script's call ends also when the run is cancelled or ends unexpectedly.
            await CompleteRuleScriptCallAsync(interceptor, row, NhAiToolResult.Failed(NhAiToolFailureCodes.Failed, null), NhAiToolFailureCodes.Failed);
            throw;
        }

        await CompleteRuleScriptCallAsync(interceptor, row, outcome.Result, outcome.Completed ? null : outcome.Code);
        logger.LogInformation(
            "Rule script version {Version} of agent {AgentId} {Outcome} in turn {TurnId} after {Calls} tool calls.",
            version.Version,
            agentTurn.Thread.AgentId,
            outcome.Completed ? "completed" : "stopped with " + outcome.Code,
            state.Scope.TurnId,
            outcome.Calls.Count);
        return outcome.Completed
            ? (NhAssistantRuleScriptRuntime.ToRuleResult(outcome), null)
            : (null, outcome.Code ?? NhAssistantErrorCodes.TurnFailed);
    }

    /// <summary>
    /// Runs the candidate in shadow next to the live rules of the same run, on the same subject and inputs,
    /// without effects: its reads run with the run's rights, its writes, proposals and alerts are recorded
    /// as the version's shadow run and never happen. A failure of the shadow never changes the live run.
    /// </summary>
    private async Task RunShadowRuleScriptAsync(
        NhAssistantTurnState state,
        IReadOnlyList<NhAssistantAgentTurnInput> inputs,
        IReadOnlyDictionary<string, AIFunction> tools,
        AssistantRuleScriptVersion version,
        NhAssistantRuleScriptAgentCeiling ceiling,
        CancellationToken cancellationToken)
    {
        var agentTurn = state.AgentTurn!;
        if (services.GetService<INhAssistantRuleScriptEngine>() is not { } engine || agentTurn.Thread.Shadow)
        {
            // A run in a shadow thread is already an evaluation; its candidate runs in the live thread's runs.
            return;
        }

        try
        {
            var recording = new NhAssistantRuleScriptRecording();
            var writes = NhAssistantRuleScriptRuntime.WritesInEffect(version, ceiling);
            var scriptTools = NhAssistantRuleScriptRuntime.ToolsOf(tools);
            var origin = new NhAiToolCallOrigin(NhAiToolCallOriginKind.Script, NhAssistantRuleScriptTool.ToolId, version.ScriptHash)
            {
                ParentInvocationId = Guid.NewGuid(),
                RuleScriptVersion = version.Version
            };
            var host = new NhAssistantRuleScriptDryHost(tools, recording, state.Scope.Agent, agentTurn.Options.Accountability, IsShadowRunTurn(agentTurn), services, logger);
            var run = new NhAssistantRuleScriptRun((tool, input, rationale, _) =>
            {
                recording.AddProposal(tool, ToolIdOf(tools, tool), input, rationale);
                return Task.FromResult(NhAssistantRuleProposal.Refused(NhAssistantRuleScriptCodes.DryRunWrite));
            })
            {
                AgentId = agentTurn.Thread.AgentId,
                SkillId = version.SkillId.Length == 0 ? null : version.SkillId,
                Version = version.Version,
                Script = version.Script,
                Mode = NhAssistantRuleScriptMode.Shadow,
                Subject = agentTurn.Subject,
                TriggerId = agentTurn.TriggerId,
                RunId = agentTurn.RunId,
                Now = _applicationClock.GetUtcNow(),
                Inputs = RuleInputsOf(inputs),
                AlertKinds = agentTurn.Options.AlertKinds,
                Tools = scriptTools,
                Writes = writes,
                Destructive = ceiling.Destructive.Where(writes.Contains).ToHashSet(StringComparer.Ordinal),
                MaxWrites = writes.Count == 0 ? 0 : ceiling.MaxWritesPerRun,
                Steps = StepsOf(version, writes, ceiling, scriptTools, origin, host)
            };

            var outcome = await engine.RunAsync(run, cancellationToken);
            var record = NhAssistantRuleScriptRuntime.RecordOf(agentTurn.Subject, outcome, recording);
            await services.GetRequiredService<NhAssistantRuleScriptStore>().AddRunRecordAsync(
                new AssistantRuleScriptRunRecord
                {
                    Id = Guid.NewGuid(),
                    VersionId = version.Id,
                    AgentId = version.AgentId,
                    SkillId = version.SkillId,
                    Version = version.Version,
                    Kind = NhAssistantRuleScriptRunKinds.Shadow,
                    RunId = agentTurn.RunId,
                    RanBy = state.Scope.Agent.ActorId,
                    RanAt = _applicationClock.GetUtcNow(),
                    SubjectCount = 1,
                    CompletedCount = outcome.Completed ? 1 : 0,
                    RecordJson = NhAssistantRuleScriptRuntime.Serialize([record])
                },
                NhAssistantRuleScriptLimits.MaxShadowRunsKept,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // A script can carry content in exception messages; log the type only.
            logger.LogWarning(
                "The shadow run of rule script version {Version} of agent {AgentId} failed with {ExceptionType}.",
                version.Version,
                agentTurn.Thread.AgentId,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Runs a candidate in shadow for an agent or skill without rules of its own, before its model works:
    /// in the run's turn and agent scope with the tools its rules would use.
    /// </summary>
    private async Task RunShadowOnlyRuleScriptAsync(
        NhAssistantTurnState state,
        NhAiInvocationContext callerContext,
        IReadOnlyList<NhAssistantAgentTurnInput> inputs,
        AssistantRuleScriptVersion version,
        NhAssistantRuleScriptAgentCeiling ceiling,
        CancellationToken cancellationToken)
    {
        using var turnScope = NhAssistantExecutionScope.EnterTurn(state.Scope);
        using var agentScope = BeginAgentScope(state);
        var tools = await RuleFunctionsAsync(state, callerContext, cancellationToken);
        await RunShadowRuleScriptAsync(state, inputs, tools, version, ceiling, cancellationToken);
    }

    /// <summary>
    /// The step session of one run of a script: every read of the run's tools and the writes the version
    /// may call, at most the ceiling's writes per run, marked with the version's origin.
    /// </summary>
    private static NhAssistantToolStepSession StepsOf(
        AssistantRuleScriptVersion version,
        IReadOnlySet<string> writes,
        NhAssistantRuleScriptAgentCeiling ceiling,
        IReadOnlyList<NhAiToolDescriptor> scriptTools,
        NhAiToolCallOrigin origin,
        INhAssistantStepHost host)
    {
        var request = new NhAssistantToolStepRequest(NhAssistantRuleScriptTool.Descriptor, NhAiToolCallOriginKind.Script)
        {
            Hash = version.ScriptHash,
            MaxSteps = NhAssistantToolStepLimits.MaxSteps,
            MaxMutationSteps = writes.Count == 0 ? 0 : Math.Min(ceiling.MaxWritesPerRun, NhAssistantToolStepLimits.MaxSteps)
        };
        var callable = scriptTools
            .Where(tool => tool.Effect == NhAiToolEffect.ReadOnly || writes.Contains(tool.Id))
            .ToArray();
        return new NhAssistantToolStepSession(request, origin, callable, host);
    }

    /// <summary>
    /// <c>propose()</c> of a live script: the rules' path of <see cref="NhAssistantRuleContext.ProposeAsync{TInput}"/>
    /// with the version's origin on the proposal, its note for the model's briefing and its rationale for
    /// the thread.
    /// </summary>
    private static async Task<NhAssistantRuleProposal> ProposeFromScriptAsync(
        NhAssistantTurnState state,
        NhAssistantToolCallInterceptor interceptor,
        IReadOnlyDictionary<string, AIFunction> tools,
        NhAiToolCallOrigin origin,
        string tool,
        JsonElement input,
        string? rationale,
        CancellationToken cancellationToken)
    {
        if (ToolIdOf(tools, tool) is not { } toolId)
        {
            return NhAssistantRuleProposal.Refused(NhAssistantErrorCodes.ProposalRefused);
        }

        var why = string.IsNullOrWhiteSpace(rationale) ? null : rationale.Trim();
        Func<CultureInfo, string?>? writer = why is null ? null : _ => why;
        NhAssistantRuleProposal proposed;
        using (NhAiToolCallOrigins.Begin(origin))
        {
            proposed = await ProposeForRulesAsync(interceptor, tools, toolId, input, writer, cancellationToken);
        }

        state.RuleProposals.Add(new NhAssistantRuleProposalNote(toolId, proposed));
        if (proposed.Outcome == NhAssistantProposeOutcome.Created && why is not null)
        {
            // The thread keeps why, so the model can answer a person who asks about it.
            state.RuleRationales.Add(NhAssistantContent.Bound(why.ReplaceLineEndings(" "), NhAssistantLimits.MaxStaleExplanationCharacters));
            state.RuleRationaleWriters.Add(writer!);
        }

        return proposed;
    }

    /// <summary>The id of the tool a script names by function name or id; <see langword="null"/> for another.</summary>
    private static string? ToolIdOf(IReadOnlyDictionary<string, AIFunction> tools, string name)
    {
        if (tools.ContainsKey(name))
        {
            return name;
        }

        return tools.Values
            .OfType<INhAiGovernedAIFunction>()
            .FirstOrDefault(function => string.Equals(function.Descriptor.ExportName, name, StringComparison.Ordinal))
            ?.Descriptor.Id;
    }

    private static NhAssistantRuleInput[] RuleInputsOf(IReadOnlyList<NhAssistantAgentTurnInput> inputs)
    {
        return inputs
            .Select(input => new NhAssistantRuleInput(input.TriggerId, input.Text, input.PayloadJson, input.CreatedAt))
            .ToArray();
    }

    /// <summary>
    /// Ends the rule script's call in the turn with what the script did.
    /// </summary>
    private static async Task CompleteRuleScriptCallAsync(
        NhAssistantToolCallInterceptor interceptor,
        AssistantToolInvocation row,
        JsonElement result,
        string? code)
    {
        var call = new NhAssistantToolCallScope
        {
            InvocationId = row.Id,
            Descriptor = NhAssistantRuleScriptTool.Descriptor,
            IdempotencyKey = "assistant-" + row.Id.ToString("N"),
            Row = row
        };
        var outcome = code is null
            ? new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Succeeded, "succeeded")
            : new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Failed, code);
        await interceptor.CompleteToolCallAsync(row, new NhAssistantToolExecution(result, outcome, call), CancellationToken.None);
    }
}
