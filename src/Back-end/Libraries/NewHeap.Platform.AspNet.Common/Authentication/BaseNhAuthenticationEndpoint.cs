using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
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
    private readonly IServiceScope _scope;

    public string Pattern { get; set; }
    public HttpMethod Method { get; protected set; } = HttpMethod.Post;
    public Delegate Handler { get; protected set; }
    protected HttpContext? HttpContext => _httpContextAccessor.HttpContext;

    protected BaseNhAuthenticationEndpoint(
        IHttpContextAccessor httpContextAccessor,
        string pattern,
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration
        )
    {
        _scope = serviceProvider.CreateScope();
        Pattern = pattern;
        _httpContextAccessor = httpContextAccessor;
        Configuration = configuration;
        Handler = () => { };
    }

    protected virtual INhAuthenticationService GetAuthService()
    {
        if (!string.IsNullOrEmpty(Configuration.AuthenticationServiceKey))
        {
            return _scope.ServiceProvider.GetRequiredKeyedService<INhAuthenticationService>(Configuration.AuthenticationServiceKey);
        }
        return _scope.ServiceProvider.GetRequiredService<INhAuthenticationService>();
    }
    
    protected IResult BadRequest(TaskResult result)
    {
        return TypedResults.BadRequest(result.GetResultItems().ToDictionary(x => x.Name, x => x.ErrorMessages.Select(error => error.ToString())));
    }

    public void Dispose()
    {
        _scope.Dispose();
    }
}

