using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// The input of the script tool: the program, the body of an async function.
/// </summary>
public sealed record NhAiScriptRunInput(string Script);

/// <summary>
/// The script tool: one governed tool, <c>run_script</c>, that takes a short JavaScript program in which
/// each tool the agent is offered in that turn is an async function of <c>tools</c>. The program runs on
/// the server and only its return value goes back to the model. The tool is marked
/// <see cref="NhAiCompositionFeature.Scripts"/>, so it is offered only where code allows scripts (an agent's
/// <c>AllowScripts()</c>, an MCP exposure's <c>AllowScripts()</c>) and the application's switch has them
/// on. Its own effect is read-only: it changes nothing itself, and every call it makes is governed as the
/// same call on its own.
/// </summary>
public static class NhAiScriptTool
{
    /// <summary>The tool id of the script tool.</summary>
    public const string ToolId = "scripts.run";

    /// <summary>The function name the model calls.</summary>
    public const string FunctionName = "run_script";

    /// <summary>The catalog the script tool belongs to.</summary>
    public const string CatalogId = "scripts";

    /// <summary>The argument that holds the program.</summary>
    public const string ScriptArgumentName = "script";

    private const string Summary = "Run a short JavaScript program on the server in which every tool of this turn is an async function; only the value it returns comes back.";

