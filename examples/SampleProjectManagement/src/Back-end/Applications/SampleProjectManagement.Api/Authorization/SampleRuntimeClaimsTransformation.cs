using Microsoft.AspNetCore.Authentication;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using System.Security.Claims;

namespace SampleProjectManagement.Api.Authorization;

public sealed class SampleRuntimeClaimsTransformation : IClaimsTransformation
{
    private const string RequestClaimsItemKey =
        $"{nameof(SampleRuntimeClaimsTransformation)}:Claims";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly INhUserManager<NhUser> _userManager;

    private sealed record RuntimeClaimsResult(
        bool UserExists,
        IReadOnlyCollection<Claim> Claims);

    public SampleRuntimeClaimsTransformation(
        IHttpContextAccessor httpContextAccessor,
        INhUserManager<NhUser> userManager)
    {
        _httpContextAccessor = httpContextAccessor;
        _userManager = userManager;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity ||
            !identity.IsAuthenticated ||
            !Guid.TryParse(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                out var userId))
        {
            return principal;
        }

        var runtimeClaimsResult = await GetRuntimeClaimsAsync(userId);
        if (!runtimeClaimsResult.UserExists)
        {
            // A validly signed token may outlive a removed user, for example
            // after a Development database reset. Returning an anonymous
            // principal lets the normal authorization pipeline produce 401.
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        var existingClaims = identity.Claims
            .Select(claim => (claim.Type, claim.Value))
            .ToHashSet();

        foreach (var claim in runtimeClaimsResult.Claims)
        {
            if (existingClaims.Add((claim.Type, claim.Value)))
            {
                identity.AddClaim(new Claim(
                    claim.Type,
                    claim.Value,
                    claim.ValueType,
                    claim.Issuer,
                    claim.OriginalIssuer));
            }
        }

        return principal;
    }

    private async Task<RuntimeClaimsResult> GetRuntimeClaimsAsync(Guid userId)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var cacheKey = $"{RequestClaimsItemKey}:{userId}";

        if (httpContext?.Items.TryGetValue(cacheKey, out var cachedValue) == true &&
            cachedValue is RuntimeClaimsResult cachedResult)
        {
            return cachedResult;
        }

        var claims = await _userManager.GetValidClaimsByUserIdAsync(
            userId,
            withDivision: true,
            httpContext?.RequestAborted ?? default);
        var result = new RuntimeClaimsResult(
            claims.Count > 0,
            claims
                .Where(SampleRuntimeAuthorizationClaims.IsRequestScoped)
                .ToArray());

        if (httpContext is not null)
        {
            httpContext.Items[cacheKey] = result;
        }

        return result;
    }
}
