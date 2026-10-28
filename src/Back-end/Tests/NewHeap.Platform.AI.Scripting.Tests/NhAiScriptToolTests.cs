using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Mcp;
using Xunit;

namespace NewHeap.Platform.AI.Scripting.Tests;

/// <summary>
/// The model's view of the script tool without a database: TypeScript-style signatures generated from
/// the tools' JSON schemas, a description that stays within its limit with many tools, the tool's marker
/// and descriptor, the registration and how the steps of the assistant's runtime map to a script's calls.
/// </summary>
public sealed class NhAiScriptToolTests
{
    [Fact]
    public void A_signature_is_generated_from_the_input_and_output_schemas()
    {
        var tool = Offer(
            "projects_change_status_v1",
            NhAiToolEffect.IdempotentMutation,
            FakeScriptHost.StatusSchema,
            """{"type":"object","properties":{"projectId":{"type":"string","format":"uuid"},"previous":{"anyOf":[{"type":"string"},{"type":"null"}]},"tags":{"type":"array","items":{"type":"string"}},"counts":{"type":"object","additionalProperties":{"type":"integer"}}},"required":["projectId"]}""");

        var declaration = NhAiScriptSignatures.Declare(tool);

        Assert.Equal(
            "/** Changes data; it may be recorded as a proposal that waits for approval. The projects_change_status_v1 test tool. */\n"
            + "projects_change_status_v1(input: { projectId: string /* uuid */; status: \"Draft\" | \"Active\" | \"OnHold\" | \"Completed\" | \"Archived\" }): "
            + "Promise<Result<{ projectId: string /* uuid */; previous?: string | null; tags?: Array<string>; counts?: Record<string, number> }>>;",
            declaration);
    }

    [Fact]
    public void Names_that_are_no_identifiers_references_and_descriptions_are_written_safely()
    {
        var tool = Offer(
            "mcp_acme-billing_refund",
            NhAiToolEffect.ReadOnly,
            """{"type":"object","properties":{"order":{"$ref":"#/$defs/order","description":"The order to refund. */ not a comment end"}},"$defs":{"order":{"type":"object","properties":{"id":{"type":"string"},"next":{"$ref":"#/$defs/order"}}}},"required":["order"]}""",
            "{}");

        var signature = NhAiScriptSignatures.Signature(tool);

        Assert.StartsWith("\"mcp_acme-billing_refund\"(input: { /** The order to refund. * / not a comment end */ order: { id?: string; next?: object } })", signature, StringComparison.Ordinal);
        Assert.Equal("tools[\"mcp_acme-billing_refund\"]", NhAiScriptSignatures.Accessor(tool.Name));
    }

    [Fact]
    public void Validation_reports_the_first_field_that_does_not_match_with_its_path()
    {
        var tool = Offer(
            "orders_create_v1",
            NhAiToolEffect.Mutation,
            """{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"sku":{"type":"string"},"quantity":{"type":"integer"}},"required":["sku","quantity"],"additionalProperties":false}},"note":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["lines"],"additionalProperties":false}""",
            "{}");

        Assert.Null(NhAiScriptSignatures.Validate(tool, Json("""{"lines":[{"sku":"a","quantity":2}],"note":null}""")));
        Assert.Equal(
            new NhAiScriptFieldError("lines[1].quantity", "expected an integer, got a number with a fraction"),
            NhAiScriptSignatures.Validate(tool, Json("""{"lines":[{"sku":"a","quantity":2},{"sku":"b","quantity":1.5}]}""")));
        Assert.Equal(new NhAiScriptFieldError("note", "expected string, got integer"), NhAiScriptSignatures.Validate(tool, Json("""{"lines":[],"note":3}""")));
        Assert.Equal(new NhAiScriptFieldError("lines[0].sku", "required"), NhAiScriptSignatures.Validate(tool, Json("""{"lines":[{"quantity":1}]}""")));
    }

