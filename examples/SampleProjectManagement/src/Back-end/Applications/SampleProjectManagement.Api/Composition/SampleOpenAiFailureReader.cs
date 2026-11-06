using System.ClientModel;
using System.Text.Json;
using NewHeap.Platform.AI;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-232: reads the content-free status and error code of a call that the OpenAI-compatible model
/// endpoint refused, so a failed model call reports them next to <c>ai-model-dependency-unavailable</c>
/// (<see cref="NhAiModelProviderFailure"/>) and a retry-eligible profile retries 429 and 5xx answers.
/// The OpenAI client throws <see cref="ClientResultException"/>, a type NewHeap does not reference, so
/// the application that chose the client registers this reader with
/// <c>AddModelProviderFailureReader</c>.
/// </summary>
/// <remarks>
/// The reader takes the status and only the <c>error.code</c> field of OpenAI's error answer, such as
/// <c>invalid_api_key</c>. The error's message can repeat part of the API key or of the request, so it
/// never reads the message or the exception's own text.
/// </remarks>
public sealed class SampleOpenAiFailureReader : INhAiModelProviderFailureReader
{
    public NhAiModelProviderError? Read(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Status 0 means the endpoint did not answer: the inner HttpRequestException tells why.
        if (exception is not ClientResultException { Status: > 0 } refused)
        {
            return null;
        }

        return new NhAiModelProviderError(refused.Status, ReadErrorCode(refused));
    }

    private static string? ReadErrorCode(ClientResultException exception)
    {
        BinaryData? content;
        try
        {
            content = exception.GetRawResponse()?.Content;
        }
        catch (InvalidOperationException)
        {
            // A streamed answer whose body was never buffered: the status alone remains.
            return null;
        }

        if (content is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToMemory());
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                return code.GetString();
            }
        }
        catch (JsonException)
        {
            // An endpoint that answers without OpenAI's error shape names no code.
        }

        return null;
    }
}
