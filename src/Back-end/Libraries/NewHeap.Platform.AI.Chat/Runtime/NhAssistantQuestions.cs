using System.Text.Json;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// A question that passed the checks of <c>ask_question</c>, before the interceptor records it with
/// its call: the question, its choices and the person who answers it.
/// </summary>
internal sealed record NhAssistantAskedQuestion(
    string Question,
    IReadOnlyList<string> Choices,
    string AddresseeActorId);

/// <summary>
/// The questions an agent asks with <c>ask_question</c> in a person's chat (LS21): the function's
/// description and schema, the checks of a question, the view of a question and the results the
/// model reads. A question lives in its call (<see cref="AssistantToolInvocation"/>), whose status
/// says whether it waits, and in the <c>question</c> part of the agent's message, which keeps the
/// question, its choices, its addressee and the end of its wait; it needs no storage of its own.
/// </summary>
internal static class NhAssistantQuestions
{
    /// <summary>
    /// The code of an answered question's result.
    /// </summary>
    public const string AnsweredCode = "answered";

    /// <summary>
    /// What the model reads about <c>ask_question</c>, with the names of the people of a shared chat.
    /// </summary>
    public static string DescriptionOf(NhAssistantSpeakers? speakers)
    {
        var description = "Asks a person of this conversation a question with "
            + NhAssistantQuestionLimits.MinChoices + " to " + NhAssistantQuestionLimits.MaxChoices
            + " choices and waits for the answer, which is this call's result. Use it when you need a decision "
            + "or a missing fact that only a person can give and the possible answers are known; ask one "
            + "question at a time and do not use it for open questions. By default the person whose turn "
            + "it is answers.";
        return speakers is null
            ? description
            : description + " In this shared conversation `addressee` may name another person: "
                + string.Join(", ", speakers.People) + ".";
    }

