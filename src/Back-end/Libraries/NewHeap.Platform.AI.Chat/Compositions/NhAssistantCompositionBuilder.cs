using System.Text.Json;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Declares a composition: a known chain of governed tools that the model calls as one tool
/// (<see cref="NhAssistantBuilder.AddComposition{TInput}"/>). Its steps call registered tools by id, in
/// order; a later step takes values from earlier results; <see cref="Returns"/> declares what returns to
/// the model. The composition runs no code of its own against the application: every step is a call of
/// its tool through the shared invoker, with the caller's rights, and gets exactly the decision the same
/// call gets on its own (<see cref="INhAssistantToolSteps"/>).
/// </summary>
/// <typeparam name="TInput">
/// The input the model passes, bound from the call's JSON with the web defaults of
/// <see cref="System.Text.Json"/>; its JSON schema is the composition's input schema.
/// </typeparam>
public sealed class NhAssistantCompositionBuilder<TInput>
    where TInput : class
{
    private readonly List<NhAssistantCompositionStepDefinition> _steps = [];
    private string? _description;
    private string? _exportName;
    private int _version = 1;
    private NhAiToolExposure _exposure = NhAiToolExposure.Agent;
    private Func<NhAssistantCompositionContext<TInput>, object?>? _returns;
    private int _maxResultBytes = 65_536;

    internal NhAssistantCompositionBuilder(string id)
    {
        Id = id;
    }

    /// <summary>The composition's tool id.</summary>
    public string Id { get; }

    /// <summary>
    /// What the composition does, as the model and MCP clients read it. Required; at most 1,024 characters.
    /// </summary>
    public NhAssistantCompositionBuilder<TInput> Describe(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > NhAssistantCompositionLimits.MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"A composition's description is at most {NhAssistantCompositionLimits.MaxDescriptionLength} characters.",
                nameof(description));
        }

        _description = description;
        return this;
    }

    /// <summary>
    /// The version of the composition's contract; 1 by default. Raise it when its input or result changes.
    /// </summary>
    public NhAssistantCompositionBuilder<TInput> Version(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        _version = version;
        return this;
    }

    /// <summary>
    /// The function name the model and MCP clients call the composition by; by default its id with
    /// underscores and its version, such as <c>projects_overview_v1</c>.
    /// </summary>
    public NhAssistantCompositionBuilder<TInput> ExportAs(string functionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        if (functionName.Length > 64 || !functionName.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new ArgumentException(
                "A composition's function name is at most 64 ASCII letters, digits, underscores or dashes.",
                nameof(functionName));
        }

        _exportName = functionName;
        return this;
    }

    /// <summary>
    /// Also exposes the composition over MCP (TC7), where an exposure that allows compositions
    /// (<c>WithNewHeapPlatformAITools(mcp =&gt; mcp.AllowCompositions())</c>) offers it under the same
    /// governance; a read-only composition is read-only there. Without it the composition is offered to
    /// agents only.
    /// </summary>
    public NhAssistantCompositionBuilder<TInput> ExposeOverMcp()
    {
        _exposure |= NhAiToolExposure.Mcp;
        return this;
    }

    /// <summary>
    /// Adds a step that calls the governed tool <paramref name="toolId"/> with the arguments
    /// <paramref name="arguments"/> builds: an object of the tool's input properties, serialized with
    /// the web defaults. The arguments may use the input and the results of earlier steps
    /// (<see cref="NhAssistantCompositionContext{TInput}.Result"/>). A step that failed or was refused stops
    /// the chain; one that became a proposal or was not needed lets it continue, but a later step that
    /// reads its result stops it.
    /// </summary>
    /// <param name="name">The step's name, unique in the composition, in lowercase dash-case.</param>
    /// <param name="toolId">The id of a registered tool of a catalog that does not change at runtime.</param>
    /// <param name="arguments">Builds the step's arguments.</param>
    public NhAssistantCompositionBuilder<TInput> Step(
        string name,
        string toolId,
        Func<NhAssistantCompositionContext<TInput>, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        NhAssistantNames.ValidateSegment(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        if (_steps.Any(step => string.Equals(step.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"The composition '{Id}' has a step '{name}' already.", nameof(name));
        }
        if (_steps.Count >= NhAssistantToolStepLimits.MaxSteps)
        {
            throw new InvalidOperationException(
                $"A composition has at most {NhAssistantToolStepLimits.MaxSteps} steps.");
        }

        _steps.Add(new NhAssistantCompositionStepDefinition(
            name,
            toolId,
            context => arguments((NhAssistantCompositionContext<TInput>)context)));
        return this;
    }

    /// <summary>
    /// Declares what returns to the model when every step ran: an object built from the input and the
    /// steps' results, serialized with the web defaults as the <c>data</c> of the shared tool result.
    /// Required. When a step stopped the chain, the model receives instead what succeeded and the
    /// stopping step's code.
    /// </summary>
    public NhAssistantCompositionBuilder<TInput> Returns(Func<NhAssistantCompositionContext<TInput>, object?> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _returns = result;
        return this;
    }

    /// <summary>
    /// The largest result returned to the model, in bytes of JSON; 65,536 by default and at most that.
    /// A larger result fails with <see cref="NhAiToolFailureCodes.ResultTooLarge"/>.
    /// </summary>
    public NhAssistantCompositionBuilder<TInput> MaxResultBytes(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1_024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, 65_536);
        _maxResultBytes = bytes;
        return this;
    }

    internal NhAssistantCompositionDefinition Build()
    {
        if (_description is null)
        {
            throw new InvalidOperationException($"The composition '{Id}' needs a description (Describe).");
        }
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException($"The composition '{Id}' needs at least one step.");
        }
        if (_returns is null)
        {
            throw new InvalidOperationException($"The composition '{Id}' needs a result (Returns).");
        }

        var returns = _returns;
        return new NhAssistantCompositionDefinition(
            Id,
            _version,
            _description,
            _exportName ?? NhAssistantCompositionDefinition.DefaultExportName(Id, _version),
            typeof(TInput),
            _exposure,
            _steps.ToArray(),
            context => returns((NhAssistantCompositionContext<TInput>)context),
            (input, steps) => NhAssistantCompositionContext<TInput>.Bind((TInput)input, steps),
            _maxResultBytes);
    }
}

