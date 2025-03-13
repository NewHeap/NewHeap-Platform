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

    /// <summary>
    /// Add endpoint for handling authentication
    /// </summary>
    /// <param name="pattern"></param>
    /// <param name="method"></param>
    /// <param name="handler"></param>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder AddAuthenticationEndpoint(string pattern, HttpMethod method,
        Delegate handler)
    {
        _endpoints.Add(new AuthenticationEndpoint { Pattern = pattern, Method = method, Handler = handler });

        return this;
    }

    /// <summary>
    /// Add endpoints for handling username password login flow
    /// </summary>
    /// <param name="enableRefreshToken"></param>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder AddUserNamePasswordEndpoint(bool enableRefreshToken = true)
    {
        UseAuthenticationEndpoint<NhUserNamePasswordAuthenticationHandler>();
        if (enableRefreshToken)
        {
            UseAuthenticationEndpoint<NhRefreshTokenAuthenticationHandler>();
        }

        UseAuthenticationEndpoint<NhLogoutAuthenticationHandler>();
        UseAuthenticationEndpoint<NhAccountInformationEndpointHandler>();
        return this;
    }

    /// <summary>
    /// Add endpoints for handling refresh token login flow
    /// </summary>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder AddRefreshTokenEndpoint()
    {
        UseAuthenticationEndpoint<NhRefreshTokenAuthenticationHandler>();
        return this;
    }

    /// <summary>
    /// Add endpoint for handling authentication flow
    /// </summary>
    /// <typeparam name="TEndpoint"></typeparam>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder UseAuthenticationEndpoint<TEndpoint>()
        where TEndpoint : IAuthenticationEndpoint
    {
        _diEndpoints.Add(typeof(TEndpoint));
        return this;
    }

    /// <summary>
    /// Add endpoint for handling authentication flow
    /// </summary>
    /// <param name="endpoint"></param>
    /// <returns></returns>
    public NhAuthenticationConfigurationBuilder UseAuthenticationEndpoint(IAuthenticationEndpoint endpoint)
    {
        _endpoints.Add(endpoint);
        return this;
    }


    /// <summary>
    /// Build the authentication configuration
    /// </summary>
    /// <param name="app"></param>
    /// <param name="services"></param>
    /// <exception cref="InvalidOperationException"></exception>
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