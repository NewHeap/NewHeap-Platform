using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Executes one bridged action through the application's own HTTP pipeline as the calling user.
/// </summary>
public interface INhAiMvcBridgeExecutor
{
    Task<TaskResult<NhAiBridgeResponse>> ExecuteAsync(
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        JsonElement input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Names shared by the bridge and its consumers.
/// </summary>
public static class NhAiMvcBridgeDefaults
{
    /// <summary>The named <see cref="HttpClient"/> used for self-HTTP calls.</summary>
    public const string HttpClientName = "newheap-ai-bridge";

    /// <summary>The correlation header carrying the invocation id.</summary>
    public const string InvocationHeaderName = "X-NewHeap-AI-Invocation";

    public const string IdempotencyKeyHeaderName = "Idempotency-Key";

    /// <summary>The hint returned with a truncated response body.</summary>
    public const string TruncationHint = "Use paging or filters to reduce the result.";

    /// <summary>The upper bound of response bytes the bridge reads to report <c>bodyBytes</c>.</summary>
    public const int MaxCountedResponseBytes = 16 * 1024 * 1024;
}

/// <summary>
/// The default self-HTTP executor. It forwards only <c>Authorization</c>,
/// <c>Accept-Language</c>, <c>Idempotency-Key</c> and <c>X-NewHeap-AI-Invocation</c>, bounds
/// the response to the descriptor's result limit and maps HTTP failures to bridge failure
/// codes. Response bodies and tokens are never logged or placed in failure messages.
/// </summary>
internal sealed partial class NhAiMvcBridgeExecutor(
    IHttpClientFactory httpClientFactory,
    INhAiBridgeConventions conventions,
    NhAiMvcBridgeOptions options,
    INhAiCallerCredentialAccessor credentialAccessor,
    IHttpContextAccessor httpContextAccessor,
    ILogger<NhAiMvcBridgeExecutor> logger) : INhAiMvcBridgeExecutor
{
    private static readonly JsonSerializerOptions MeasureOptions = new(JsonSerializerDefaults.Web);

    public async Task<TaskResult<NhAiBridgeResponse>> ExecuteAsync(
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        JsonElement input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);

        NhAiBridgeHttpRequest request;
        try
        {
            request = conventions.BuildRequest(action, input);
        }
        catch (NhAiBridgeInputException exception)
        {
            LogInputRejected(logger, descriptor.Id, context.InvocationId);
            return TaskResult<NhAiBridgeResponse>.Failed(NhAiBridgeFailureCodes.Validation, exception.Message);
        }

        using var message = await CreateMessageAsync(request, descriptor, context, cancellationToken);
        var client = httpClientFactory.CreateClient(NhAiMvcBridgeDefaults.HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            LogTransportFailure(logger, descriptor.Id, context.InvocationId, exception.GetType().Name);
            return TaskResult<NhAiBridgeResponse>.Failed(
                NhAiBridgeFailureCodes.Upstream,
                "The API could not be reached.");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            LogResponse(logger, descriptor.Id, context.InvocationId, status);
            var failureCode = MapFailure(response.StatusCode);
            if (failureCode is null || failureCode == NhAiBridgeFailureCodes.Validation)
            {
                var envelope = await ReadEnvelopeAsync(response, descriptor.MaxResultBytes, cancellationToken);
                if (failureCode is null)
                {
                    return TaskResult<NhAiBridgeResponse>.Succeeded(envelope);
                }
                return TaskResult<NhAiBridgeResponse>
                    .Failed(NhAiBridgeFailureCodes.Validation, "The API rejected the request as invalid.")
                    .WithData(envelope);
            }

            return TaskResult<NhAiBridgeResponse>.Failed(failureCode, FailureMessage(failureCode));
        }
    }

    private async Task<HttpRequestMessage> CreateMessageAsync(
        NhAiBridgeHttpRequest request,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        var baseUrl = options.SelfBaseUrl!.EndsWith('/') ? options.SelfBaseUrl : options.SelfBaseUrl + "/";
        var message = new HttpRequestMessage(
            new HttpMethod(request.Method),
            new Uri(new Uri(baseUrl, UriKind.Absolute), request.BuildRelativeUri()));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var token = await credentialAccessor.GetBearerTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var acceptLanguage = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.AcceptLanguage].ToString();
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            message.Headers.TryAddWithoutValidation(HeaderNames.AcceptLanguage, acceptLanguage);
        }
        if (descriptor.Effect != NhAiToolEffect.ReadOnly && !string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            message.Headers.TryAddWithoutValidation(NhAiMvcBridgeDefaults.IdempotencyKeyHeaderName, context.IdempotencyKey);
        }
        message.Headers.TryAddWithoutValidation(
            NhAiMvcBridgeDefaults.InvocationHeaderName,
            context.InvocationId.ToString("D"));

