using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Where a run's proposal waits: in the run's own thread, at another object (K11), or nowhere
/// because the object refused it.
/// </summary>
internal sealed record NhAssistantPlacementDecision
{
    private NhAssistantPlacementDecision()
    {
    }

    /// <summary>The proposal waits in the run's own thread, as before.</summary>
    public static NhAssistantPlacementDecision Own { get; } = new();

    /// <summary>The object the call names, when it names another one than the run's own.</summary>
    public NhAiSubjectInfo? Target { get; private init; }

    /// <summary>Why the proposal is not placed: one of <see cref="NhAssistantPlacementReasons"/>.</summary>
    public string? RefusalReason { get; private init; }

    /// <summary>The object the call named, when the refusal concerns a valid reference.</summary>
    public NhAiSubjectRef? Named { get; private init; }

    /// <summary>The serializer of the tool's input, to confirm the object the invoker evaluated.</summary>
    internal JsonSerializerOptions? InputOptions { get; private init; }

    public bool Refused => RefusalReason is not null;

    public static NhAssistantPlacementDecision Placed(NhAiSubjectInfo target, JsonSerializerOptions inputOptions)
    {
        return new NhAssistantPlacementDecision { Target = target, InputOptions = inputOptions };
    }

    public static NhAssistantPlacementDecision Refuse(string reason, NhAiSubjectRef? named)
    {
        return new NhAssistantPlacementDecision { RefusalReason = reason, Named = named };
    }
}

