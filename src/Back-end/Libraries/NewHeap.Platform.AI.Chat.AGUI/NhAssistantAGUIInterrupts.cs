using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AGUI;

/// <summary>
/// What an AG-UI interrupt of the assistant waits for.
/// </summary>
internal enum NhAssistantAGUIInterruptKind
{
    /// <summary>The decision of a proposal: the semantics of <c>approvals/{approvalId}/decide</c>.</summary>
    Approval = 1,

    /// <summary>The answer to an <c>ask_question</c> question: the semantics of <c>questions/{questionId}/answer</c>.</summary>
    Question = 2,

    /// <summary>The answer to an MCP server's request for input: the semantics of <c>elicitations/{elicitationId}/answer</c>.</summary>
    Elicitation = 3
}

/// <summary>
/// An interrupt id the endpoint issued, read back from a resume entry. An approval's id carries the hash of
/// the proposal the person saw, so a decision is bound to that proposal exactly as the decide endpoint's
/// <c>expectedProposalHash</c> binds it.
/// </summary>
internal sealed record NhAssistantAGUIInterruptRef(NhAssistantAGUIInterruptKind Kind, Guid Id, string? ProposalHash);

/// <summary>
/// The interrupts of the assistant's AG-UI endpoint: their ids, the AG-UI interrupt objects and the answers
/// a resume entry may carry. Every value a resume entry carries is untrusted; it only selects an interrupt
/// the endpoint issued and supplies an answer that the decide, answer and elicitation services check again.
/// </summary>
internal static class NhAssistantAGUIInterrupts
{
    private const string ApprovalPrefix = "nh-approval:";
    private const string QuestionPrefix = "nh-question:";
    private const string ElicitationPrefix = "nh-elicitation:";
    private const int MaxProposalHashLength = 256;
    private const int MaxReasonLength = 500;

    public static string IdOf(NhAssistantApprovalView approval)
    {
        return $"{ApprovalPrefix}{approval.ApprovalId:N}:{approval.ProposalHash}";
    }

    public static string IdOf(NhAssistantQuestionView question)
    {
        return $"{QuestionPrefix}{question.QuestionId:N}";
    }

    public static string IdOf(NhAssistantElicitationView elicitation)
    {
        return $"{ElicitationPrefix}{elicitation.ElicitationId:N}";
    }

    /// <summary>
    /// Reads an interrupt id the endpoint issued; <see langword="false"/> for any other value.
    /// </summary>
    public static bool TryParse(string? interruptId, out NhAssistantAGUIInterruptRef reference)
    {
        reference = null!;
        if (string.IsNullOrEmpty(interruptId))
        {
            return false;
        }
        if (interruptId.StartsWith(ApprovalPrefix, StringComparison.Ordinal))
        {
            var rest = interruptId[ApprovalPrefix.Length..];
            var separator = rest.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0
                || !Guid.TryParseExact(rest[..separator], "N", out var approvalId)
                || rest.Length - separator - 1 is 0 or > MaxProposalHashLength)
            {
                return false;
            }

            reference = new NhAssistantAGUIInterruptRef(NhAssistantAGUIInterruptKind.Approval, approvalId, rest[(separator + 1)..]);
            return true;
        }
        if (TryParseId(interruptId, QuestionPrefix, out var questionId))
        {
            reference = new NhAssistantAGUIInterruptRef(NhAssistantAGUIInterruptKind.Question, questionId, null);
            return true;
        }
        if (TryParseId(interruptId, ElicitationPrefix, out var elicitationId))
        {
            reference = new NhAssistantAGUIInterruptRef(NhAssistantAGUIInterruptKind.Elicitation, elicitationId, null);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The interrupt of a proposal the turn waits for. It names the proposing tool call when the run reported
    /// it, as an AG-UI tool approval (<c>tool_call</c>), and is a <c>confirmation</c> otherwise. Its metadata holds
    /// the approval part of the conversation.
    /// </summary>
    public static AGUIInterrupt Approval(NhAssistantApprovalView approval, string? toolCallId)
    {
        var decisions = new JsonArray("approve", "reject");
        if (approval.Consultation?.LeaveRoute is not null)
        {
            decisions.Add("leave");
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["approved"] = new JsonObject { ["type"] = "boolean", ["description"] = "Approve (true) or reject (false) the proposal." },
                ["decision"] = new JsonObject { ["type"] = "string", ["enum"] = decisions },
                ["reason"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxReasonLength }
            }
        };
        return new AGUIInterrupt
        {
            Id = IdOf(approval),
            Reason = toolCallId is null ? InterruptReasons.Confirmation : InterruptReasons.ToolCall,
            Message = approval.Summary,
            ToolCallId = toolCallId,
            ResponseSchema = Element(schema),
            ExpiresAt = Timestamp(approval.ExpiresAt),
            Metadata = Metadata("approval", NhAssistantDtoMapper.ToDto(approval))
        };
    }

