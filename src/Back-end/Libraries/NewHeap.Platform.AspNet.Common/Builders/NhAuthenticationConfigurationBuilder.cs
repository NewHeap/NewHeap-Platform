using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Authentication;

namespace NewHeap.Platform.AspNet.Common.Builders;

public class NhAuthenticationConfigurationBuilder
{
    private readonly List<IAuthenticationEndpoint> _endpoints = [];
    private readonly List<Type> _diEndpoints = [];

    public NhAuthenticationConfigurationBuilder AddAuthenticationEndpoint(string pattern, HttpMethod method,
        RequestDelegate handler)
    {
        _endpoints.Add(new AuthenticationEndpoint { Pattern = pattern, Method = method, Handler = handler });

        return this;
    }

    public NhAuthenticationConfigurationBuilder AddUserNamePasswordEndpoint(bool enableRefreshToken = true)
    {
        UseAuthenticationEndpoint<NhUserNamePasswordAuthenticationHandler>();
        if (enableRefreshToken)
        {
            UseAuthenticationEndpoint<NhRefreshTokenAuthenticationHandler>();
        }
        return this;
    }

    public NhAuthenticationConfigurationBuilder AddRefreshTokenEndpoint()
    {
        UseAuthenticationEndpoint<NhRefreshTokenAuthenticationHandler>();
        return this;
    }

    public NhAuthenticationConfigurationBuilder UseAuthenticationEndpoint<TEndpoint>()
        where TEndpoint : IAuthenticationEndpoint
    {
        _diEndpoints.Add(typeof(TEndpoint));
        return this;
    }

    public NhAuthenticationConfigurationBuilder UseAuthenticationEndpoint(IAuthenticationEndpoint endpoint)
    {
        _endpoints.Add(endpoint);
        return this;
    }


    public void Build(IApplicationBuilder app, IServiceProvider services)
    {
        app.UseEndpoints(endpoints =>
        {
            foreach (var type in _diEndpoints)
            {
                var endpoint = (IAuthenticationEndpoint)services.GetRequiredService(type);
                ConfigureEndpoint(endpoint, endpoints);
            }

            foreach (var endpoint in _endpoints)
            {
                ConfigureEndpoint(endpoint, endpoints);
            }
        });
        return;

        void ConfigureEndpoint(IAuthenticationEndpoint endpoint, IEndpointRouteBuilder endpoints)
        {
            switch (endpoint.Method)
            {
                case HttpMethod.Get:
                    endpoints.MapGet(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Post:
                    endpoints.MapPost(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Put:
                    endpoints.MapPut(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Delete:
                    endpoints.MapDelete(endpoint.Pattern, endpoint.Handler);
                    break;
                case HttpMethod.Patch:
                    endpoints.MapPatch(endpoint.Pattern, endpoint.Handler);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Invalid HTTP method {endpoint.Method} for authentication");
            }
        }
    }
}

public class AuthenticationEndpoint : IAuthenticationEndpoint
{
    public required string Pattern { get; init; }
    public HttpMethod Method { get; init; }
    public required Delegate Handler { get; init; }
}

public interface IAuthenticationEndpoint
{
    public string Pattern { get; }

    public HttpMethod Method { get; }

    public Delegate Handler { get; }
}

public enum HttpMethod
{
    Get,
    Post,
    Put,
    Delete,
    Patch
}