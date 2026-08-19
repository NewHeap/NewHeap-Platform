using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NewHeap.Platform.Common.Services.Api;

public interface IBaseNhApiService
{
}

/// <summary>
/// Base implementation for services that call one configured target API.
/// </summary>
public abstract class BaseNhApiService<TApi> : IBaseNhApiService
    where TApi : class
{
    private const int MaximumErrorBodyLength = 2_000;

    private readonly ILogger _logger;
    private readonly INhApiHttpClientFactory<TApi> _httpClientFactory;

    protected BaseNhApiService(
        ILogger logger,
        INhApiHttpClientFactory<TApi> httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected virtual Task<TaskResult<TResponse>> DoGetAsync<TResponse>(
        string url,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<object, TResponse>(
            HttpMethod.Get,
            url,
            requestModel: null,
            cancellationToken);
    }

    protected virtual Task<TaskResult<CollectionResultModel<TResponse>>> DoGetCollectionAsync<TResponse>(
        string url,
        IBaseCollectionRequestModel? requestModel = null,
        CancellationToken cancellationToken = default)
    {
        if (requestModel != null)
        {
            var serializedRequest = JsonSerializer.Serialize(
                requestModel,
                requestModel.GetType(),
                _httpClientFactory.JsonSerializerOptions);
            url = AddQueryParameter(url, "q", serializedRequest);
        }

        return DoGetAsync<CollectionResultModel<TResponse>>(url, cancellationToken);
    }

    protected virtual Task<TaskResult<TResponse>> DoPostAsync<TRequest, TResponse>(
        string url,
        TRequest requestModel,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<TRequest, TResponse>(
            HttpMethod.Post,
            url,
            requestModel,
            cancellationToken);
    }

    protected virtual Task<TaskResult<TResponse>> DoPutAsync<TRequest, TResponse>(
        string url,
        TRequest requestModel,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<TRequest, TResponse>(
            HttpMethod.Put,
            url,
            requestModel,
            cancellationToken);
    }

    protected virtual Task<TaskResult<TResponse>> DoPatchAsync<TRequest, TResponse>(
        string url,
        TRequest requestModel,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<TRequest, TResponse>(
            HttpMethod.Patch,
            url,
            requestModel,
            cancellationToken);
    }

    protected virtual Task<TaskResult> DoPostAsync<TRequest>(
        string url,
        TRequest requestModel,
        CancellationToken cancellationToken = default)
    {
        return SendWithoutResponseAsync(
            HttpMethod.Post,
            url,
            requestModel,
            cancellationToken);
    }

    protected virtual Task<TaskResult> DoPutAsync<TRequest>(
        string url,
        TRequest requestModel,
        CancellationToken cancellationToken = default)
    {
        return SendWithoutResponseAsync(
            HttpMethod.Put,
            url,
            requestModel,
            cancellationToken);
    }

    protected virtual Task<TaskResult> DoPatchAsync<TRequest>(
        string url,
        TRequest requestModel,
        CancellationToken cancellationToken = default)
    {
        return SendWithoutResponseAsync(
            HttpMethod.Patch,
            url,
            requestModel,
            cancellationToken);
    }

    protected virtual Task<TaskResult> DoDeleteAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return SendWithoutResponseAsync<object>(
            HttpMethod.Delete,
            url,
            requestModel: null,
            cancellationToken);
    }

    protected virtual Task<TaskResult<TResponse>> DoDeleteAsync<TResponse>(
        string url,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<object, TResponse>(
            HttpMethod.Delete,
            url,
            requestModel: null,
            cancellationToken);
    }

    /// <summary>
    /// Sends a GET request without buffering or deserializing its response.
    /// The returned result must be disposed by the caller.
    /// </summary>
    protected virtual Task<DisposableTaskResult<NhApiHttpResponse>> DoGetResponseAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return DoSendResponseAsync<object>(
            HttpMethod.Get,
            url,
            requestModel: null,
            cancellationToken);
    }

    /// <summary>
    /// Sends a request without buffering or deserializing its response.
    /// The returned result must be disposed by the caller.
    /// </summary>
    protected virtual async Task<DisposableTaskResult<NhApiHttpResponse>> DoSendResponseAsync<TRequest>(
        HttpMethod method,
        string url,
        TRequest? requestModel = default,
        CancellationToken cancellationToken = default)
    {
        var result = new DisposableTaskResult<NhApiHttpResponse>();
        HttpClient? httpClient = null;
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;

        try
        {
            request = CreateRequest(method, url, requestModel);
            httpClient = _httpClientFactory.CreateHttpClient();
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await ApplyErrorResponseAsync(response, result, cancellationToken).ConfigureAwait(false);
                return result;
            }

            result.Data = new NhApiHttpResponse(httpClient, request, response);
            httpClient = null;
            request = null;
            response = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Error sending {Method} request to API client {ApiClient} at {Url}.",
                method,
                typeof(TApi).FullName,
                url);
            result.WithKeylessError("Error sending request to the target API.");
        }
        finally
        {
            response?.Dispose();
            request?.Dispose();
            httpClient?.Dispose();
        }

        return result;
    }

    private async Task<TaskResult<TResponse>> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string url,
        TRequest? requestModel,
        CancellationToken cancellationToken)
    {
        var result = new TaskResult<TResponse>();

        try
        {
            using var request = CreateRequest(method, url, requestModel);
            using var httpClient = _httpClientFactory.CreateHttpClient();
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await ApplyErrorResponseAsync(response, result, cancellationToken).ConfigureAwait(false);
                return result;
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return result.WithKeylessError("The target API returned no response body.");
            }

            var responseContent = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return result.WithKeylessError("The target API returned an empty response body.");
            }

            if (typeof(TResponse) == typeof(string))
            {
                result.Data = (TResponse)(object)responseContent;
                return result;
            }

            var data = JsonSerializer.Deserialize<TResponse>(
                responseContent,
                _httpClientFactory.JsonSerializerOptions);

            if (data == null)
            {
                return result.WithKeylessError("Failed to deserialize the target API response.");
            }

            result.Data = data;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Error sending {Method} request to API client {ApiClient} at {Url}.",
                method,
                typeof(TApi).FullName,
                url);
            result.WithKeylessError("Error sending request to the target API.");
        }

        return result;
    }

    private async Task<TaskResult> SendWithoutResponseAsync<TRequest>(
        HttpMethod method,
        string url,
        TRequest? requestModel,
        CancellationToken cancellationToken)
    {
        var result = new TaskResult();

        try
        {
            using var request = CreateRequest(method, url, requestModel);
            using var httpClient = _httpClientFactory.CreateHttpClient();
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await ApplyErrorResponseAsync(response, result, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Error sending {Method} request to API client {ApiClient} at {Url}.",
                method,
                typeof(TApi).FullName,
                url);
            result.WithKeylessError("Error sending request to the target API.");
        }

        return result;
    }

    protected virtual HttpRequestMessage CreateRequest<TRequest>(
        HttpMethod method,
        string url,
        TRequest? requestModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var request = new HttpRequestMessage(method, url);
        if (requestModel != null)
        {
            request.Content = JsonContent.Create(
                requestModel,
                options: _httpClientFactory.JsonSerializerOptions);
        }

        return request;
    }

    protected virtual async Task ApplyErrorResponseAsync(
        HttpResponseMessage response,
        TaskResult result,
        CancellationToken cancellationToken)
    {
        var responseContent = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(responseContent))
        {
            TryApplyJsonErrors(responseContent, result);
        }

        if (result.GetResultItems().Count > 0)
        {
            return;
        }

        var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            result.WithKeylessError($"The target API returned {status}.");
            return;
        }

        var safeResponseContent = responseContent.Length <= MaximumErrorBodyLength
            ? responseContent
            : $"{responseContent[..MaximumErrorBodyLength]}…";
        result.WithKeylessError($"The target API returned {status}: {safeResponseContent}");
    }

    private static void TryApplyJsonErrors(string responseContent, TaskResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(responseContent);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var root = document.RootElement;
            if (TryGetPropertyIgnoreCase(root, "errors", out var errors))
            {
                ApplyErrorElement(errors, string.Empty, result);
            }

            if (result.GetResultItems().Count == 0 && LooksLikeModelState(root))
            {
                foreach (var property in root.EnumerateObject())
                {
                    ApplyErrorElement(property.Value, property.Name, result);
                }
            }

            if (result.GetResultItems().Count == 0
                && TryGetPropertyIgnoreCase(root, "detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                result.WithKeylessError(detail.GetString()!);
            }

            if (result.GetResultItems().Count == 0
                && TryGetPropertyIgnoreCase(root, "title", out var title)
                && title.ValueKind == JsonValueKind.String)
            {
                result.WithKeylessError(title.GetString()!);
            }
        }
        catch (JsonException)
        {
            // The caller falls back to a bounded plain-text error.
        }
    }

    private static bool LooksLikeModelState(JsonElement root)
    {
        var properties = root.EnumerateObject().ToList();
        return properties.Count > 0
            && properties.All(property =>
                property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.String);
    }

    private static void ApplyErrorElement(
        JsonElement element,
        string name,
        TaskResult result)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var message = element.GetString();
            if (!string.IsNullOrWhiteSpace(message))
            {
                result.AddError(name, message);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    result.AddError(name, item.GetString()!);
                }
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ApplyErrorElement(property.Value, property.Name, result);
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string AddQueryParameter(string url, string name, string value)
    {
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var urlWithoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var separator = urlWithoutFragment.Contains('?')
            ? urlWithoutFragment.EndsWith('?') || urlWithoutFragment.EndsWith('&') ? string.Empty : "&"
            : "?";

        return $"{urlWithoutFragment}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}{fragment}";
    }
}
