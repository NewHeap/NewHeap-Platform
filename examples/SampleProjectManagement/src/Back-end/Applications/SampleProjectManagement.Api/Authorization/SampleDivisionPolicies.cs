using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace SampleProjectManagement.Api.Authorization;

/// <summary>
/// SPM-211: the policies of the division membership endpoints. <c>Program.cs</c> and the sample
/// tests share them, so the tests prove the policies the API runs with.
/// </summary>
public static class SampleDivisionPolicies
{
    /// <summary>The read policy name of the library's division-user controller.</summary>
    public const string View = "app.division.view";

    /// <summary>The write policy name of the library's division-user controller.</summary>
    public const string Manage = "app.division.manage";

    /// <summary>
    /// Registers <see cref="View"/> and <see cref="Manage"/> as division policies: the caller needs
    /// the <c>division.view</c> or <c>division.manage</c> permission in the division of the
    /// active-division header, so a division manager works in that division only.
    /// </summary>
    public static AuthorizationOptions AddSampleDivisionPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(
            View,
            policy => policy.RequireActiveDivisionAccess(
                null,
                new Claim(NhPlatformClaimTypes.DivisionPermission, "division.view")));

        options.AddPolicy(
            Manage,
            policy => policy.RequireActiveDivisionAccess(
                null,
                new Claim(NhPlatformClaimTypes.DivisionPermission, "division.manage")));

        return options;
    }
}