/// <summary>
/// The limits of compositions.
/// </summary>
public static class NhAssistantCompositionLimits
{
    /// <summary>The longest description of a composition.</summary>
    public const int MaxDescriptionLength = 1_024;
}

/// <summary>
/// The stable codes of compositions.
/// </summary>
public static class NhAssistantCompositionCodes
{
    /// <summary>
    /// A step needed the result of an earlier step that has none, for example one that became a proposal
    /// that waits for people; the chain stopped before it.
    /// </summary>
    public const string StepResultUnavailable = "assistant-composition-step-result-unavailable";
}

/// <summary>
/// What a composition's steps and result read: its input and the results of the steps that ran.
/// </summary>
/// <typeparam name="TInput">The composition's input.</typeparam>
public sealed class NhAssistantCompositionContext<TInput> : NhAssistantCompositionContext
    where TInput : class
{
    private NhAssistantCompositionContext(TInput input, Func<string, NhAssistantToolStep?> steps)
        : base(steps)
    {
        Input = input;
    }

    /// <summary>The input the model passed.</summary>
    public TInput Input { get; }

    internal static NhAssistantCompositionContext Bind(TInput input, Func<string, NhAssistantToolStep?> steps)
    {
        return new NhAssistantCompositionContext<TInput>(input, steps);
    }
}

/// <summary>
/// The results of a composition's steps.
/// </summary>
public abstract class NhAssistantCompositionContext
{
    private readonly Func<string, NhAssistantToolStep?> _steps;

    private protected NhAssistantCompositionContext(Func<string, NhAssistantToolStep?> steps)
    {
        _steps = steps;
    }

    /// <summary>
    /// The data of the earlier step <paramref name="step"/>: the <c>data</c> of its shared tool result.
    /// When that step has no data, because it became a proposal, was not needed or did not run, the
    /// chain stops here with <see cref="NhAssistantCompositionCodes.StepResultUnavailable"/>; read
    /// <see cref="TryGetResult"/> where a step's result is optional.
    /// </summary>
    /// <exception cref="ArgumentException">The composition has no step named <paramref name="step"/> before this one.</exception>
    public JsonElement Result(string step)
    {
        if (TryGetResult(step, out var data))
        {
            return data;
        }

        throw new NhAssistantCompositionStop(step);
    }

    /// <summary>
    /// The data of the earlier step <paramref name="step"/>, when it succeeded.
    /// </summary>
    public bool TryGetResult(string step, out JsonElement data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        if (_steps(step) is { } ran && ran.TryGetData(out data))
        {
            return true;
        }

        data = default;
        return false;
    }

    /// <summary>
    /// How the earlier step <paramref name="step"/> ended, for example to name the proposal it made;
    /// <see langword="null"/> when it did not run.
    /// </summary>
    public NhAssistantToolStep? StepOf(string step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        return _steps(step);
    }
}

/// <summary>
/// The internal signal that ends a chain when a step reads an earlier step's result that does not exist.
/// It never leaves the composition: the model receives <see cref="NhAssistantCompositionCodes.StepResultUnavailable"/>.
/// </summary>
internal sealed class NhAssistantCompositionStop(string step)
    : Exception($"The composition step '{step}' has no result.")
{
    public string Step { get; } = step;
}

internal sealed record NhAssistantCompositionStepDefinition(
    string Name,
    string ToolId,
    Func<NhAssistantCompositionContext, object?> Arguments);

/// <summary>
/// A composition as the application declared it.
/// </summary>
internal sealed record NhAssistantCompositionDefinition(
    string Id,
    int Version,
    string Description,
    string ExportName,
    Type InputType,
    NhAiToolExposure Exposure,
    IReadOnlyList<NhAssistantCompositionStepDefinition> Steps,
    Func<NhAssistantCompositionContext, object?> Returns,
    Func<object, Func<string, NhAssistantToolStep?>, NhAssistantCompositionContext> CreateContext,
    int MaxResultBytes)
{
    public static string DefaultExportName(string id, int version)
    {
        var name = new string(id.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
        return name + "_v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
