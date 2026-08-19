using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Identity.Claims;
using SampleProjectManagement.Core.Models.View;
using System.Reflection;
using System.Security.Claims;

namespace SampleProjectManagement.Core.Services;

[ClaimMatchOneAuthorize(NhPlatformClaimTypes.Permission, "app.project.manage")]
[ClaimMatchOneAuthorize(ClaimTypes.Role, "administrator")]
public sealed class ProjectEditorMatchOneContract;

/// <summary>
/// Executes ClaimMatchOneAuthorizeAttribute as an OR contract. The attribute is
/// metadata; this concrete service is the enforcement point used by the API.
/// </summary>
public sealed class ProjectAuthorizationSampleService
{
    public MatchOneAuthorizationSampleViewModel EvaluateEditorAccess(ClaimsPrincipal user)
    {
        var rules = typeof(ProjectEditorMatchOneContract)
            .GetCustomAttributes<ClaimMatchOneAuthorizeAttribute>(inherit: true)
            .ToList();
        var matched = rules.FirstOrDefault(rule => user.HasClaim(rule.Type, rule.Value));

        return new MatchOneAuthorizationSampleViewModel
        {
            Allowed = matched is not null,
            MatchedRule = matched is null ? null : $"{matched.Type}={matched.Value}",
            RequiredOneOf = rules.Select(rule => $"{rule.Type}={rule.Value}").ToList()
        };
    }
}
