using System.Text.Json;
using NewHeap.Platform.AI.Chat;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// The host of a script call: the assistant's step session of that call. In a turn every call runs
/// through the turn's interceptor as the same call of the model would; outside a turn, such as over MCP,
/// through its governed function. The session's tools are the script's tools.
/// </summary>
internal sealed class NhAiScriptStepHost : INhAiScriptHost
{
    private readonly NhAssistantToolStepSession _session;

    public NhAiScriptStepHost(NhAssistantToolStepSession session)
    {
        _session = session;
        Tools = session.Tools
            .Select(descriptor => new NhAiScriptToolOffer(NameOf(descriptor), descriptor))
            .ToArray();
    }

    public IReadOnlyList<NhAiScriptToolOffer> Tools { get; }

    public async ValueTask<NhAiScriptCallOutcome> CallAsync(
        NhAiScriptToolCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var step = await _session.RunAsync(call.Tool.Name, call.Arguments, cancellationToken);
        return OutcomeOf(step, _session.Stopped);
    }

    /// <summary>
    /// The script's view of a step: a step that waits for an inline decision stops the script; one that
    /// stopped the session for another reason, such as a limit of the turn, is a refusal that stops it too.
    /// </summary>
    internal static NhAiScriptCallOutcome OutcomeOf(NhAssistantToolStep step, bool sessionStopped)
    {
        var result = step.Result;
        return step.Status switch
        {
            NhAssistantToolStepStatus.AwaitingApproval => NhAiScriptCallOutcome.AwaitingApproval(result, step.ProposalId),
            NhAssistantToolStepStatus.Refused => NhAiScriptCallOutcome.Refused(WithCode(result, step.Code)),
            _ when sessionStopped => NhAiScriptCallOutcome.Refused(WithCode(result, step.Code)),
            NhAssistantToolStepStatus.Proposed => NhAiScriptCallOutcome.Proposed(result, step.ProposalId),
            NhAssistantToolStepStatus.NotNeeded => NhAiScriptCallOutcome.NotNeeded(result),
            _ => NhAiScriptCallOutcome.Completed(result)
        };
    }

    private static string NameOf(NhAiToolDescriptor descriptor)
    {
        return string.IsNullOrWhiteSpace(descriptor.ExportName) ? descriptor.Id : descriptor.ExportName;
    }

    /// <summary>
    /// The step's result with the step's code, so the script names why the turn refused it.
    /// </summary>
    private static JsonElement WithCode(JsonElement result, string? code)
    {
        if (code is null
            || !code.All(character => character == '-' || char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            || (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty(NhAiToolResult.CodePropertyName, out var existing)
                && existing.ValueKind == JsonValueKind.String))
        {
            return result;
        }

        return NhAiToolResult.Failed(code, null);
    }
}
