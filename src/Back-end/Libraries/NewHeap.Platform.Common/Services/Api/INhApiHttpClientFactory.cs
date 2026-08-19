using NewHeap.Platform.Common.Models.Options;
using System.Text.Json;

namespace NewHeap.Platform.Common.Services.Api;

/// <summary>
/// Creates configured HTTP clients for one logical target API.
/// </summary>
public interface INhApiHttpClientFactory<TApi>
    where TApi : class
{
    JsonSerializerOptions JsonSerializerOptions { get; }

    HttpClient CreateHttpClient();
}

internal sealed class NhApiHttpClientFactory<TApi> : INhApiHttpClientFactory<TApi>
    where TApi : class
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NhApiHttpClientFactory(
        IHttpClientFactory httpClientFactory,
        NhApiClientRegistration<TApi> registration)
    {
        _httpClientFactory = httpClientFactory;
        JsonSerializerOptions = registration.Options.JsonSerializerOptions;
    }

    public JsonSerializerOptions JsonSerializerOptions { get; }

    public HttpClient CreateHttpClient()
    {
        return _httpClientFactory.CreateClient(NhApiClientNames.GetClientName<TApi>());
    }
}

internal sealed record NhApiClientRegistration<TApi>(NhApiClientOptions Options)
    where TApi : class;

internal static class NhApiClientNames
{
    public static string GetClientName<TApi>() where TApi : class
    {
        return $"NewHeap.Platform.ApiClient.{typeof(TApi).AssemblyQualifiedName}";
    }

    public static string GetAuthenticationClientName<TApi>() where TApi : class
    {
        return $"{GetClientName<TApi>()}.Authentication";
    }
}