    /// <summary>
    /// The interrupt of the question the turn waits for: the answer is the zero-based index of a choice.
    /// </summary>
    public static AGUIInterrupt Question(NhAssistantQuestionView question)
    {
        var choices = new JsonArray();
        for (var index = 0; index < question.Choices.Count; index++)
        {
            choices.Add(new JsonObject { ["const"] = index, ["title"] = question.Choices[index] });
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["choice"] = new JsonObject { ["type"] = "integer", ["oneOf"] = choices }
            },
            ["required"] = new JsonArray("choice")
        };
        return new AGUIInterrupt
        {
            Id = IdOf(question),
            Reason = InterruptReasons.InputRequired,
            Message = question.Question,
            ToolCallId = question.QuestionId.ToString("D"),
            ResponseSchema = Element(schema),
            ExpiresAt = Timestamp(question.ExpiresAt),
            Metadata = Metadata("question", NhAssistantDtoMapper.ToDto(question))
        };
    }

    /// <summary>
    /// The interrupt object of an MCP server's request for input, which the run carries in a <c>CUSTOM</c>
    /// event (<see cref="NhAssistantAGUICustomEvents.ElicitationRequested"/>): the answer is an action and, for
    /// a form that is accepted, the form's values.
    /// </summary>
    public static AGUIInterrupt Elicitation(NhAssistantElicitationView elicitation)
    {
        var fields = new JsonObject();
        var required = new JsonArray();
        foreach (var field in elicitation.Fields)
        {
            fields[field.Name] = FieldSchema(field);
            if (field.Required)
            {
                required.Add(field.Name);
            }
        }

        var properties = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("accept", "decline", "cancel") }
        };
        if (elicitation.Fields.Count > 0)
        {
            properties["content"] = new JsonObject { ["type"] = "object", ["properties"] = fields, ["required"] = required };
        }

        return new AGUIInterrupt
        {
            Id = IdOf(elicitation),
            Reason = InterruptReasons.InputRequired,
            Message = elicitation.Message,
            ToolCallId = elicitation.InvocationId.ToString("D"),
            ResponseSchema = Element(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray("action")
            }),
            ExpiresAt = Timestamp(elicitation.ExpiresAt),
            Metadata = Metadata("elicitation", NhAssistantDtoMapper.ToDto(elicitation))
        };
    }

    /// <summary>
    /// The decision a resume entry of an approval interrupt carries: <c>approved</c> as an AG-UI tool approval
    /// sends it, or <c>decision</c> (<c>approve</c>, <c>reject</c> or <c>leave</c>), with an optional
    /// <c>reason</c>. <see langword="null"/> when it carries none.
    /// </summary>
    public static (string Decision, string? Reason)? DecisionOf(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        string? decision = null;
        if (value.TryGetProperty("decision", out var named) && named.ValueKind == JsonValueKind.String)
        {
            decision = named.GetString();
        }
        else if (value.TryGetProperty("approved", out var approved) && approved.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            decision = approved.GetBoolean() ? "approve" : "reject";
        }
        if (decision is not ("approve" or "reject" or "leave"))
        {
            return null;
        }

        string? reason = null;
        if (value.TryGetProperty("reason", out var given))
        {
            if (given.ValueKind != JsonValueKind.String || given.GetString()!.Length > MaxReasonLength)
            {
                return null;
            }
            reason = given.GetString();
        }

        return (decision, reason);
    }

    /// <summary>
    /// The zero-based choice a resume entry of a question interrupt carries, or <see langword="null"/>.
    /// </summary>
    public static int? ChoiceOf(JsonElement? payload)
    {
        return payload is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty("choice", out var choice)
            && choice.ValueKind == JsonValueKind.Number
            && choice.TryGetInt32(out var index)
                ? index
                : null;
    }

    /// <summary>
    /// The action and form values a resume entry of an elicitation carries; a cancelled entry cancels. The
    /// elicitation service checks both against the request's fields.
    /// </summary>
    public static (string? Action, IReadOnlyDictionary<string, JsonElement>? Content) ElicitationAnswerOf(AGUIResume resume)
    {
        ArgumentNullException.ThrowIfNull(resume);
        if (string.Equals(resume.Status, ResumeStatus.Cancelled, StringComparison.Ordinal))
        {
            return ("cancel", null);
        }
        if (resume.Payload is not { ValueKind: JsonValueKind.Object } payload)
        {
            return (null, null);
        }

        var action = payload.TryGetProperty("action", out var named) && named.ValueKind == JsonValueKind.String
            ? named.GetString()
            : null;
        Dictionary<string, JsonElement>? content = null;
        if (payload.TryGetProperty("content", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            content = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in values.EnumerateObject())
            {
                content[property.Name] = property.Value.Clone();
            }
        }

        return (action, content);
    }

    private static bool TryParseId(string interruptId, string prefix, out Guid id)
    {
        id = Guid.Empty;
        return interruptId.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(interruptId[prefix.Length..], "N", out id);
    }

    private static JsonObject FieldSchema(NhAssistantElicitationField field)
    {
        var schema = new JsonObject { ["title"] = field.Title };
        if (field.Description is not null)
        {
            schema["description"] = field.Description;
        }

        if (field.Type == NhAssistantElicitationFieldTypes.Enum)
        {
            schema["type"] = "string";
            var values = new JsonArray();
            foreach (var option in field.Options ?? [])
            {
                values.Add(new JsonObject { ["const"] = option.Value, ["title"] = option.Label });
            }
            schema["oneOf"] = values;
            return schema;
        }

        schema["type"] = field.Type;
        if (field.MinLength is { } minLength)
        {
            schema["minLength"] = minLength;
        }
        if (field.MaxLength is { } maxLength)
        {
            schema["maxLength"] = maxLength;
        }
        if (field.Minimum is { } minimum)
        {
            schema["minimum"] = minimum;
        }
        if (field.Maximum is { } maximum)
        {
            schema["maximum"] = maximum;
        }
        return schema;
    }

    private static JsonElement Metadata(string name, NhAssistantMessagePartDto part)
    {
        var value = JsonSerializer.SerializeToElement(part, NhAssistantJsonSerializerContext.Default.NhAssistantMessagePartDto);
        return Element(new JsonObject { [name] = JsonNode.Parse(value.GetRawText()) });
    }

    private static string Timestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    internal static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
