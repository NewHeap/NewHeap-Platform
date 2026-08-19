using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services.Api;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public class NhApiClientTest
{
    [Fact]
    public async Task GetAsync_UsesConfiguredClientAndDeserializesResponse()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri.Should().Be(new Uri("https://api.example.test/items/42"));

            return JsonResponse(HttpStatusCode.OK, """{"id":42,"name":"Sample"}""");
        });
        await using var provider = CreateProvider(handler);
        var service = provider.GetRequiredService<TestApiService>();

        var result = await service.GetAsync("items/42");

        result.Success.Should().BeTrue();
        result.Data.Should().BeEquivalentTo(new TestResponse(42, "Sample"));
    }

    [Fact]
    public async Task PostAsync_SerializesStringEnums()
    {
        string? requestContent = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            requestContent = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(HttpStatusCode.OK, """{"id":7,"name":"Created"}""");
        });
        await using var provider = CreateProvider(handler);
        var service = provider.GetRequiredService<TestApiService>();

        var result = await service.PostAsync(
            "items",
            new TestRequest("New", TestState.Ready));

        result.Success.Should().BeTrue();
        requestContent.Should().Contain("\"state\":\"Ready\"");
    }

    [Fact]
    public async Task GetCollectionAsync_AddsEncodedQParameterAndKeepsExistingQuery()
    {
        Uri? requestUri = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            return JsonResponse(
                HttpStatusCode.OK,
                """{"page":2,"itemsPerPage":25,"totalCount":0,"resultCount":0,"items":[],"orderBy":[],"filter":[],"search":"needle"}""");
        });
        await using var provider = CreateProvider(handler);
        var service = provider.GetRequiredService<TestApiService>();
        var requestModel = new CollectionRequestModel
        {
            Page = 2,
            ItemsPerPage = 25,
            Search = "needle"
        };

        var result = await service.GetCollectionAsync("items?active=true", requestModel);

        result.Success.Should().BeTrue();
        requestUri!.Query.Should().StartWith("?active=true&q=");
        var serializedRequest = Uri.UnescapeDataString(
            requestUri.Query[(requestUri.Query.IndexOf("q=", StringComparison.Ordinal) + 2)..]);
        serializedRequest.Should().Contain("\"page\":2");
        serializedRequest.Should().Contain("\"search\":\"needle\"");
    }

    [Fact]
    public async Task ErrorResponse_AppliesModelStateErrorsToTaskResult()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            HttpStatusCode.BadRequest,
            """{"name":["Name is required."],"":["General error."]}"""));
        await using var provider = CreateProvider(handler);
        var service = provider.GetRequiredService<TestApiService>();

        var result = await service.GetAsync("items/42");

        result.Success.Should().BeFalse();
        result.GetResultItems().Should().ContainSingle(item =>
            item.Name == "name"
            && item.ErrorMessages.Any(error => error.ToString() == "Name is required."));
        result.GetResultItems().Should().ContainSingle(item =>
            item.Name == string.Empty
            && item.ErrorMessages.Any(error => error.ToString() == "General error."));
    }

    [Fact]
    public async Task CustomTokenProvider_AddsBearerToken()
    {
        string? authorization = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return JsonResponse(HttpStatusCode.OK, """{"id":1,"name":"Authorized"}""");
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddNhApiClient<TestApi, StubAccessTokenProvider>(options =>
            {
                options.BaseAddress = new Uri("https://api.example.test/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<TestApiService>();
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<TestApiService>().GetAsync("items/1");

        result.Success.Should().BeTrue();
        authorization.Should().Be("Bearer sample-token");
    }

    [Fact]
    public async Task UsernamePasswordProvider_CachesTokenAcrossConcurrentRequests()
    {
        var authenticationCalls = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            request.RequestUri.Should().Be(new Uri("https://api.example.test/auth/login"));
            Interlocked.Increment(ref authenticationCalls);
            return JsonResponse(
                HttpStatusCode.OK,
                $$"""{"token":"cached-token","validTo":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""");
        });
        var options = new NhApiClientOptions
        {
            BaseAddress = new Uri("https://api.example.test/"),
            Authentication = new NhApiUsernamePasswordAuthenticationOptions
            {
                Endpoint = "auth/login",
                Username = "service-user",
                Password = "secret"
            }
        };
        var provider = new NhUsernamePasswordApiAccessTokenProvider<TestApi>(
            new StubHttpClientFactory(handler, options.BaseAddress),
            new NhApiClientRegistration<TestApi>(options),
            NullLogger<NhUsernamePasswordApiAccessTokenProvider<TestApi>>.Instance);

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => provider.GetAccessTokenAsync()));

        tokens.Should().OnlyContain(token => token == "cached-token");
        authenticationCalls.Should().Be(1);
    }

    [Fact]
    public void AddNhApiClient_RegistersFactoryAndConfiguredBaseAddress()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNhApiClient<TestApi>(options =>
        {
            options.BaseAddress = new Uri("https://api.example.test/root/");
            options.Timeout = TimeSpan.FromSeconds(12);
        });
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<INhApiHttpClientFactory<TestApi>>();
        using var client = factory.CreateHttpClient();

        client.BaseAddress.Should().Be(new Uri("https://api.example.test/root/"));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void ApiConvenienceMethods_AreVirtual()
    {
        var methodNames = new HashSet<string>
        {
            "DoDeleteAsync",
            "DoGetAsync",
            "DoGetCollectionAsync",
            "DoGetResponseAsync",
            "DoPatchAsync",
            "DoPostAsync",
            "DoPutAsync",
            "DoSendResponseAsync"
        };
        var methods = typeof(BaseNhApiService<TestApi>)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => methodNames.Contains(method.Name))
            .ToList();

        methods.Should().NotBeEmpty();
        methods.Should().OnlyContain(method => method.IsVirtual && !method.IsFinal);
    }

    [Fact]
    public async Task DeleteAsync_CanDeserializeTypedResponse()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Delete);
            return JsonResponse(HttpStatusCode.OK, """{"id":42,"name":"Deleted"}""");
        });
        await using var provider = CreateProvider(handler);
        var service = provider.GetRequiredService<TestApiService>();

        var result = await service.DeleteAsync("items/42");

        result.Success.Should().BeTrue();
        result.Data.Should().BeEquivalentTo(new TestResponse(42, "Deleted"));
    }

    [Fact]
    public async Task GetResponseAsync_DisposesResponseContentWithResult()
    {
        var responseStream = new TrackingStream(Encoding.UTF8.GetBytes("download"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(responseStream)
        });
        await using var provider = CreateProvider(handler);
        var service = provider.GetRequiredService<TestApiService>();

        var result = await service.GetResponseAsync("downloads/42");
        result.Success.Should().BeTrue();
        var stream = await result.Data!.ReadAsStreamAsync();
        var buffer = new byte[8];
        await stream.ReadExactlyAsync(buffer);
        Encoding.UTF8.GetString(buffer).Should().Be("download");

        result.Dispose();

        responseStream.IsDisposed.Should().BeTrue();
        var readAfterDispose = () => result.Data.ReadAsStreamAsync();
        await readAfterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetResponseAsync_DisposesFailedResponseBeforeReturning()
    {
        var responseStream = new TrackingStream(
            Encoding.UTF8.GetBytes("""{"errors":["Download failed."]}"""));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(responseStream)
        });
        await using var provider = CreateProvider(handler);
        var service = provider.GetRequiredService<TestApiService>();

        var result = await service.GetResponseAsync("downloads/42");

        result.Success.Should().BeFalse();
        result.Data.Should().BeNull();
        result.AllErrorMessages.Select(error => error.ToString())
            .Should().Contain("Download failed.");
        responseStream.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void AddNhApiClient_BindsConfigurationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:BaseAddress"] = "https://configured.example.test/",
                ["Api:Timeout"] = "00:00:15",
                ["Api:Authentication:Endpoint"] = "/auth/login",
                ["Api:Authentication:Username"] = "service-user",
                ["Api:Authentication:Password"] = "secret"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNhApiClient<TestApi>(configuration.GetSection("Api"));
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<INhApiHttpClientFactory<TestApi>>();
        var registration = provider.GetRequiredService<NhApiClientRegistration<TestApi>>();
        using var client = factory.CreateHttpClient();

        client.BaseAddress.Should().Be(new Uri("https://configured.example.test/"));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        registration.Options.Authentication.Should().NotBeNull();
        registration.Options.Authentication!.Endpoint.Should().Be("/auth/login");
    }

    private static ServiceProvider CreateProvider(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddNhApiClient<TestApi>(options =>
            {
                options.BaseAddress = new Uri("https://api.example.test/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<TestApiService>();
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestApi;

    private enum TestState
    {
        Ready
    }

    private sealed record TestRequest(string Name, TestState State);

    private sealed record TestResponse(int Id, string Name);

    private sealed class TestApiService : BaseNhApiService<TestApi>
    {
        public TestApiService(INhApiHttpClientFactory<TestApi> httpClientFactory)
            : base(NullLogger<TestApiService>.Instance, httpClientFactory)
        {
        }

        public Task<TaskResult<TestResponse>> GetAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            return DoGetAsync<TestResponse>(url, cancellationToken);
        }

        public Task<TaskResult<TestResponse>> PostAsync(
            string url,
            TestRequest request,
            CancellationToken cancellationToken = default)
        {
            return DoPostAsync<TestRequest, TestResponse>(url, request, cancellationToken);
        }

        public Task<TaskResult<CollectionResultModel<TestResponse>>> GetCollectionAsync(
            string url,
            IBaseCollectionRequestModel request,
            CancellationToken cancellationToken = default)
        {
            return DoGetCollectionAsync<TestResponse>(url, request, cancellationToken);
        }

        public Task<TaskResult<TestResponse>> DeleteAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            return DoDeleteAsync<TestResponse>(url, cancellationToken);
        }

        public Task<DisposableTaskResult<NhApiHttpResponse>> GetResponseAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            return DoGetResponseAsync(url, cancellationToken);
        }
    }

    private sealed class StubAccessTokenProvider : INhApiAccessTokenProvider<TestApi>
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("sample-token");
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly Uri _baseAddress;

        public StubHttpClientFactory(HttpMessageHandler handler, Uri baseAddress)
        {
            _handler = handler;
            _baseAddress = baseAddress;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = _baseAddress
            };
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] buffer)
            : base(buffer)
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
