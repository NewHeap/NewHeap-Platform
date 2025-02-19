using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Policy.Requirements;

public partial class ActiveDivisionAccessRequirement : IAuthorizationRequirement
{
    public ActiveDivisionAccessRequirement(IEnumerable<Claim> requiredClaims, IEnumerable<string>? requiredRoles = null)
    {
        RequiredClaims = requiredClaims;
        RequiredRoles = requiredRoles;
    }

    public IEnumerable<Claim> RequiredClaims { get; set; }
    public IEnumerable<string>? RequiredRoles { get; set; }
}