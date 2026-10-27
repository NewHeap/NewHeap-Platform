using System.Collections.Concurrent;
using System.Security.Claims;
using NewHeap.Media.Modules;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Identity.Claims;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// Authorizes every media operation for the signed-in user. The path must lie in the projects
/// folder of the active division, and that division is authorized from the user's current rights:
/// the application permission <c>app.project.view</c> or <c>app.project.manage</c>, or the division
/// permission <c>project.view</c> or <c>project.manage</c>, which
/// <see cref="NewHeap.Platform.AspNet.Common.HttpContextExtensions.HasDivisionAccessAsync"/> reads
/// from the user store. The active-division header only selects the division; no request header
/// grants a permission.
/// </summary>
public sealed class ProjectMediaAuthorizationModule : IAuthorizationModule
{
    public const string ApplicationPermissionSource = "application-permission";
    public const string DivisionPermissionSource = "division-permission";
    public const string NoPermissionSource = "none";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SampleMediaAuthorizationLog _authorizationLog;

    public ProjectMediaAuthorizationModule(
        IHttpContextAccessor httpContextAccessor,
        SampleMediaAuthorizationLog authorizationLog)
    {
        _httpContextAccessor = httpContextAccessor;
        _authorizationLog = authorizationLog;
    }

    public async Task IsAuthorizedAsync(AuthorizationContext context)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var divisionId = httpContext?.GetActiveDivisionId();
        var permission = context.Action == ActionType.Read
            ? "view"
            : "manage";
        var applicationPermission = $"app.project.{permission}";
        var divisionPermission = $"project.{permission}";
        var normalizedPath = NormalizePath(context.Path);

        var source = NoPermissionSource;
        if (httpContext?.User.Identity?.IsAuthenticated == true &&
            IsInProjectsOfDivision(normalizedPath, divisionId))
        {
            source = await GetPermissionSourceAsync(
                httpContext,
                divisionId,
                applicationPermission,
                divisionPermission);
        }

        context.Authorized = source != NoPermissionSource;

        _authorizationLog.Record(new SampleMediaAuthorizationDecision(
            DateTimeOffset.UtcNow,
            divisionId,
            context.Action,
            normalizedPath,
            applicationPermission,
            context.Authorized,
            source));
    }

    private static async Task<string> GetPermissionSourceAsync(
        HttpContext httpContext,
        Guid? divisionId,
        string applicationPermission,
        string divisionPermission)
    {
        if (httpContext.User.HasClaim(NhPlatformClaimTypes.Permission, applicationPermission))
        {
            return ApplicationPermissionSource;
        }

        // Division membership comes from the user store, so a revoked membership or a division
        // claim the store does not grant never authorizes the path.
        if (await httpContext.HasDivisionAccessAsync(
                divisionId,
                [new Claim(NhPlatformClaimTypes.DivisionPermission, divisionPermission)],
                cancellationToken: httpContext.RequestAborted))
        {
            return DivisionPermissionSource;
        }

        return NoPermissionSource;
    }

    private static bool IsInProjectsOfDivision(string normalizedPath, Guid? divisionId)
    {
        if (!divisionId.HasValue)
        {
            return false;
        }

        var projectsRoot = $"/divisions/{divisionId.Value:D}/projects";

        return normalizedPath.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(projectsRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        var normalized = (path ?? "/").Replace('\\', '/');
        normalized = "/" + normalized.Trim('/');
        return normalized.Length == 0 ? "/" : normalized;
    }
}

public sealed record SampleMediaAuthorizationDecision(
    DateTimeOffset OccurredAtUtc,
    Guid? DivisionId,
    ActionType Action,
    string Path,
    string RequiredPermission,
    bool Authorized,
    string Source);

public sealed class SampleMediaAuthorizationLog
{
    private readonly ConcurrentQueue<SampleMediaAuthorizationDecision> _items = new();

    public IReadOnlyList<SampleMediaAuthorizationDecision> Items =>
        _items.Reverse().Take(50).ToArray();

    public void Record(SampleMediaAuthorizationDecision decision)
    {
        _items.Enqueue(decision);
        while (_items.Count > 100)
        {
            _items.TryDequeue(out _);
        }
    }
}
