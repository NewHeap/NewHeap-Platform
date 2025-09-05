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
        var domain = new Uri(token.Issuer).Host;

        HttpContext!.Response.Cookies.Append(Configuration.CookieName!, token.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = token.ValidTo,
            Domain = domain,
            IsEssential = true,
        });

        if (Configuration.RefreshTokenEnabled && !string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            HttpContext.Response.Cookies.Append(Configuration.RefreshCookieName!, token.RefreshToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.Now.AddDays(2),
                Domain = domain,
                IsEssential = true,
            });
        }
        else
        {
            token.RefreshToken = null;
        }
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

