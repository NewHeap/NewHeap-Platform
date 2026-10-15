using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Hand-offs from a run to other agents: the generated tools a run's model may use and the rule
/// context's <c>HandOffAsync</c>. The agent-run host checks and records them.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    private const int MaxHandOffExportNameLength = 64;

    /// <summary>
    /// Hands work from the turn's run to <paramref name="targetAgentId"/>; outside a run, such as in
    /// a person's chat, nothing is handed off.
    /// </summary>
    private Task<NhAssistantHandOffReceipt> HandOffAsync(
        NhAssistantTurnState state,
        string targetAgentId,
        NhAiSubjectRef subject,
        string message,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        if (!IsRun(state) || services.GetService<INhAssistantHandOffs>() is not { } handOffs)
        {
            return Task.FromResult(NhAssistantHandOffReceipt.Refused(NhAssistantErrorCodes.HandOffUnavailable));
        }

        return handOffs.HandOffAsync(
            new NhAssistantHandOffRequest(state.AgentTurn!, state.Skill?.Id, targetAgentId, subject, message, payloadJson),
            cancellationToken);
    }

    /// <summary>
    /// The hand-off tools of a run: one per agent that accepts hand-offs from this agent and that the
    /// agent's or its skill's selectors select. People and consultations never get them.
    /// </summary>
    private IEnumerable<AIFunction> CreateHandOffFunctions(NhAssistantTurnState state)
    {
        if (!IsRun(state))
        {
            yield break;
        }

        var agentId = state.AgentTurn!.Thread.AgentId;
        var selectors = state.Scope.Agent.ToolSelectors;
        foreach (var handOff in registration.AgentRuns.Values.SelectMany(options => options.HandOffs))
        {
            if (handOff.Sources.Contains(agentId, StringComparer.Ordinal)
                && selectors.Any(selector => IsSelected(selector, handOff.ToolId)))
            {
                yield return CreateHandOffFunction(state, handOff);
            }
        }
    }

    private NhAssistantMemoryFunction CreateHandOffFunction(NhAssistantTurnState state, NhAssistantHandOffOptions handOff)
    {
        var runSubject = state.AgentTurn!.Subject.Subject;
        var ownSubject = string.Equals(runSubject.Type, handOff.SubjectType, StringComparison.Ordinal);
        var description = (handOff.Description is null ? string.Empty : handOff.Description + " ")
            + $"Hands work on one {handOff.SubjectType} to the agent '{handOff.AgentId}', which works on it in a run "
            + "of its own; its changes wait for approval there. Use it when that agent's work is needed, "
            + "not to ask questions.";
        var schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                subjectId = new
                {
                    type = "string",
                    maxLength = NhAiSubjectRef.MaxIdLength,
                    description = ownSubject
                        ? $"The id of the {handOff.SubjectType}; leave it out for the subject of this run."
                        : $"The id of the {handOff.SubjectType}, exactly as the application shows it."
                },
                message = new
                {
                    type = "string",
                    maxLength = NhAssistantHandOffTools.MaxMessageChars,
                    description = "What the agent should look at and why, in a few sentences."
                }
            },
            required = ownSubject ? new[] { "message" } : new[] { "subjectId", "message" }
        });
        return new NhAssistantMemoryFunction(
            HandOffDescriptor(handOff),
            description,
            schema,
            async (arguments, cancellationToken) =>
            {
                var subjectId = Text(arguments, "subjectId")?.Trim();
                var message = Text(arguments, "message")?.Trim();
                if (string.IsNullOrEmpty(subjectId) && ownSubject)
                {
                    subjectId = runSubject.Id;
                }
                if (string.IsNullOrEmpty(message)
                    || message.Length > NhAssistantHandOffTools.MaxMessageChars
                    || string.IsNullOrEmpty(subjectId)
                    || subjectId.Length > NhAiSubjectRef.MaxIdLength)
                {
                    return HandOffResult(
                        NhAssistantHandOffReceipt.Refused(NhAiToolFailureCodes.InputInvalid),
                        "Give the subject id and what the agent should work on.");
                }

                var receipt = await HandOffAsync(
                    state,
                    handOff.AgentId,
                    new NhAiSubjectRef(handOff.SubjectType, subjectId),
                    message,
                    null,
                    cancellationToken);
                return HandOffResult(receipt, receipt.Outcome switch
                {
                    NhAssistantHandOffOutcomes.Accepted => "The agent took the work; it answers in its own thread.",
                    NhAssistantHandOffOutcomes.Waiting => "The agent is paused; the work waits until it resumes.",
                    NhAssistantHandOffOutcomes.Deduplicated => "This run already handed the subject to the agent.",
                    _ => "The agent did not take the work."
                });
            });
    }

    private static JsonElement HandOffResult(NhAssistantHandOffReceipt receipt, string message)
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["success"] = receipt.Outcome != NhAssistantHandOffOutcomes.Refused,
            ["outcome"] = receipt.Outcome,
            ["runId"] = receipt.RunId,
            ["code"] = receipt.Code,
            ["message"] = message
        });
    }

    private static NhAiToolDescriptor HandOffDescriptor(NhAssistantHandOffOptions handOff)
    {
        const NhAiToolEffect effect = NhAiToolEffect.ReadOnly;
        return new NhAiToolDescriptor(
            handOff.ToolId,
            1,
            $"Hands work on a {handOff.SubjectType} to agent {handOff.AgentId}.",
            typeof(JsonElement),
            typeof(JsonElement),
            effect,
            NhAiToolExposure.Agent,
            false,
            [])
        {
            ExportName = HandOffExportName(handOff),
            CatalogId = "assistant",
            ContractHash = NhAiCanonicalJson.ComputeHash(handOff.ToolId + "@1:" + effect),
            Approval = NhAiApprovalRequirement.NotRequired,
            DataClassification = NhAiDataClassification.Internal,
            RetentionCategory = NhAiRetentionCategory.ConversationContent
        };
    }

    /// <summary>
    /// <c>handoff_{agent}_{subjectType}</c> within the 64 characters model providers accept, with a
    /// stable hash suffix when it has to be shortened.
    /// </summary>
    private static string HandOffExportName(NhAssistantHandOffOptions handOff)
    {
        var name = "handoff_" + handOff.AgentId + "_" + handOff.SubjectType;
        if (name.Length <= MaxHandOffExportNameLength)
        {
            return name;
        }

        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12];
        return name[..(MaxHandOffExportNameLength - suffix.Length - 1)].TrimEnd('_', '-') + "_" + suffix;
    }
}
