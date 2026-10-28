using System.Text.Json;

namespace NewHeap.Platform.AI.Scripting.Tests;

/// <summary>
/// An engine-only host: the tools a test offers, with a handler per tool that decides as a turn would.
/// It records every call it received, so a test proves which calls reached a tool and which never did.
/// </summary>
internal sealed class FakeScriptHost : INhAiScriptHost
{
    public const string ProjectSchema = """
        {"type":"object","properties":{"query":{"anyOf":[{"type":"string"},{"type":"null"}]},"limit":{"type":"integer"}},"additionalProperties":false}
        """;

    public const string ProjectListSchema = """
        {"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"key":{"type":"string"},"name":{"type":"string"}},"required":["id","key","name"],"additionalProperties":false}}
        """;

    public const string RiskSchema = """
        {"type":"object","properties":{"projectId":{"type":"string","format":"uuid"},"level":{"type":"string","enum":["Low","Medium","High"]}},"required":["projectId"],"additionalProperties":false}
        """;

    public const string StatusSchema = """
        {"type":"object","properties":{"projectId":{"type":"string","format":"uuid"},"status":{"type":"string","enum":["Draft","Active","OnHold","Completed","Archived"]}},"required":["projectId","status"],"additionalProperties":false}
        """;

    private readonly List<NhAiScriptToolOffer> _tools = [];
    private readonly Dictionary<string, Func<JsonElement, NhAiScriptCallOutcome>> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyList<NhAiScriptToolOffer> Tools => _tools;

    public List<NhAiScriptToolCall> Calls { get; } = [];

    public FakeScriptHost Tool(
        string name,
        Func<JsonElement, NhAiScriptCallOutcome> handler,
        NhAiToolEffect effect = NhAiToolEffect.ReadOnly,
        string inputSchema = ProjectSchema,
        string outputSchema = "{}",
        string? id = null,
        NhAiCompositionFeature feature = NhAiCompositionFeature.None)
    {
        _tools.Add(new NhAiScriptToolOffer(name, Descriptor(id ?? name.Replace('_', '.'), name, effect, inputSchema, outputSchema, feature)));
        _handlers[name] = handler;
        return this;
    }

    public ValueTask<NhAiScriptCallOutcome> CallAsync(NhAiScriptToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(call);
        return ValueTask.FromResult(_handlers[call.Tool.Name](call.Arguments));
    }

    public static NhAiToolDescriptor Descriptor(
        string id,
        string name,
        NhAiToolEffect effect,
        string inputSchema,
        string outputSchema,
        NhAiCompositionFeature feature = NhAiCompositionFeature.None)
    {
        return new NhAiToolDescriptor(
            id,
            1,
            "The " + name + " test tool.",
            typeof(JsonElement),
            typeof(JsonElement),
            effect,
            NhAiToolExposure.Agent,
            false,
            [])
        {
            ExportName = name,
            CatalogId = "test",
            InputSchemaJson = inputSchema,
            OutputSchemaJson = outputSchema,
            ContractHash = new string('a', 64),
            CompositionFeature = feature
        };
    }

    public static NhAiScriptCallOutcome Ok(object? data)
    {
        return NhAiScriptCallOutcome.Completed(JsonSerializer.SerializeToElement(new { success = true, data }));
    }

    public static NhAiScriptCallOutcome Fail(string code, string message)
    {
        return NhAiScriptCallOutcome.Completed(JsonSerializer.SerializeToElement(new { success = false, code, message }));
    }

    public static JsonElement ProposalRecorded(Guid proposalId)
    {
        return JsonSerializer.SerializeToElement(new
        {
            success = false,
            code = "assistant-proposal-recorded",
            proposalId,
            message = "The proposal was recorded and waits for a person's approval."
        });
    }
}
