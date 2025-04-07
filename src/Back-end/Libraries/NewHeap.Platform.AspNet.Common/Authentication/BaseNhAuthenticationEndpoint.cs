using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.Common.Models;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Base class for implementing authentication endpoints
/// </summary>
public abstract class BaseNhAuthenticationEndpoint : IAuthenticationEndpoint
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public string Pattern { get; set; }
    public HttpMethod Method { get; protected set; } = HttpMethod.Post;
    public Delegate Handler { get; protected set; }
    protected HttpContext? HttpContext => _httpContextAccessor.HttpContext;

    protected BaseNhAuthenticationEndpoint(IHttpContextAccessor httpContextAccessor, string pattern)
    {
        Pattern = pattern;
        _httpContextAccessor = httpContextAccessor;
    }

    protected IResult BadRequest(TaskResult result)
    {
        return TypedResults.BadRequest(result.Results.ToDictionary(x => x.Name, x => x.ErrorMessages.Select(error => error.ToString())));
    }
}