    [Fact]
    public void The_description_lists_every_signature_and_stays_within_its_limit_with_many_tools()
    {
        var few = Enumerable.Range(0, 3).Select(index => Offer("tool_" + index + "_v1", NhAiToolEffect.ReadOnly, FakeScriptHost.ProjectSchema, FakeScriptHost.ProjectListSchema)).ToArray();
        var many = Enumerable.Range(0, 400).Select(index => Offer("tool_" + index + "_v1", NhAiToolEffect.ReadOnly, FakeScriptHost.ProjectSchema, FakeScriptHost.ProjectListSchema)).ToArray();
        var limits = new NhAiScriptLimits { MaxDescriptionLength = 6_000 };

        var short_ = NhAiScriptTool.Describe(few, limits);
        var long_ = NhAiScriptTool.Describe(many, limits);

        Assert.Contains("tool_2_v1(input?: { query?: string | null; limit?: number }): Promise<Result<Array<{ id: string /* uuid */; key: string; name: string }>>>;", short_, StringComparison.Ordinal);
        Assert.Contains("declare const tools", short_, StringComparison.Ordinal);
        Assert.True(long_.Length <= limits.MaxDescriptionLength, long_.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("tool_0_v1(input?: { query?: string | null; limit?: number })", long_, StringComparison.Ordinal);
        Assert.Contains(" more", long_, StringComparison.Ordinal);
        Assert.Contains("exact signature", long_, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_tool_is_marked_as_the_script_tool_and_is_read_only_itself()
    {
        var descriptor = NhAiScriptTool.Descriptor;

        Assert.Equal(NhAiCompositionFeature.Scripts, descriptor.CompositionFeature);
        Assert.Equal(NhAiScriptTool.FunctionName, descriptor.ExportName);
        Assert.Equal(NhAiToolEffect.ReadOnly, descriptor.Effect);
        Assert.Equal(NhAiToolExposure.Agent, descriptor.Exposure);
        Assert.Equal(NhAiToolExposure.Mcp, NhAiScriptTool.McpDescriptor.Exposure);
        Assert.Equal(NhAiCompositionFeature.Scripts, NhAiScriptTool.McpDescriptor.CompositionFeature);
        Assert.NotEqual(descriptor.ContractHash, NhAiScriptTool.McpDescriptor.ContractHash);
        // Its calls may change data, which an MCP client is told even though the script itself does not.
        Assert.False(NhAiToolAnnotationHints.Resolve(descriptor).ReadOnly);
        Assert.Equal(NhAiDataClassification.Confidential, descriptor.DataClassification);
    }

    [Fact]
    public void Registration_adds_the_catalog_the_mcp_contributor_and_the_limits_per_agent()
    {
        var services = new ServiceCollection();

        services.AddNewHeapPlatformAIScripting(scripting => scripting
            .ConfigureAgent("project-assistant", limits => limits.MaxMutationsPerScript = 2)
            .ConfigureMcp(limits => limits.MaxToolCalls = 8)
            .AllowDestructive("projects.delete"));
        services.AddNewHeapPlatformAIScripting(scripting => scripting.ConfigureAgent("risk-desk", limits => limits.MaxToolCalls = 5));

        var options = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(NhAiScriptingOptions)).ImplementationInstance as NhAiScriptingOptions;
        Assert.NotNull(options);
        Assert.Equal(2, options.LimitsOf("project-assistant").MaxMutationsPerScript);
        Assert.Equal(5, options.LimitsOf("risk-desk").MaxToolCalls);
        Assert.Equal(0, options.LimitsOf("someone-else").MaxMutationsPerScript);
        Assert.Equal(8, options.Mcp.MaxToolCalls);
        Assert.True(options.AllowsDestructive("projects.delete"));
        Assert.False(options.AllowsDestructive("projects.archive"));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(INhAiMcpToolContributor));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(INhAiToolCatalog));

        using var provider = services.BuildServiceProvider();
        var catalog = Assert.Single(provider.GetServices<INhAiToolCatalog>().OfType<INhAiAttestedToolCatalog>());
        Assert.Equal(NhAiToolCatalogGovernance.SharedInvoker, catalog.Governance);
        NhAiToolCatalogAttestation.Validate(catalog, provider);
        var function = Assert.Single(catalog.CreateFunctions(provider));
        Assert.Equal(NhAiScriptTool.Descriptor, Assert.IsAssignableFrom<INhAiGovernedAIFunction>(function).Descriptor);
        // Outside a turn the tool describes no tools.
        Assert.Contains("offers no tools", function.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_outside_their_range_fail_the_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddNewHeapPlatformAIScripting(scripting => scripting
            .ConfigureAgent("project-assistant", limits => limits.MaxToolCalls = 100)));
    }

    [Theory]
    [InlineData(NhAssistantToolStepStatus.Succeeded, false, NhAiScriptCallOutcomeKind.Completed)]
    [InlineData(NhAssistantToolStepStatus.Failed, false, NhAiScriptCallOutcomeKind.Completed)]
    [InlineData(NhAssistantToolStepStatus.Proposed, false, NhAiScriptCallOutcomeKind.Proposed)]
    [InlineData(NhAssistantToolStepStatus.NotNeeded, false, NhAiScriptCallOutcomeKind.NotNeeded)]
    [InlineData(NhAssistantToolStepStatus.AwaitingApproval, true, NhAiScriptCallOutcomeKind.AwaitingApproval)]
    [InlineData(NhAssistantToolStepStatus.Refused, false, NhAiScriptCallOutcomeKind.Refused)]
    [InlineData(NhAssistantToolStepStatus.Failed, true, NhAiScriptCallOutcomeKind.Refused)]
    public void A_step_of_the_runtime_is_the_calls_outcome_and_a_stopped_session_stops_the_script(
        NhAssistantToolStepStatus status,
        bool sessionStopped,
        NhAiScriptCallOutcomeKind expected)
    {
        var proposalId = Guid.NewGuid();
        var step = new NhAssistantToolStep(0, "projects.search", status, JsonSerializer.SerializeToElement(new { success = status == NhAssistantToolStepStatus.Succeeded }))
        {
            Code = status == NhAssistantToolStepStatus.Succeeded ? null : "assistant-tool-call-limit-reached",
            ProposalId = proposalId
        };

        var outcome = NhAiScriptStepHost.OutcomeOf(step, sessionStopped);

        Assert.Equal(expected, outcome.Kind);
        if (expected == NhAiScriptCallOutcomeKind.Refused)
        {
            Assert.Equal("assistant-tool-call-limit-reached", outcome.Result.GetProperty("code").GetString());
        }
        if (expected is NhAiScriptCallOutcomeKind.Proposed or NhAiScriptCallOutcomeKind.AwaitingApproval)
        {
            Assert.Equal(proposalId, outcome.ProposalId);
        }
    }

    private static NhAiScriptToolOffer Offer(string name, NhAiToolEffect effect, string input, string output)
    {
        return new NhAiScriptToolOffer(name, FakeScriptHost.Descriptor(name.Replace('_', '.'), name, effect, input, output));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
