using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// The strict structured-output check of the AI test clients against schemas that
/// Microsoft.Extensions.AI generates, and against the same schemas once the application normalized
/// them (LS37).
/// </summary>
public sealed class NhAiStrictStructuredOutputTests
{
    /// <summary>
    /// The options an application passes to <see cref="AIJsonUtilities.CreateJsonSchema"/> for strict
    /// output: closed objects and every property required.
    /// </summary>
    private static readonly AIJsonSchemaCreateOptions StrictOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            DisallowAdditionalProperties = true,
            RequireAllProperties = true
        }
    };

    [Fact]
    public void A_reference_into_the_properties_is_refused_and_the_same_type_in_defs_is_accepted()
    {
        // A list element type used twice: the second list refers into the first one's items.
        var generated = AIJsonUtilities.CreateJsonSchema(typeof(Board), inferenceOptions: StrictOptions);
        Assert.Equal(
            "#/properties/open/items",
            generated.GetProperty("properties").GetProperty("done").GetProperty("items").GetProperty("$ref").GetString());

        var refused = Assert.Single(NhAiStrictStructuredOutput.Validate(generated));
        Assert.Equal(NhAiStrictStructuredOutput.ReferenceRule, refused.Rule);
        Assert.Equal("#/properties/done/items", refused.Path);

        // The application moves the element type into $defs and refers to it from both lists.
        var normalized = JsonNode.Parse(generated.GetRawText())!.AsObject();
        var line = normalized["properties"]!["open"]!["items"]!.DeepClone();
        normalized["$defs"] = new JsonObject { ["line"] = line };
        normalized["properties"]!["open"]!["items"] = new JsonObject { ["$ref"] = "#/$defs/line" };
        normalized["properties"]!["done"]!["items"] = new JsonObject { ["$ref"] = "#/$defs/line" };

        Assert.Empty(NhAiStrictStructuredOutput.Validate(JsonSerializer.SerializeToElement(normalized)));
    }

    [Fact]
    public void Generated_schemas_of_repeated_nullable_and_recursive_types_follow_the_strict_rules_as_documented()
    {
        // Repeated object types are written out in place, nullable objects allow null in their type,
        // and a type that contains itself refers to the root: all accepted.
        Assert.Empty(NhAiStrictStructuredOutput.Validate(Schema<Order>()));
        Assert.Empty(NhAiStrictStructuredOutput.Validate(Schema<Contact>()));
        Assert.Equal(
            "#",
            Schema<Node>().GetProperty("properties").GetProperty("children").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Empty(NhAiStrictStructuredOutput.Validate(Schema<Node>()));

        // A recursive type below the root refers into the properties: refused until it moves to $defs.
        var nested = Assert.Single(NhAiStrictStructuredOutput.Validate(Schema<Tree>()));
        Assert.Equal(NhAiStrictStructuredOutput.ReferenceRule, nested.Rule);
        Assert.Equal("#/properties/root/properties/branches/items/properties/branches", nested.Path);
        Assert.Empty(NhAiStrictStructuredOutput.Validate(JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "title": { "type": "string" },
                "root": { "$ref": "#/$defs/branch" }
              },
              "required": ["title", "root"],
              "additionalProperties": false,
              "$defs": {
                "branch": {
                  "type": "object",
                  "properties": {
                    "label": { "type": "string" },
                    "branches": { "type": "array", "items": { "$ref": "#/$defs/branch" } }
                  },
                  "required": ["label", "branches"],
                  "additionalProperties": false
                }
              }
            }
            """).RootElement));
    }

    [Fact]
    public void The_default_response_format_leaves_objects_open_and_optional_properties_out_of_required()
    {
        var format = ChatResponseFormat.ForJsonSchema<Contact>();
        var open = NhAiStrictStructuredOutput.Validate(format.Schema!.Value);
        Assert.Equal(
            ["#", "#/properties/home", "#/properties/work"],
            open.Select(violation => violation.Path));
        Assert.All(open, violation => Assert.Equal(NhAiStrictStructuredOutput.AdditionalPropertiesRule, violation.Rule));

        var optional = NhAiStrictStructuredOutput.Validate(AIJsonUtilities.CreateJsonSchema(typeof(Page)));
        Assert.Contains(optional, violation => violation is { Rule: NhAiStrictStructuredOutput.RequiredRule, Path: "#/properties/limit" });
        Assert.Empty(NhAiStrictStructuredOutput.Validate(Schema<Page>()));

        Assert.Equal(
            NhAiStrictStructuredOutput.ReferenceRule,
            Assert.Single(NhAiStrictStructuredOutput.Validate(JsonDocument.Parse("""
                { "type": "object", "properties": { "a": { "$ref": "#/$defs/missing" } }, "required": ["a"], "additionalProperties": false, "$defs": {} }
                """).RootElement)).Rule);
    }

    [Fact]
    public async Task The_strict_deterministic_client_refuses_the_call_as_a_provider_would()
    {
        var refusedFormat = ChatResponseFormat.ForJsonSchema(
            AIJsonUtilities.CreateJsonSchema(typeof(Board), inferenceOptions: StrictOptions),
            "board");
        var acceptedFormat = ChatResponseFormat.ForJsonSchema(Schema<Order>(), "order");
        var client = new NhAiDeterministicChatClient("""{"billing":null}""", """{"open":[]}""")
        {
            StrictStructuredOutput = true
        };
        ChatMessage[] messages = [new(ChatRole.User, "Plan the board.")];

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync(messages, new ChatOptions { ResponseFormat = refusedFormat }));
        var refusedStream = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(messages, new ChatOptions { ResponseFormat = refusedFormat }))
            {
            }
        });
        var answered = await client.GetResponseAsync(messages, new ChatOptions { ResponseFormat = acceptedFormat });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("'board'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("#/properties/done/items", refused.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, refusedStream.StatusCode);
        // A refused request consumes no response and is still recorded.
        Assert.Equal("""{"billing":null}""", answered.Text);
        Assert.Equal(3, client.Requests.Count);

        // Off by default: the same request is answered.
        var lenient = new NhAiDeterministicChatClient("accepted");
        Assert.Equal("accepted", (await lenient.GetResponseAsync(messages, new ChatOptions { ResponseFormat = refusedFormat })).Text);

        var scripted = new NhAiScriptedChatClient { StrictStructuredOutput = true }.RespondWithText("never played");
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            scripted.GetResponseAsync(messages, new ChatOptions { ResponseFormat = refusedFormat }));
        Assert.Equal(1, scripted.RemainingSteps);
    }

    [Fact]
    public async Task A_governed_call_with_a_refused_schema_fails_without_a_retry()
    {
        var client = new NhAiDeterministicChatClient("unused") { StrictStructuredOutput = true };
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("structured-model", client);
        services.AddSingleton<INhAiUsageSink>(new NhAiCapturedUsageSink());
        services.AddNewHeapPlatformAI(ai => ai.AddChatProfile(
                "structured-assistant",
                profile => profile
                    .UseKeyedClient("structured-model")
                    .RequireCapabilities(NhAiModelCapability.Chat | NhAiModelCapability.StructuredOutput)
                    .PermitDataClassifications(NhAiDataClassification.Internal)
                    .WithBudget(2_000, 1_000, 4, 1m)
                    .AllowSafeRetry())
            .UseBudgetManager<NhAiTestBudgetManager>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiChatExecutor>()
            .GetResponseAsync(new NhAiChatRequest(
                new NhAiInvocationContext("planner", "planning", new Dictionary<string, string>())
                {
                    ActorKind = NhAiActorKind.Agent,
                    AccountableOwnerId = "owner-1"
                },
                "structured-assistant",
                NhAiModelCapability.StructuredOutput,
                NhAiDataClassification.Internal,
                [new ChatMessage(ChatRole.User, "Plan the board.")])
            {
                Options = new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        AIJsonUtilities.CreateJsonSchema(typeof(Board), inferenceOptions: StrictOptions),
                        "board")
                },
                EstimatedInputTokens = 10,
                RequestedOutputTokens = 100
            });

        // A provider's 400 is no transient failure, so a retry-eligible profile does not call again.
        Assert.False(result.Success);
        Assert.Equal(NhAiModelFailureCodes.DependencyUnavailable, Assert.Single(result.GetResultItems()).Name);
        Assert.Single(client.Requests);
    }

    private static JsonElement Schema<T>()
    {
        return AIJsonUtilities.CreateJsonSchema(typeof(T), inferenceOptions: StrictOptions);
    }

    public sealed record Address(string Street, string City);

    public sealed record Order(Address Billing, Address Shipping);

    public sealed record Line(string Sku, int Quantity);

    public sealed record Board(IReadOnlyList<Line> Open, IReadOnlyList<Line> Done);

    public sealed record Contact(string Name, Address? Home, Address? Work);

    public sealed record Node(string Name, IReadOnlyList<Node> Children);

    public sealed record Branch(string Label, IReadOnlyList<Branch> Branches);

    public sealed record Tree(string Title, Branch Root);

    public sealed record Page(string Query, int Limit = 10);
}
