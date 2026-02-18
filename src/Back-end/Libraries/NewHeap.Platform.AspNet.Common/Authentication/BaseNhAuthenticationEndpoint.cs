using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Base class for implementing authentication endpoints
/// </summary>
public abstract class BaseNhAuthenticationEndpoint : IAuthenticationEndpoint, IDisposable
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    protected readonly AuthenticationConfiguration Configuration;

    public string Pattern { get; set; }
    public HttpMethod Method { get; protected set; } = HttpMethod.Post;
    public Delegate Handler { get; protected set; }
    protected HttpContext? HttpContext => _httpContextAccessor.HttpContext;

    protected BaseNhAuthenticationEndpoint(
        IHttpContextAccessor httpContextAccessor,
        string pattern,
        AuthenticationConfiguration configuration
        )
    {
        Pattern = pattern;
        _httpContextAccessor = httpContextAccessor;
        Configuration = configuration;
        Handler = () => { };
    }

    protected void WriteTokenToCookie(UserToken token)
    {
        if(HttpContext == null)
        {
            throw new InvalidOperationException("HttpContext is not available.");
        }

        var authService = GetAuthService();
        authService.WriteTokenToCookie(HttpContext, token);
    }
    
    protected virtual INhAuthenticationService GetAuthService()
    {
        var services = HttpContext!.RequestServices;
        if (!string.IsNullOrEmpty(Configuration.AuthenticationServiceKey))
        {
            return services.GetRequiredKeyedService<INhAuthenticationService>(Configuration.AuthenticationServiceKey);
        }

        return services.GetRequiredService<INhAuthenticationService>();
    }
    
    protected IResult BadRequest(TaskResult result)
    {
        return TypedResults.BadRequest(result.GetResultItems().ToDictionary(x => x.Name, x => x.ErrorMessages.Select(error => error.ToString())));
    }

    protected IResult Ok<T>(T result)
    {
        return TypedResults.Ok(result);
    }

    public void Dispose()
    {
    }
}