    /// <summary>
    /// The longest time the shared invoker gives one script call: the longest script timeout and a margin,
    /// so a script's own timeout always stops it first.
    /// </summary>
    private static readonly TimeSpan InvocationTimeout = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30);

    private static readonly string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{\"script\":{\"type\":\"string\",\"maxLength\":"
        + NhAiScriptLimits.HardMaxScriptLength.ToString(CultureInfo.InvariantCulture)
        + "}},\"required\":[\"script\"],\"additionalProperties\":false}";

    /// <summary>
    /// The descriptor of the script tool as agents are offered it.
    /// </summary>
    public static NhAiToolDescriptor Descriptor { get; } = CreateDescriptor(NhAiToolExposure.Agent);

    /// <summary>
    /// The descriptor of the script tool as an MCP exposure lists it: the same tool with MCP exposure.
    /// </summary>
    public static NhAiToolDescriptor McpDescriptor { get; } = CreateDescriptor(NhAiToolExposure.Mcp);

    /// <summary>
    /// The JSON schema of the script tool's arguments: one string <c>script</c>.
    /// </summary>
    public static JsonElement CreateInputSchema(NhAiScriptLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var schema = "{\"type\":\"object\",\"properties\":{\"script\":{\"type\":\"string\",\"maxLength\":"
            + limits.MaxScriptLength.ToString(CultureInfo.InvariantCulture)
            + ",\"description\":"
            + JsonSerializer.Serialize("The body of an async JavaScript function. Await the tools of `tools` and return only what the answer needs.")
            + "}},\"required\":[\"script\"],\"additionalProperties\":false}";
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The description of the script tool for one host and its limits: how a program is written, what
    /// each call returns, the limits, and the TypeScript-style declarations of the tools the script may
    /// call. Beyond <see cref="NhAiScriptLimits.MaxDescriptionLength"/> tools are listed with their
    /// argument names and then by name only.
    /// </summary>
    public static string Describe(IReadOnlyList<NhAiScriptToolOffer> tools, NhAiScriptLimits limits)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(limits);
        var builder = new StringBuilder();
        builder.Append("Runs a short JavaScript program on the server and returns only the value the program returns, so a chain of tool calls costs one call. ");
        builder.Append("Write the body of an async function: `await` each call, use loops and conditions, and `return` just what the answer needs. ");
        builder.Append("Each tool below is `tools.<name>(input)`, an async function that takes one object of its input properties. ");
        builder.Append("A call returns the tool's result `{ success: true, data }` or `{ success: false, code, message }`; check `success`. ");
        builder.Append("Every call gets the same governance as the call on its own: a call that changes data may be recorded as a proposal that waits for approval (`code` \"assistant-proposal-recorded\") and has then not happened, and a call that needs approval before the conversation can go on stops the script. ");
        builder.Append("The result lists every call in order with its outcome. ");
        builder.Append("The program runs in strict mode without modules, require, timers, network or host access, for at most ")
            .Append(limits.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)).Append(" seconds and ")
            .Append(limits.MaxToolCalls.ToString(CultureInfo.InvariantCulture)).Append(" tool calls, ");
        builder.Append(limits.MaxMutationsPerScript == 0
            ? "and it is read-only: it is offered no tool that changes data. "
            : "with at most " + limits.MaxMutationsPerScript.ToString(CultureInfo.InvariantCulture) + " calls that change data. ");
        builder.Append("It may return at most ").Append(limits.MaxResultBytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes of JSON. ");
        builder.Append("A call that does not match its signature stops the script with the exact signature and the field to correct. `console.log` output comes back with the result.\n\n");
        builder.Append("```ts\ntype Result<T> = { success: true; data: T } | { success: false; code: string; message?: string; data?: unknown };\ndeclare const tools: {\n");

        var footer = "};\n```\nExample: `const found = await tools.<name>({ ... }); if (!found.success) { return found.code; } return found.data.length;`";
        var remaining = new List<NhAiScriptToolOffer>();
        foreach (var tool in tools)
        {
            var declaration = Indent(NhAiScriptSignatures.Declare(tool));
            if (remaining.Count == 0 && builder.Length + declaration.Length + footer.Length + 200 <= limits.MaxDescriptionLength)
            {
                builder.Append(declaration);
                continue;
            }

            remaining.Add(tool);
        }

        var unlisted = new List<NhAiScriptToolOffer>();
        foreach (var tool in remaining)
        {
            var compact = "  " + NhAiScriptSignatures.DeclareCompact(tool) + "\n";
            if (unlisted.Count == 0 && builder.Length + compact.Length + footer.Length + 200 <= limits.MaxDescriptionLength)
            {
                builder.Append(compact);
                continue;
            }

            unlisted.Add(tool);
        }

        if (unlisted.Count > 0)
        {
            const string tail = ". A call returns the exact signature when it does not match.\n";
            var more = ", and " + unlisted.Count.ToString(CultureInfo.InvariantCulture) + " more";
            var names = new StringBuilder("  // Also: ");
            var listed = 0;
            foreach (var tool in unlisted)
            {
                var name = (listed == 0 ? string.Empty : ", ") + NhAiScriptSignatures.MemberName(tool.Name);
                if (builder.Length + names.Length + name.Length + more.Length + tail.Length + footer.Length > limits.MaxDescriptionLength)
                {
                    break;
                }

                names.Append(name);
                listed++;
            }
            if (listed < unlisted.Count)
            {
                names.Append(listed == 0 ? string.Empty : ", ").Append("and ").Append((unlisted.Count - listed).ToString(CultureInfo.InvariantCulture)).Append(" more");
            }
            builder.Append(names).Append(tail);
        }
        if (tools.Count == 0)
        {
            builder.Append("  // This turn offers no tools to scripts.\n");
        }

        builder.Append(footer);
        return builder.ToString();
    }

    private static string Indent(string declaration)
    {
        return "  " + declaration.Replace("\n", "\n  ", StringComparison.Ordinal) + "\n";
    }

    private static NhAiToolDescriptor CreateDescriptor(NhAiToolExposure exposure)
    {
        var schemaHash = NhAiCanonicalJson.ComputeHash(new
        {
            input = JsonDocument.Parse(InputSchemaJson).RootElement,
            output = JsonDocument.Parse(NhAiScriptResult.DataSchemaJson).RootElement
        });
        var contractHash = NhAiCanonicalJson.ComputeHash(new
        {
            id = ToolId,
            version = 1,
            effect = nameof(NhAiToolEffect.ReadOnly),
            feature = nameof(NhAiCompositionFeature.Scripts),
            exposure = exposure.ToString(),
            schemaHash
        });
        return new NhAiToolDescriptor(
            ToolId,
            1,
            Summary,
            typeof(NhAiScriptRunInput),
            typeof(JsonElement),
            NhAiToolEffect.ReadOnly,
            exposure,
            false,
            [])
        {
            ExportName = FunctionName,
            CatalogId = CatalogId,
            DeclaringAssembly = typeof(NhAiScriptTool).Assembly.GetName().Name ?? string.Empty,
            InputSchemaJson = InputSchemaJson,
            OutputSchemaJson = NhAiScriptResult.DataSchemaJson,
            SchemaHash = schemaHash,
            ContractHash = contractHash,
            Approval = NhAiApprovalRequirement.NotRequired,
            CompositionFeature = NhAiCompositionFeature.Scripts,
            // Its calls may change data; an MCP client is told so even though the script itself does not.
            ReadOnlyHint = NhAiToolHint.False,
            IdempotentHint = NhAiToolHint.False,
            OpenWorldHint = NhAiToolHint.True,
            Timeout = InvocationTimeout,
            MaxConcurrency = 16,
            MaxInputBytes = 65_536,
            MaxResultBytes = 2 * 1024 * 1024,
            // The value of a script can carry data of every tool it called.
            DataClassification = NhAiDataClassification.Confidential,
            RetentionCategory = NhAiRetentionCategory.ConversationContent
        };
    }
}

