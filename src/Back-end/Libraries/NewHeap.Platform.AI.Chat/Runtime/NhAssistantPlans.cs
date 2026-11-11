using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// The plan an agent keeps with <c>update_plan</c> (<c>run.KeepPlan()</c>): the function's description
/// and schema, the checks of an update, the stored part and view of a plan and the results the model
/// reads. A plan is the agent's own statement about its work in one turn, model output that people see
/// as the agent's: plain text within <see cref="NhAssistantPlanLimits"/>, never markup, never an
/// instruction to people and never replayed to a later turn. It lives in the turn's message
/// (<see cref="NhAssistantStoredPart.PlanType"/>) and in the results of its calls, which a turn that
/// waited and continued reads back; it needs no storage of its own.
/// </summary>
internal static class NhAssistantPlans
{
    /// <summary>
    /// The code of an update that changed the plan.
    /// </summary>
    public const string UpdatedCode = "plan-updated";

    /// <summary>
    /// The status the model names a step in progress with, as the function's schema offers it.
    /// </summary>
    private const string ModelInProgress = "in_progress";

    /// <summary>
    /// What the model reads about <c>update_plan</c>.
    /// </summary>
    public static string Description { get; } =
        "Keeps a short, visible checklist of your work in this turn, which people see as your plan while "
        + "you work. Use it for work of several steps, not for a simple answer: set the whole list with "
        + "`steps` (1 to " + NhAssistantPlanLimits.MaxSteps.ToString(CultureInfo.InvariantCulture)
        + " steps of at most " + NhAssistantPlanLimits.MaxStepLength.ToString(CultureInfo.InvariantCulture)
        + " characters, plain text, at most one in_progress), or pass `advance: true` to complete the step "
        + "in progress and start the next pending one. Keep the plan true to what you do; update it at most "
        + NhAssistantPlanLimits.MaxUpdatesPerTurn.ToString(CultureInfo.InvariantCulture)
        + " times. It describes your own work and asks nothing of anyone.";

