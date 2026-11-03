using System.Text.Json;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The rule script as a call of its run: no tool any model is offered, but the call its rule turn records
/// in the thread, with the version's number and hash as its arguments and what the script did as its
/// result. Every tool call of the script is a step under it, whose origin
/// (<see cref="NhAiToolCallOrigin"/>) names this id, the version's hash and
/// <see cref="NhAiToolCallOrigin.RuleScriptVersion"/>.
/// </summary>
public static class NhAssistantRuleScriptTool
{
    /// <summary>The id the calls of a rule script name as their origin.</summary>
    public const string ToolId = "assistant.rule-script";

    /// <summary>The function name of the rule script's call in its run.</summary>
    public const string FunctionName = "rule_script";

    private const string InputSchemaJson = """
        {"type":"object","properties":{"version":{"type":"integer","minimum":1},"hash":{"type":"string"}},"required":["version","hash"],"additionalProperties":false}
        """;

    /// <summary>The descriptor of the rule script's call.</summary>
    public static NhAiToolDescriptor Descriptor { get; } = CreateDescriptor();

    private static NhAiToolDescriptor CreateDescriptor()
    {
        var schemaHash = NhAiCanonicalJson.ComputeHash(new { input = JsonDocument.Parse(InputSchemaJson).RootElement });
        var contractHash = NhAiCanonicalJson.ComputeHash(new
        {
            id = ToolId,
            version = 1,
            effect = nameof(NhAiToolEffect.ReadOnly),
            schemaHash
        });
        return new NhAiToolDescriptor(
            ToolId,
            1,
            "Run the agent's rules as the rule script stored as data.",
            typeof(NhAssistantRuleScriptCallInput),
            typeof(JsonElement),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Agent,
            // The rule turn runs it; every call it makes is authorized on its own.
            true,
            [])
        {
            ExportName = FunctionName,
            CatalogId = "assistant",
            DeclaringAssembly = typeof(NhAssistantRuleScriptTool).Assembly.GetName().Name ?? string.Empty,
            InputSchemaJson = InputSchemaJson,
            SchemaHash = schemaHash,
            ContractHash = contractHash,
            Approval = NhAiApprovalRequirement.NotRequired,
            DataClassification = NhAiDataClassification.Confidential,
            RetentionCategory = NhAiRetentionCategory.ConversationContent
        };
    }
}

/// <summary>
/// The arguments of a rule script's call in its run: which version ran.
/// </summary>
/// <param name="Version">The version, from 1.</param>
/// <param name="Hash">The SHA-256 of its script, lowercase hexadecimal.</param>
public sealed record NhAssistantRuleScriptCallInput(int Version, string Hash);
