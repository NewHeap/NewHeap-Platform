using Microsoft.AspNetCore.Authentication.JwtBearer;
using NewHeap.Media;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Maps the media library of the project documents (SPM-181).
/// </summary>
public static class SampleProjectMediaComposition
{
    public const string RoutePrefix = "project-media";

    /// <summary>
    /// Maps the media routes under <see cref="RoutePrefix"/> for signed-in users only. The policy
    /// names the bearer scheme, because minimal APIs do not inherit the scheme of
    /// <c>NhBaseController</c> and <c>WithIdentity</c> makes the Identity cookie the default scheme.
    /// <see cref="ProjectMediaAuthorizationModule"/> then authorizes the division of every path the
    /// user reads, creates, changes or deletes.
    /// </summary>
    public static RouteGroupBuilder MapSampleProjectMedia(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapNhMediaEndpoints(RoutePrefix, options =>
        {
            options.ConfigureAllRoutes(route => route.RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()));
        });
    }
}