    /// <summary>
    /// The JSON schema of <c>update_plan</c>: a whole list of steps or <c>advance</c>.
    /// </summary>
    public static string Schema { get; } = JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["steps"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = NhAssistantPlanLimits.MaxSteps,
                ["description"] = "The whole plan, in order; replaces the plan.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["text"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "The step as a short plain-text phrase."
                        },
                        ["status"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = new[] { "pending", ModelInProgress, "completed" }
                        }
                    },
                    ["required"] = new[] { "text", "status" }
                }
            },
            ["advance"] = new Dictionary<string, object>
            {
                ["type"] = "boolean",
                ["description"] = "Completes the step in progress and starts the next pending step; leave out steps."
            }
        }
    });

    /// <summary>
    /// Whether <paramref name="toolId"/> is <c>update_plan</c>, whose calls the conversation shows as
    /// the plan instead of as tool calls.
    /// </summary>
    public static bool IsPlanCall(string toolId)
    {
        return string.Equals(toolId, NhAssistantMemoryFunction.PlanDescriptor.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan after an <c>update_plan</c> call of a turn that updated its plan
    /// <paramref name="updates"/> times so far, or the result the model reads when the call changes
    /// nothing: <see cref="NhAssistantErrorCodes.PlanUpdateLimitReached"/> once the turn used its
    /// updates, <see cref="NhAssistantErrorCodes.PlanInvalid"/> for an update <see cref="Apply"/> refuses.
    /// </summary>
    public static (IReadOnlyList<NhAssistantPlanStep>? Plan, JsonElement? Refusal) Update(
        AIFunctionArguments arguments,
        IReadOnlyList<NhAssistantPlanStep>? current,
        int updates)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (updates >= NhAssistantPlanLimits.MaxUpdatesPerTurn)
        {
            return (null, Refused(
                NhAssistantErrorCodes.PlanUpdateLimitReached,
                "The plan was not changed: this turn updated it as often as it may. Continue the work without updating it."));
        }

        var (plan, refusal) = Apply(arguments, current);
        return plan is null
            ? (null, Refused(NhAssistantErrorCodes.PlanInvalid, refusal!))
            : (plan, null);
    }

    /// <summary>
    /// Checks an <c>update_plan</c> call against the plan of the turn so far
    /// (<paramref name="current"/>, <see langword="null"/> before the first update): either a whole
    /// list of steps within the bounds with at most one step in progress, or <c>advance</c> on a plan
    /// that has a step to complete or start. Returns the new plan, or why it was refused for the model.
    /// </summary>
    public static (IReadOnlyList<NhAssistantPlanStep>? Plan, string? Refusal) Apply(
        AIFunctionArguments arguments,
        IReadOnlyList<NhAssistantPlanStep>? current)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var advance = AdvanceOf(arguments);
        var hasSteps = arguments.TryGetValue("steps", out var stepsValue)
                       && stepsValue is not null
                       && stepsValue is not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
        if (advance is null || advance == hasSteps)
        {
            return (null, "The plan was not changed: give either `steps` or `advance: true`, not both.");
        }

        if (advance == true)
        {
            return Advance(current);
        }

        var steps = StepsOf(stepsValue);
        if (steps is null || steps.Count is < 1 or > NhAssistantPlanLimits.MaxSteps)
        {
            return (null, "The plan was not changed: give 1 to "
                + NhAssistantPlanLimits.MaxSteps.ToString(CultureInfo.InvariantCulture)
                + " steps, each with a `text` of at most "
                + NhAssistantPlanLimits.MaxStepLength.ToString(CultureInfo.InvariantCulture)
                + " characters and a `status` of pending, in_progress or completed.");
        }
        if (steps.Count(step => step.Status == NhAssistantPlanStepStatuses.InProgress) > 1)
        {
            return (null, "The plan was not changed: at most one step can be in_progress.");
        }

        return (steps, null);
    }

    /// <summary>
    /// The <c>plan</c> part of the turn's message for <paramref name="steps"/>.
    /// </summary>
    public static NhAssistantStoredPart PartOf(IReadOnlyList<NhAssistantPlanStep> steps, Guid? runId, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return new NhAssistantStoredPart(
            NhAssistantStoredPart.PlanType,
            Steps: steps,
            RunId: runId,
            UpdatedAt: updatedAt);
    }

    /// <summary>
    /// The plan as the conversation shows it, from its part; <see langword="null"/> for a part that is
    /// not a plan or that holds no valid steps.
    /// </summary>
    public static NhAssistantPlanPartView? ViewOf(NhAssistantStoredPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part is not { Type: NhAssistantStoredPart.PlanType, Steps: { Count: > 0 } steps, UpdatedAt: { } updatedAt }
            || steps.Any(step => string.IsNullOrEmpty(step.Text) || !NhAssistantPlanStepStatuses.IsKnown(step.Status)))
        {
            return null;
        }

        return new NhAssistantPlanPartView(steps, updatedAt);
    }

    /// <summary>
    /// The plan of the run <paramref name="runId"/> in <paramref name="messages"/>, newest first: the
    /// plan part of the first message that holds one of that run; <see langword="null"/> without one.
    /// </summary>
    public static NhAssistantPlanPartView? LatestOfRun(IEnumerable<string> messages, Guid runId)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var partsJson in messages)
        {
            var plan = NhAssistantContent.DeserializeParts(partsJson)
                .LastOrDefault(part => part.Type == NhAssistantStoredPart.PlanType && part.RunId == runId);
            if (plan is not null && ViewOf(plan) is { } view)
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>
    /// The plan of the turn so far and how often it was updated, from the successful calls of the turn
    /// (oldest first), for a turn that waited for a person and continues.
    /// </summary>
    public static (IReadOnlyList<NhAssistantPlanStep>? Plan, int Updates) OfTurn(IReadOnlyList<NhAssistantTurnToolResult> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var updates = calls.Where(call => IsPlanCall(call.ToolId)).ToArray();
        return updates.Length == 0
            ? (null, 0)
            : (PlanOfResult(updates[^1].ResultJson), updates.Length);
    }

    /// <summary>
    /// The result the model reads after an update: the plan as it now stands and how far it is.
    /// </summary>
    public static JsonElement Updated(IReadOnlyList<NhAssistantPlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = true,
                ["code"] = UpdatedCode,
                ["completed"] = steps.Count(step => step.Status == NhAssistantPlanStepStatuses.Completed),
                ["total"] = steps.Count,
                ["steps"] = steps
                    .Select(step => new Dictionary<string, object?>
                    {
                        ["text"] = step.Text,
                        ["status"] = ModelStatusOf(step.Status)
                    })
                    .ToArray()
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// The result of an update that changed nothing.
    /// </summary>
    public static JsonElement Refused(string code, string message)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = code,
                ["message"] = message
            },
            NhAssistantContent.JsonOptions);
    }

    private static (IReadOnlyList<NhAssistantPlanStep>? Plan, string? Refusal) Advance(IReadOnlyList<NhAssistantPlanStep>? current)
    {
        if (current is not { Count: > 0 })
        {
            return (null, "The plan was not changed: this turn has no plan yet; set its `steps` first.");
        }

        var steps = current.ToList();
        var inProgress = steps.FindIndex(step => step.Status == NhAssistantPlanStepStatuses.InProgress);
        if (inProgress >= 0)
        {
            steps[inProgress] = steps[inProgress] with { Status = NhAssistantPlanStepStatuses.Completed };
        }

        // The next step is the first pending one after the completed step, then any pending one.
        var next = steps.FindIndex(Math.Max(inProgress, 0), step => step.Status == NhAssistantPlanStepStatuses.Pending);
        if (next < 0)
        {
            next = steps.FindIndex(step => step.Status == NhAssistantPlanStepStatuses.Pending);
        }
        if (next >= 0)
        {
            steps[next] = steps[next] with { Status = NhAssistantPlanStepStatuses.InProgress };
        }
        else if (inProgress < 0)
        {
            return (null, "The plan was not changed: every step is completed; set new `steps` for further work.");
        }

        return (steps, null);
    }

    private static bool? AdvanceOf(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue("advance", out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined } => false,
            // Anything else is no answer to the question whether to advance.
            _ => null
        };
    }

    private static List<NhAssistantPlanStep>? StepsOf(object? value)
    {
        if (value is not JsonElement element)
        {
            element = value is null ? default : JsonSerializer.SerializeToElement(value, NhAssistantContent.JsonOptions);
        }
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > NhAssistantPlanLimits.MaxSteps)
        {
            return null;
        }

        var steps = new List<NhAssistantPlanStep>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("text", out var textValue)
                || textValue.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("status", out var statusValue)
                || statusValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = PlainText(textValue.GetString());
            var status = StatusOf(statusValue.GetString());
            if (text is null || status is null)
            {
                return null;
            }

            steps.Add(new NhAssistantPlanStep(text, status));
        }

        return steps;
    }

    /// <summary>
    /// A step as plain text on one line: control and format characters are dropped, every run of white
    /// space becomes one space, and the result must be 1 to <see cref="NhAssistantPlanLimits.MaxStepLength"/>
    /// characters. Markup stays text; clients never render it.
    /// </summary>
    internal static string? PlainText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            var category = char.GetUnicodeCategory(character);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            builder.Append(character);
        }

        return builder.Length is 0 or > NhAssistantPlanLimits.MaxStepLength ? null : builder.ToString();
    }

    private static string? StatusOf(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "pending" => NhAssistantPlanStepStatuses.Pending,
            ModelInProgress or NhAssistantPlanStepStatuses.InProgress => NhAssistantPlanStepStatuses.InProgress,
            "completed" => NhAssistantPlanStepStatuses.Completed,
            _ => null
        };
    }

    private static string ModelStatusOf(string status)
    {
        return status == NhAssistantPlanStepStatuses.InProgress ? ModelInProgress : status;
    }

    private static IReadOnlyList<NhAssistantPlanStep>? PlanOfResult(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("steps", out var steps)
                ? StepsOf(steps.Clone())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