/// <summary>
/// What one script function works with: the tools it describes to the model and the limits of its
/// scripts. In a turn they are the agent's; for an MCP request the caller's tools and the MCP limits.
/// </summary>
internal sealed record NhAiScriptScope(IReadOnlyList<NhAiToolDescriptor>? Tools, NhAiScriptLimits Limits);

/// <summary>
/// The model-facing script function behind the governed function. Its description lists the tools of
/// its scope; a call opens the call's step session (<see cref="INhAssistantToolSteps"/>), runs the program
/// over the session's tools and completes the session with the result, which audits the call.
/// </summary>
internal sealed class NhAiScriptFunction : AIFunction
{
    private static readonly JsonSerializerOptions ResultOptions = AIJsonUtilities.DefaultOptions;

    private readonly IServiceProvider _services;
    private readonly NhAiToolDescriptor _descriptor;
    private readonly Func<NhAiScriptScope> _scope;
    private readonly NhAiScriptingOptions _options;
    private readonly NhAiScriptRunner _runner;
    private readonly object _sync = new();
    private string? _describedKey;
    private string? _description;

    public NhAiScriptFunction(IServiceProvider services, NhAiToolDescriptor descriptor, Func<NhAiScriptScope> scope)
    {
        _services = services;
        _descriptor = descriptor;
        _scope = scope;
        _options = services.GetService<NhAiScriptingOptions>() ?? new NhAiScriptingOptions();
        _runner = services.GetService<NhAiScriptRunner>() ?? new NhAiScriptRunner(_options);
    }

    public override string Name => _descriptor.ExportName;

    public override string Description
    {
        get
        {
            var scope = _scope();
            var offers = (scope.Tools ?? [])
                .Select(tool => new NhAiScriptToolOffer(string.IsNullOrWhiteSpace(tool.ExportName) ? tool.Id : tool.ExportName, tool))
                .ToArray();
            var callable = _runner.CallableTools(offers, scope.Limits);
            var key = string.Join(
                "\n",
                callable.Select(offer => offer.Name + "=" + offer.Descriptor.Id + "@"
                    + offer.Descriptor.Version.ToString(CultureInfo.InvariantCulture) + ":" + offer.Descriptor.ContractHash))
                + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(scope.Limits).ToString(CultureInfo.InvariantCulture);
            lock (_sync)
            {
                if (!string.Equals(key, _describedKey, StringComparison.Ordinal) || _description is null)
                {
                    _description = NhAiScriptTool.Describe(callable, scope.Limits);
                    _describedKey = key;
                }

                return _description;
            }
        }
    }

    public override JsonElement JsonSchema => NhAiScriptTool.CreateInputSchema(_scope().Limits);

    public override JsonSerializerOptions JsonSerializerOptions => ResultOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var script = ReadScript(arguments);
        var limits = _scope().Limits;
        var begun = await _services.GetRequiredService<INhAssistantToolSteps>().BeginAsync(
            new NhAssistantToolStepRequest(_descriptor, NhAiToolCallOriginKind.Script)
            {
                Hash = NhAiScriptRunner.HashOf(script),
                MaxSteps = limits.MaxToolCalls,
                MaxMutationSteps = limits.MaxMutationsPerScript,
                // A destructive tool that did not opt in is never a step of a script.
                Tools = tool => tool.Effect != NhAiToolEffect.Destructive || _options.AllowsDestructive(tool.Id)
            },
            cancellationToken);
        if (!begun.Success)
        {
            // The call was not authorized, or it runs where no steps run; nothing ran.
            return NhAiToolResult.Create(TaskResult<JsonElement>.Failed(begun), ResultOptions);
        }

        var session = begun.Data;
        JsonElement result;
        try
        {
            var outcome = await _runner.RunAsync(script, new NhAiScriptStepHost(session), limits, cancellationToken);
            result = outcome.ToToolResult();
        }
        catch (Exception)
        {
            // The call is audited also when it is cancelled or ends unexpectedly.
            await session.CompleteAsync(NhAiToolResult.Failed(NhAiToolFailureCodes.Failed, null), CancellationToken.None);
            throw;
        }

        await session.CompleteAsync(result, cancellationToken);
        return result;
    }

    private static string ReadScript(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue(NhAiScriptTool.ScriptArgumentName, out var value) || value is null)
        {
            throw new ArgumentException("The script tool needs the argument 'script'.", nameof(arguments));
        }
        if (arguments.Count != 1)
        {
            throw new ArgumentException("The script tool takes only the argument 'script'.", nameof(arguments));
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => throw new ArgumentException("The argument 'script' is a string.", nameof(arguments))
        };
    }
}