/// <summary>
/// Places a run's proposal for another object than its own in the agent's thread at that object
/// (K11, K12). The tool declares which argument names which object type
/// (<see cref="NhAiToolDescriptor.Subject"/>); the platform reads the object from the arguments as the
/// tool binds them and checks it before the call runs: the agent works on its type and, per division,
/// in its division; it exists; the account the run answers to reads it; and the run has placed fewer
/// than <see cref="NhAssistantLimits.MaxPlacedProposalsPerRun"/> proposals. Tools without a declared
/// subject, MCP tools included, propose in the run's own thread.
/// </summary>
internal sealed class NhAssistantProposalPlacement(
    NhAssistantSubjectAccessService subjects,
    NhAssistantThreadService threads,
    NhAssistantRunStore runs,
    NhAssistantRegistrationState registration)
{
    private const string InputPropertyName = "input";

    /// <summary>
    /// Whether a call of <paramref name="descriptor"/> in this turn can concern another object: a run
    /// of an agent (not a person's turn in the thread and not a consultation), a tool that changes
    /// something and declares its subject, and never an MCP tool.
    /// </summary>
    public static bool Applies(
        NhAssistantAgentTurnContext? agentTurn,
        bool consultation,
        NhAiToolDescriptor descriptor)
    {
        return agentTurn is { RunId: not null, IsRequestTurn: false }
            && !consultation
            && descriptor.Subject is not null
            && descriptor.Effect != NhAiToolEffect.ReadOnly
            && !descriptor.Id.StartsWith("mcp.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Decides where the proposal of this call waits, before the call runs. A call whose arguments
    /// cannot be read as the tool's input, or that names no object, stays in the run's own thread;
    /// its own binding then decides.
    /// </summary>
    public async Task<NhAssistantPlacementDecision> DecideAsync(
        NhAssistantAgentTurnContext agentTurn,
        NhAiToolDescriptor descriptor,
        AIFunction function,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentTurn);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(function);
        var declared = descriptor.Subject!;
        var options = function.JsonSerializerOptions ?? AIJsonUtilities.DefaultOptions;
        if (ReadNamedId(descriptor, options, arguments, HasInputEnvelope(function.JsonSchema)) is not { } namedId)
        {
            return NhAssistantPlacementDecision.Own;
        }
        if (!IsValidId(namedId))
        {
            return NhAssistantPlacementDecision.Refuse(NhAssistantPlacementReasons.NotFound, null);
        }

        var thread = agentTurn.Thread;
        var target = new NhAiSubjectRef(declared.Type, namedId);
        if (string.Equals(target.Type, thread.SubjectType, StringComparison.Ordinal)
            && string.Equals(target.Id, thread.SubjectId, StringComparison.Ordinal))
        {
            return NhAssistantPlacementDecision.Own;
        }

        var worksOn = agentTurn.Options.WorksOn;
        if (!worksOn.Covers(target.Type))
        {
            return NhAssistantPlacementDecision.Refuse(NhAssistantPlacementReasons.DoesNotWorkOn, target);
        }

        var info = await subjects.DescribeAsync(target, cancellationToken);
        // An object of another tenant is unknown to a run bound to a tenant.
        if (info is null
            || (thread.TenantId is not null && !string.Equals(info.TenantId, thread.TenantId, StringComparison.Ordinal)))
        {
            return NhAssistantPlacementDecision.Refuse(NhAssistantPlacementReasons.NotFound, target);
        }

        // An app-wide agent places in any scope; a per-division agent only in its own division.
        if (worksOn.PerDivision && (thread.DivisionId is null || info.DivisionId != thread.DivisionId))
        {
            return NhAssistantPlacementDecision.Refuse(NhAssistantPlacementReasons.DoesNotWorkOn, target);
        }

        // The account the run answers to, the agent's own or the accountable person, reads the object.
        var access = await subjects.ResolveAsync(target, agentTurn.AccountableOwnerId, cancellationToken);
        if (access.Role < NhAiSubjectRole.Reader)
        {
            return NhAssistantPlacementDecision.Refuse(NhAssistantPlacementReasons.NoAccess, target);
        }

        var limit = registration.Limits.MaxPlacedProposalsPerRun;
        if (limit == 0 || await runs.CountPlacedProposalsAsync(agentTurn.RunId!.Value, cancellationToken) >= limit)
        {
            return NhAssistantPlacementDecision.Refuse(NhAssistantPlacementReasons.Limit, target);
        }

        return NhAssistantPlacementDecision.Placed(info, options);
    }

    /// <summary>
    /// Whether the input the shared invoker evaluated names the object the placement checked. The
    /// invoker binds the same arguments, so they agree unless a binder changed the value.
    /// </summary>
    public static bool Confirms(NhAssistantPlacementDecision decision, NhAiToolDescriptor descriptor, object? evaluatedInput)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (decision.Target is not { } target || evaluatedInput is null || decision.InputOptions is null || descriptor.Subject is null)
        {
            return false;
        }

        try
        {
            var element = evaluatedInput is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(evaluatedInput, evaluatedInput.GetType(), decision.InputOptions);
            return ReadProperty(element, descriptor.Subject.ArgumentName) is { } id
                && string.Equals(id, target.Subject.Id, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The agent's thread at the object as the checks before a placed proposal is recorded see it: the
    /// existing thread, or a description of the one <see cref="ThreadAtAsync"/> creates, which is not
    /// stored yet. A proposal that a later check refuses, such as a duplicate, then leaves no empty
    /// thread at the object. Fails when nobody answers for the agent at the object.
    /// </summary>
    public async Task<TaskResult<AssistantThread>> PrepareThreadAtAsync(
        NhAssistantAgent agent,
        NhAiSubjectInfo target,
        bool shadow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(target);
        if (await runs.FindThreadAsync(target.ScopeKey, agent.Id, target.Subject, cancellationToken, shadow) is { } existing)
        {
            return TaskResult<AssistantThread>.Succeeded(existing);
        }

        var accountable = threads.ResolveAccountableOwner(agent, target);
        if (!accountable.Success)
        {
            return TaskResult<AssistantThread>.Failed(accountable);
        }

        return TaskResult<AssistantThread>.Succeeded(new AssistantThread
        {
            ScopeKey = target.ScopeKey,
            AgentId = agent.Id,
            SubjectType = target.Subject.Type,
            SubjectId = target.Subject.Id,
            SubjectDisplayName = target.DisplayName,
            DivisionId = target.DivisionId,
            TenantId = target.TenantId,
            SubjectVersion = target.Version,
            AccountableOwnerId = accountable.Data!,
            Shadow = shadow
        });
    }

    /// <summary>
    /// The agent's thread at the object, created when needed; a run in shadow places in the agent's
    /// shadow thread there.
    /// </summary>
    public Task<TaskResult<AssistantThread>> ThreadAtAsync(
        NhAssistantAgent agent,
        NhAiSubjectInfo target,
        bool shadow,
        CancellationToken cancellationToken)
    {
        return threads.GetOrCreateAsync(agent, target, cancellationToken, shadow);
    }

    /// <summary>
    /// Reads the id of the object the call names, as the tool binds its input: the input is
    /// deserialized into the tool's input type with the function's serializer and serialized again,
    /// so a Guid or a number reads as the tool sees it. <see langword="null"/> when the arguments do
    /// not bind or name no object.
    /// </summary>
    private static string? ReadNamedId(
        NhAiToolDescriptor descriptor,
        JsonSerializerOptions options,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        bool enveloped)
    {
        if (arguments is null)
        {
            return null;
        }

        try
        {
            if (InputOf(arguments, enveloped) is not { ValueKind: JsonValueKind.Object } input)
            {
                return null;
            }

            var bound = JsonSerializer.Deserialize(input, descriptor.InputType, options);
            if (bound is null)
            {
                return null;
            }

            var canonical = JsonSerializer.SerializeToElement(bound, descriptor.InputType, options);
            return ReadProperty(canonical, descriptor.Subject!.ArgumentName);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The input object of the call: the <c>input</c> envelope when the function takes one and the
    /// caller used it, otherwise the arguments themselves, as the governed function normalizes them.
    /// </summary>
    private static JsonElement? InputOf(IEnumerable<KeyValuePair<string, object?>> arguments, bool enveloped)
    {
        var values = arguments.ToArray();
        if (enveloped
            && values.Length == 1
            && string.Equals(values[0].Key, InputPropertyName, StringComparison.Ordinal))
        {
            return ToElement(values[0].Value);
        }
        if (enveloped && values.Any(value => string.Equals(value.Key, InputPropertyName, StringComparison.Ordinal)))
        {
            // The envelope next to other properties does not bind; the function refuses it.
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values)
            {
                if (ToElement(value) is { } element)
                {
                    writer.WritePropertyName(name);
                    element.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement? ToElement(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(value, value.GetType(), AIJsonUtilities.DefaultOptions)
        };
    }

    private static bool HasInputEnvelope(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = properties.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == 1 && string.Equals(names[0], InputPropertyName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The id in <paramref name="name"/>: a string as written, an integer in invariant digits;
    /// <see langword="null"/> when it is missing, null or of another kind.
    /// </summary>
    private static string? ReadProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement? found = element.TryGetProperty(name, out var exact) ? exact : null;
        if (found is null)
        {
            var matches = element.EnumerateObject()
                .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            found = matches.Length == 1 ? matches[0].Value : null;
        }

        return found switch
        {
            { ValueKind: JsonValueKind.String } text => text.GetString(),
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var signed) => signed.ToString(CultureInfo.InvariantCulture),
            { ValueKind: JsonValueKind.Number } number when number.TryGetUInt64(out var unsigned) => unsigned.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static bool IsValidId(string id)
    {
        return !string.IsNullOrWhiteSpace(id) && id.Length <= NhAiSubjectRef.MaxIdLength && id.Trim() == id;
    }
}
