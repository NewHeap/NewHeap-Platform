using Microsoft.AspNetCore.Http;
using NewHeap.Media.Modules;
using System.Linq;
using System.Threading.Tasks;

namespace WebAPI.Services;

public class LoggedInUserMediaAuthorizationModule : IAuthorizationModule
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggedInUserMediaAuthorizationModule(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Task IsAuthorizedAsync(AuthorizationContext context)
    {
        context.Authorized = (_httpContextAccessor.HttpContext?.User.Claims.Count() ?? 0) > 0;
        return Task.CompletedTask;
    }
}