    /// <summary>
    /// The JSON schema of <c>ask_question</c>; <c>addressee</c> only in a shared chat, with the names of
    /// its people.
    /// </summary>
    public static string SchemaOf(NhAssistantSpeakers? speakers)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["question"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "The question, at most " + NhAssistantQuestionLimits.MaxQuestionLength + " characters."
            },
            ["choices"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                ["minItems"] = NhAssistantQuestionLimits.MinChoices,
                ["maxItems"] = NhAssistantQuestionLimits.MaxChoices,
                ["description"] = "The distinct answers to choose from, each at most "
                    + NhAssistantQuestionLimits.MaxChoiceLength + " characters."
            }
        };
        if (speakers is not null)
        {
            properties["addressee"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = speakers.People.ToArray(),
                ["description"] = "The person who answers; leave it out for the person whose turn it is."
            };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new[] { "question", "choices" }
        });
    }

    /// <summary>
    /// Checks the arguments of an <c>ask_question</c> call: a question within the bounds, distinct
    /// choices within the bounds and, in a shared chat, an addressee who is a person of the
    /// conversation. Returns the question, or why it was refused for the model.
    /// </summary>
    public static (NhAssistantAskedQuestion? Question, string? Refusal) Check(
        AIFunctionArguments arguments,
        NhAssistantSpeakers? speakers,
        string currentActorId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentActorId);
        var question = TextOf(arguments, "question")?.Trim();
        if (string.IsNullOrEmpty(question) || question.Length > NhAssistantQuestionLimits.MaxQuestionLength)
        {
            return (null, "Nothing was asked: give a question of 1 to " + NhAssistantQuestionLimits.MaxQuestionLength + " characters.");
        }

        var choices = ChoicesOf(arguments);
        if (choices is null
            || choices.Count is < NhAssistantQuestionLimits.MinChoices or > NhAssistantQuestionLimits.MaxChoices
            || choices.Any(choice => choice.Length is 0 or > NhAssistantQuestionLimits.MaxChoiceLength)
            || choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != choices.Count)
        {
            return (null, "Nothing was asked: give " + NhAssistantQuestionLimits.MinChoices + " to "
                + NhAssistantQuestionLimits.MaxChoices + " distinct choices of at most "
                + NhAssistantQuestionLimits.MaxChoiceLength + " characters each.");
        }

        var addressee = currentActorId;
        var named = TextOf(arguments, "addressee");
        if (speakers is not null && !string.IsNullOrWhiteSpace(named))
        {
            // In a shared chat the model names people as the conversation shows them; a name nobody
            // has is refused rather than asked of the wrong person.
            if (speakers.ActorOf(named) is not { } actorId)
            {
                return (null, "Nothing was asked: the addressee is none of the people of this conversation ("
                    + string.Join(", ", speakers.People) + ").");
            }

            addressee = actorId;
        }

        return (new NhAssistantAskedQuestion(question, choices, addressee), null);
    }

    /// <summary>
    /// The <c>question</c> part of the agent's message for a question recorded with its call.
    /// </summary>
    public static NhAssistantStoredPart PartOf(NhAssistantAskedQuestion asked, Guid invocationId, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(asked);
        return new NhAssistantStoredPart(
            NhAssistantStoredPart.QuestionType,
            Text: asked.Question,
            InvocationId: invocationId,
            Choices: asked.Choices,
            AddresseeActorId: asked.AddresseeActorId,
            ExpiresAt: expiresAt);
    }

    /// <summary>
    /// The question as the conversation shows it, from its part and its call; a waiting question whose
    /// wait <paramref name="now"/> of the application's clock reached reads as expired.
    /// </summary>
    public static NhAssistantQuestionView? ViewOf(NhAssistantStoredPart part, AssistantToolInvocation invocation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(invocation);
        if (part is not { Type: NhAssistantStoredPart.QuestionType, Text: { } question, Choices: { } choices, AddresseeActorId: { } addressee, ExpiresAt: { } expiresAt })
        {
            return null;
        }

        var status = invocation.Status switch
        {
            NhAssistantToolCallStatuses.AwaitingAnswer => now >= expiresAt ? NhAssistantQuestionStatuses.Expired : NhAssistantQuestionStatuses.Pending,
            NhAssistantToolCallStatuses.Succeeded => NhAssistantQuestionStatuses.Answered,
            NhAssistantToolCallStatuses.NotExecuted => NhAssistantQuestionStatuses.Cancelled,
            _ => NhAssistantQuestionStatuses.Expired
        };
        return new NhAssistantQuestionView(invocation.Id, question, choices, addressee, expiresAt, status)
        {
            AnswerIndex = status == NhAssistantQuestionStatuses.Answered ? AnswerIndexOf(invocation) : null,
            ClosedAt = invocation.CompletedAt,
            TurnId = invocation.TurnId
        };
    }

    /// <summary>
    /// The result of an answered question as the model reads it: the chosen answer, its index and the
    /// name of the person who chose it.
    /// </summary>
    public static JsonElement AnswerResult(NhAssistantQuestionView question, int choice, string? answeredBy)
    {
        ArgumentNullException.ThrowIfNull(question);
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = true,
                ["code"] = AnsweredCode,
                ["choice"] = choice,
                ["answer"] = question.Choices[choice],
                ["answeredBy"] = answeredBy
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// The result the model reads while the question waits; the turn ends with it and continues with
    /// the answer instead.
    /// </summary>
    public static JsonElement WaitingResult()
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = true,
                ["code"] = "waiting",
                ["message"] = "The question was posted; the turn waits for the answer."
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// The result of a question that was not posted.
    /// </summary>
    public static JsonElement Refused(string message)
    {
        return Closed(NhAssistantErrorCodes.QuestionInvalid, message);
    }

    /// <summary>
    /// The stored result of a question that closed without an answer: expired or cancelled.
    /// </summary>
    public static JsonElement Closed(string code, string message)
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

    /// <summary>
    /// The zero-based index of the answer an answered question's call keeps as its result.
    /// </summary>
    public static int? AnswerIndexOf(AssistantToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.ResultJson is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(invocation.ResultJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("choice", out var choice)
                   && choice.TryGetInt32(out var index)
                ? index
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TextOf(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            _ => value.ToString()
        };
    }

    private static List<string>? ChoicesOf(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue("choices", out var value) || value is null)
        {
            return null;
        }

        var choices = new List<string>();
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }
                    choices.Add(item.GetString()!.Trim());
                }
                break;
            case IEnumerable<object?> items:
                foreach (var item in items)
                {
                    var text = item switch
                    {
                        string plain => plain,
                        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                        _ => null
                    };
                    if (text is null)
                    {
                        return null;
                    }
                    choices.Add(text.Trim());
                }
                break;
            default:
                return null;
        }

        return choices;
    }
}