        if (request.FormFiles.Count > 0)
        {
            var multipart = new MultipartFormDataContent();
            foreach (var field in request.FormFields)
            {
                multipart.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
            }
            foreach (var file in request.FormFiles)
            {
                var content = new ByteArrayContent(file.Content);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
                multipart.Add(content, file.Name, file.FileName);
            }
            message.Content = multipart;
        }
        else if (request.FormFields.Count > 0)
        {
            message.Content = new FormUrlEncodedContent(request.FormFields);
        }
        else if (request.Body is not null)
        {
            message.Content = new StringContent(
                conventions.SerializeBody(request.Body),
                Encoding.UTF8,
                "application/json");
        }
        return message;
    }

    private static async Task<NhAiBridgeResponse> ReadEnvelopeAsync(
        HttpResponseMessage response,
        int maxResultBytes,
        CancellationToken cancellationToken)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var keep = (int)Math.Max(0, Math.Min(read, maxResultBytes - kept.Length));
            if (keep > 0)
            {
                kept.Write(buffer, 0, keep);
            }
            total += read;
            if (total >= NhAiMvcBridgeDefaults.MaxCountedResponseBytes)
            {
                break;
            }
        }

        return CreateEnvelope((int)response.StatusCode, contentType, kept.ToArray(), total, maxResultBytes);
    }

    internal static NhAiBridgeResponse CreateEnvelope(
        int status,
        string? contentType,
        byte[] kept,
        long totalBytes,
        int maxResultBytes)
    {
        var complete = kept.LongLength == totalBytes;
        if (totalBytes == 0)
        {
            return new NhAiBridgeResponse { Status = status, ContentType = contentType, BodyBytes = 0 };
        }

        var isJson = contentType is not null
            && (contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
        var isText = isJson || (contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isText)
        {
            return new NhAiBridgeResponse
            {
                Status = status,
                ContentType = contentType,
                BodyBytes = totalBytes,
                Hint = "The response has binary content that the bridge does not return."
            };
        }

        var text = Encoding.UTF8.GetString(kept);
        if (complete)
        {
            JsonElement? body = null;
            if (isJson)
            {
                try
                {
                    using var document = JsonDocument.Parse(kept);
                    body = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    body = null;
                }
            }
            body ??= JsonSerializer.SerializeToElement(text, MeasureOptions);

            var envelope = new NhAiBridgeResponse
            {
                Status = status,
                ContentType = contentType,
                Body = body,
                BodyBytes = totalBytes
            };
            if (Fits(envelope, maxResultBytes))
            {
                return envelope;
            }
        }

        return CreateTruncatedEnvelope(status, contentType, text, totalBytes, maxResultBytes);
    }

    private static NhAiBridgeResponse CreateTruncatedEnvelope(
        int status,
        string? contentType,
        string text,
        long totalBytes,
        int maxResultBytes)
    {
        var length = text.Length;
        while (true)
        {
            var fragment = text[..length];
            if (length > 0 && char.IsHighSurrogate(fragment[^1]))
            {
                fragment = fragment[..^1];
            }

            var envelope = new NhAiBridgeResponse
            {
                Status = status,
                ContentType = contentType,
                Truncated = true,
                BodyBytes = totalBytes,
                BodyText = fragment,
                Hint = NhAiMvcBridgeDefaults.TruncationHint
            };
            var size = Measure(envelope);
            if (size <= maxResultBytes || length == 0)
            {
                return length == 0 && size > maxResultBytes
                    ? envelope with { BodyText = null, Hint = null }
                    : envelope;
            }

            // Shrink proportionally to the overshoot; escaping can make characters wider than one byte.
            var overshoot = size - maxResultBytes;
            var ratio = (double)fragment.Length / Math.Max(1, size);
            length = Math.Max(0, fragment.Length - (int)Math.Ceiling(overshoot * Math.Max(ratio, 0.15)) - 1);
        }
    }

    private static bool Fits(NhAiBridgeResponse envelope, int maxResultBytes)
    {
        return Measure(envelope) <= maxResultBytes;
    }

    private static int Measure(NhAiBridgeResponse envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(envelope, MeasureOptions).Length;
    }

    private static string? MapFailure(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        if (status is >= 200 and < 300)
        {
            return null;
        }
        return status switch
        {
            400 or 422 => NhAiBridgeFailureCodes.Validation,
            401 => NhAiBridgeFailureCodes.Unauthenticated,
            403 => NhAiBridgeFailureCodes.Forbidden,
            404 => NhAiBridgeFailureCodes.NotFound,
            409 => NhAiBridgeFailureCodes.Conflict,
            408 or 504 => NhAiBridgeFailureCodes.Timeout,
            _ => NhAiBridgeFailureCodes.Upstream
        };
    }

    private static string FailureMessage(string code)
    {
        return code switch
        {
            NhAiBridgeFailureCodes.Unauthenticated => "The API did not accept the caller's credentials.",
            NhAiBridgeFailureCodes.Forbidden => "The API denied access to this operation.",
            NhAiBridgeFailureCodes.NotFound => "The API did not find the requested resource.",
            NhAiBridgeFailureCodes.Conflict => "The API reported a conflict with the current state.",
            NhAiBridgeFailureCodes.Timeout => "The API call did not complete within the tool timeout.",
            _ => "The API call failed."
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "AI bridge tool {ToolId} returned HTTP {StatusCode} for invocation {InvocationId}.")]
    private static partial void LogResponse(ILogger logger, string toolId, Guid invocationId, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "AI bridge tool {ToolId} could not reach the API for invocation {InvocationId} ({FailureType}).")]
    private static partial void LogTransportFailure(ILogger logger, string toolId, Guid invocationId, string failureType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "AI bridge tool {ToolId} rejected its input for invocation {InvocationId}.")]
    private static partial void LogInputRejected(ILogger logger, string toolId, Guid invocationId);
}
