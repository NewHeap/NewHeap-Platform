using Microsoft.AspNetCore.Authorization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Policy.Requirements;

public partial class ActiveDivisionAccessRequirement : IAuthorizationRequirement
{
    public IEnumerable<Claim> RequiredClaims { get; set; }
    public IEnumerable<string> RequiredRoles { get; set; }

    public ActiveDivisionAccessRequirement(IEnumerable<Claim> requiredClaims, IEnumerable<string> requiredRoles = null)
    {
        RequiredClaims = requiredClaims;
        RequiredRoles = requiredRoles;
    }
}
