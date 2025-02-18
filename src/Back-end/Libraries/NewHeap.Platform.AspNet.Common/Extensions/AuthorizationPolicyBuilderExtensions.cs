using System;
using System.IO;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Policy.Requirements;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using StackExchange.Utils;

namespace NewHeap.Platform.AspNet.Common.Extensions;
public static partial class ServiceCollectionExtensions
{
    public static AuthorizationPolicyBuilder RequireActiveDivisionAccess(this AuthorizationPolicyBuilder authorizationPolicyBuilder, IEnumerable<string>? roles = null, params Claim[] claims)
    {
        authorizationPolicyBuilder.Requirements.Add(new ActiveDivisionAccessRequirement(
            requiredRoles: roles,
            requiredClaims: claims));

        return authorizationPolicyBuilder;
    }
}