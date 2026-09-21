using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    INhAiBridgeBodySerializer bodySerializer,
    IServiceProvider requestServices,
    IEnumerable<INhAiBridgeTrustedQueryBindingProvider> trustedQueryBindingProviders,
    NhAiMvcBridgeRuntimeSettings settings,
    INhAiCallerCredentialAccessor credentialAccessor,
    IHttpContextAccessor httpContextAccessor,
    ILogger<NhAiMvcBridgeExecutor> logger) : INhAiMvcBridgeExecutor, INhAiMvcBridgeShapingExecutor
{
    private static readonly JsonSerializerOptions MeasureOptions = new(JsonSerializerDefaults.Web);

    public Task<TaskResult<NhAiBridgeResponse>> ExecuteAsync(
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        JsonElement input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(action, descriptor, input, context, null, cancellationToken);
    }

    /// <summary>
    /// Executes a gateway operation: count requests use <see cref="INhAiBridgeConventions.BuildCountRequest"/>
    /// and a successful body is read up to the shaper's response limit and shaped before it is bounded.
    /// </summary>
    public Task<TaskResult<NhAiBridgeResponse>> ExecuteShapedAsync(
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        JsonElement input,
        NhAiInvocationContext context,
        NhAiBridgeResultShaper shaper,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shaper);
        return ExecuteCoreAsync(action, descriptor, input, context, shaper, cancellationToken);
    }

    private async Task<TaskResult<NhAiBridgeResponse>> ExecuteCoreAsync(
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        JsonElement input,
        NhAiInvocationContext context,
        NhAiBridgeResultShaper? shaper,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);

        NhAiBridgeHttpRequest request;
        try
        {
            request = shaper?.CountOnly == true
                ? conventions.BuildCountRequest(action, input)
                : conventions.BuildRequest(action, input);
            await ApplyTrustedQueryBindingsAsync(
                request,
                action,
                descriptor,
                context,
                trustedQueryBindingProviders,
                cancellationToken);
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
            if (failureCode is null && shaper is not null)
            {
                var (contentType, kept, total) = await ReadBodyAsync(response, shaper.MaxResponseBytes, cancellationToken);
                return shaper.Shape(status, contentType, kept, total, descriptor.MaxResultBytes);
            }
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

    private static async ValueTask ApplyTrustedQueryBindingsAsync(
        NhAiBridgeHttpRequest request,
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        IEnumerable<INhAiBridgeTrustedQueryBindingProvider> providers,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<(NhAiBridgeTrustedQueryBindingKind Kind, string Name), (string Value, string Operator)>();
        foreach (var provider in providers)
        {
            var bindings = await provider.GetBindingsAsync(action, descriptor, context, cancellationToken);
            foreach (var binding in bindings)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(binding.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(binding.ScopeKey);
                if (!context.TryGetScopeValue(binding.ScopeKey, out var value))
                {
                    throw new NhAiBridgeInputException(
                        $"The trusted query binding '{binding.Name}' requires invocation scope '{binding.ScopeKey}'.");
                }
                var key = (binding.Kind, binding.Name);
                if (resolved.TryGetValue(key, out var existing)
                    && (!string.Equals(existing.Value, value, StringComparison.Ordinal)
                        || !string.Equals(existing.Operator, binding.Operator, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new NhAiBridgeInputException(
                        $"The trusted query binding '{binding.Name}' was registered more than once with different values.");
                }
                resolved[key] = (value, binding.Operator);
            }
        }

        foreach (var binding in resolved)
        {
            if (binding.Key.Kind == NhAiBridgeTrustedQueryBindingKind.QueryValue)
            {
                RemoveQuery(request.Query, binding.Key.Name);
                request.Query.Add(new(binding.Key.Name, binding.Value.Value));
                continue;
            }
            if (binding.Key.Kind == NhAiBridgeTrustedQueryBindingKind.CollectionFilter)
            {
                AddTrustedCollectionFilter(
                    request,
                    binding.Key.Name,
                    binding.Value.Operator,
                    binding.Value.Value);
                continue;
            }
            throw new NhAiBridgeInputException(
                $"The trusted query binding '{binding.Key.Name}' has an unsupported kind.");
        }
    }

    private static void AddTrustedCollectionFilter(
        NhAiBridgeHttpRequest request,
        string key,
        string filterOperator,
        string value)
    {
        var existing = request.Query
            .Where(item => string.Equals(item.Key, "filter", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .SingleOrDefault();
        RemoveQuery(request.Query, "filter");

        JsonArray filters;
        try
        {
            filters = existing is null ? [] : JsonNode.Parse(existing)?.AsArray() ?? [];
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new NhAiBridgeInputException("The collection filter query could not be combined with trusted bindings.");
        }
        foreach (var item in filters.ToArray())
        {
            if (item is JsonObject filter
                && string.Equals(filter["key"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))
            {
                filters.Remove(item);
            }
        }
        filters.Add(new JsonObject
        {
            ["key"] = key,
            ["operator"] = filterOperator,
            ["value"] = value
        });
        request.Query.Add(new("filter", filters.ToJsonString()));
    }

    private static void RemoveQuery(IList<KeyValuePair<string, string>> query, string name)
    {
        for (var index = query.Count - 1; index >= 0; index--)
        {
            if (string.Equals(query[index].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                query.RemoveAt(index);
            }
        }
    }

    private async Task<HttpRequestMessage> CreateMessageAsync(
        NhAiBridgeHttpRequest request,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.SelfBaseUrl!.EndsWith('/') ? settings.SelfBaseUrl : settings.SelfBaseUrl + "/";
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
                bodySerializer.Serialize(request.Body, requestServices),
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
        var (contentType, kept, total) = await ReadBodyAsync(response, maxResultBytes, cancellationToken);
        return CreateEnvelope((int)response.StatusCode, contentType, kept, total, maxResultBytes);
    }

    /// <summary>Keeps at most <paramref name="keepBytes"/> bytes and counts the rest up to a bound.</summary>
    private static async Task<(string? ContentType, byte[] Kept, long TotalBytes)> ReadBodyAsync(
        HttpResponseMessage response,
        int keepBytes,
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
            var keep = (int)Math.Max(0, Math.Min(read, keepBytes - kept.Length));
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

        return (contentType, kept.ToArray(), total);
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
