using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace SampleProjectManagement.Api.Authorization;

public static class SampleRuntimeAuthorizationClaims
{
    public static bool IsRequestScoped(Claim claim) =>
        IsRequestScoped(claim.Type);

    public static bool IsRequestScoped(string claimType) =>
        claimType == NhPlatformClaimTypes.DivisionPermission ||
        claimType == NhPlatformClaimTypes.DivisionRole ||
        claimType == SampleProjectClaimTypes.ProjectPermission ||
        claimType == SampleProjectClaimTypes.ProjectRole;
}
