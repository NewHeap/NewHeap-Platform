using System.Collections.Concurrent;
using NewHeap.Media.Modules;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Identity.Claims;

namespace SampleProjectManagement.Api.Services;

public sealed class ProjectMediaAuthorizationModule : IAuthorizationModule
{
    public const string SamplePermissionsHeader = "X-Sample-Media-Permissions";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SampleMediaAuthorizationLog _authorizationLog;

    public ProjectMediaAuthorizationModule(
        IHttpContextAccessor httpContextAccessor,
        SampleMediaAuthorizationLog authorizationLog)
    {
        _httpContextAccessor = httpContextAccessor;
        _authorizationLog = authorizationLog;
    }

    public Task IsAuthorizedAsync(AuthorizationContext context)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var divisionId = httpContext?.GetActiveDivisionId();
        var requiredPermission = context.Action == ActionType.Read
            ? "app.project.view"
            : "app.project.manage";
        var normalizedPath = NormalizePath(context.Path);
        var scopedRoot = divisionId.HasValue
            ? $"/divisions/{divisionId.Value:D}/projects"
            : string.Empty;
        var pathIsScoped = divisionId.HasValue &&
            (normalizedPath.Equals(scopedRoot, StringComparison.OrdinalIgnoreCase) ||
             normalizedPath.StartsWith(scopedRoot + "/", StringComparison.OrdinalIgnoreCase));

        var userHasPermission = httpContext?.User.HasClaim(
            NhPlatformClaimTypes.Permission,
            requiredPermission) == true;
        var userHasDivisionPermission = divisionId.HasValue &&
            httpContext?.User.HasClaim(
                NhPlatformClaimTypes.DivisionPermission,
                $"{divisionId.Value:D}_{requiredPermission[4..]}") == true;
        var samplePermissions = httpContext?.Request.Headers[SamplePermissionsHeader]
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        var sampleHeaderHasPermission = samplePermissions.Contains(
            requiredPermission,
            StringComparer.OrdinalIgnoreCase);

        context.Authorized = pathIsScoped &&
            (userHasPermission || userHasDivisionPermission || sampleHeaderHasPermission);

        _authorizationLog.Record(new SampleMediaAuthorizationDecision(
            DateTimeOffset.UtcNow,
            divisionId,
            context.Action,
            normalizedPath,
            requiredPermission,
            context.Authorized,
            userHasPermission || userHasDivisionPermission ? "claims" :
                sampleHeaderHasPermission ? "documented-sample-header" : "none"));

        return Task.CompletedTask;
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
