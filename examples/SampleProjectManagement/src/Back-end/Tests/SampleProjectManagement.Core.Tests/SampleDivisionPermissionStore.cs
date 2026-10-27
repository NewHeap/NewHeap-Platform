using System.Collections.Concurrent;
using System.Security.Claims;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NSubstitute;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Stands in for the NewHeap user store in tests that authorize through
/// <c>HttpContextExtensions.HasDivisionAccessAsync</c>. It keeps the division permissions a test grants
/// each user, stored as NewHeap stores them with the division id as the claim value's prefix, and its
/// <see cref="UserManager"/> checks required division claims against them as
/// <c>NhUserManager.DivisionAccessAsync</c> does.
/// </summary>
internal sealed class SampleDivisionPermissionStore
{
    private readonly ConcurrentDictionary<Guid, Claim[]> _claims = new();

    public SampleDivisionPermissionStore()
    {
        UserManager = Substitute.For<INhUserManager>();
        UserManager
            .GetValidClaimsByUserIdAsync(Arg.Any<Guid>(), true, Arg.Any<CancellationToken>())
            .Returns(call => _claims.TryGetValue(call.ArgAt<Guid>(0), out var claims)
                ? claims.ToList()
                : new List<Claim>());
        UserManager
            .DivisionAccessAsync(
                Arg.Any<Guid?>(),
                Arg.Any<IEnumerable<Claim>>(),
                Arg.Any<IEnumerable<Claim>?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => HoldsDivisionClaims(
                call.ArgAt<Guid?>(0),
                call.ArgAt<IEnumerable<Claim>>(1),
                call.ArgAt<IEnumerable<Claim>?>(2)));
    }

    public INhUserManager UserManager { get; }

    /// <summary>
    /// Replaces the user's division permissions with <paramref name="permissions"/> in
    /// <paramref name="divisionId"/>, such as <c>project.view</c> and <c>project.manage</c>.
    /// </summary>
    public SampleDivisionPermissionStore Grant(Guid userId, Guid divisionId, params string[] permissions)
    {
        _claims[userId] = permissions
            .Select(permission => new Claim(
                NhPlatformClaimTypes.DivisionPermission,
                $"{divisionId:D}_{permission}"))
            .ToArray();
        return this;
    }

    private static bool HoldsDivisionClaims(
        Guid? divisionId,
        IEnumerable<Claim> userClaims,
        IEnumerable<Claim>? requiredClaims)
    {
        if (!divisionId.HasValue || requiredClaims is null)
        {
            return false;
        }

        return requiredClaims.All(required => userClaims.Any(claim =>
            claim.Type == required.Type &&
            claim.Value == $"{divisionId.Value:D}_{required.Value}"));
    }
}
