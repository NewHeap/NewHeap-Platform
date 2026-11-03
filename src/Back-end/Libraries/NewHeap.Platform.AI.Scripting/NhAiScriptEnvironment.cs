using System.Text.Json;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// What a host adds to a script's global scope next to <c>tools</c> and <c>console</c>: read-only values,
/// frozen in the engine, and platform functions, such as the inputs and the alerts of a rule script; and
/// the tools it refuses beyond the limits. The script tool a model calls adds nothing
/// (<see cref="None"/>). The engine, its sandbox and its limits stay the same.
/// </summary>
internal sealed class NhAiScriptEnvironment
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "tools",
        "console",
        "globalThis",
        "undefined",
        "eval",
        "Function"
    };

    /// <summary>The environment of the script tool: nothing beyond <c>tools</c> and <c>console</c>.</summary>
    public static NhAiScriptEnvironment None { get; } = new();

    /// <summary>
    /// Read-only values by global name, such as <c>subject</c>; the engine freezes each one deeply.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Values { get; init; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>
    /// Platform functions by name: a global name such as <c>done</c>, or a namespace and a member such as
    /// <c>alerts.raise</c>.
    /// </summary>
    public IReadOnlyList<NhAiScriptHostFunction> Functions { get; init; } = [];

    /// <summary>
    /// Why the host refuses a tool that changes something beyond the limits, or <see langword="null"/>
    /// when it does not; for example a write a rule script's version did not opt in.
    /// </summary>
    public Func<NhAiScriptToolOffer, string?>? Refuse { get; init; }

    /// <summary>
    /// Whether a destructive tool opted in to these scripts; <see langword="null"/> asks the scripting
    /// options (<see cref="NhAiScriptingOptions.AllowsDestructive"/>).
    /// </summary>
    public Func<string, bool>? AllowsDestructive { get; init; }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> for a name that hides the engine's own globals or
    /// is defined twice.
    /// </summary>
    public void Validate()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Values.Keys)
        {
            Require(IsIdentifier(name) && !Reserved.Contains(name) && names.Add(name), name);
        }

        var spaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in Functions)
        {
            ArgumentNullException.ThrowIfNull(function);
            var separator = function.Name.IndexOf('.', StringComparison.Ordinal);
            if (separator < 0)
            {
                Require(IsIdentifier(function.Name) && !Reserved.Contains(function.Name) && names.Add(function.Name), function.Name);
                continue;
            }

            var space = function.Name[..separator];
            var member = function.Name[(separator + 1)..];
            Require(IsIdentifier(space) && IsIdentifier(member) && !Reserved.Contains(space), function.Name);
            if (spaces.Add(space))
            {
                Require(names.Add(space), function.Name);
            }
        }
    }

    private static bool IsIdentifier(string name)
    {
        return name.Length is > 0 and <= 64
            && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static void Require(bool valid, string name)
    {
        if (!valid)
        {
            throw new InvalidOperationException($"Script environment: '{name}' is no free global name.");
        }
    }
}

/// <summary>
/// A platform function of a script's environment. A synchronous one (<see cref="Invoke"/>) answers at
/// once; an asynchronous one (<see cref="InvokeAsync"/>) returns a promise that settles in the program's
/// order of calls, as a tool call does. Each gets the program's arguments as JSON.
/// </summary>
internal sealed record NhAiScriptHostFunction
{
    public NhAiScriptHostFunction(string name, Func<IReadOnlyList<JsonElement>, NhAiScriptHostReply> invoke)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(invoke);
        Name = name;
        Invoke = invoke;
    }

    public NhAiScriptHostFunction(
        string name,
        Func<IReadOnlyList<JsonElement>, CancellationToken, ValueTask<NhAiScriptHostReply>> invokeAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(invokeAsync);
        Name = name;
        InvokeAsync = invokeAsync;
    }

    /// <summary>A global name such as <c>done</c>, or <c>namespace.member</c> such as <c>alerts.raise</c>.</summary>
    public string Name { get; }

    public Func<IReadOnlyList<JsonElement>, NhAiScriptHostReply>? Invoke { get; }

    public Func<IReadOnlyList<JsonElement>, CancellationToken, ValueTask<NhAiScriptHostReply>>? InvokeAsync { get; }
}

/// <summary>
/// What a platform function answers: a value for the program, nothing, or an error that the program
/// receives as a <c>TypeError</c>.
/// </summary>
internal readonly record struct NhAiScriptHostReply(JsonElement? Value, string? Error)
{
    public static NhAiScriptHostReply Nothing { get; } = new(null, null);

    public static NhAiScriptHostReply Of(JsonElement value)
    {
        return new NhAiScriptHostReply(value, null);
    }

    public static NhAiScriptHostReply Failed(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new NhAiScriptHostReply(null, error);
    }
}
