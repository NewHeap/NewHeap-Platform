using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Caching;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.DAL;
using ZiggyCreatures.Caching.Fusion;

namespace SampleProjectManagement.Api.Controllers;

/// <summary>
/// SPM-170–172: memory caching, composed cache keys and explicit invalidation.
/// Call GET twice to observe an identical GeneratedAtUtc, then DELETE and GET again.
/// </summary>
[ApiController]
[Route("library-samples/cache")]
[Authorize(Policy = "app.project.view")]
public sealed class CachingSamplesController : ControllerBase
{
    private readonly IFusionCache _cache;
    private readonly SampleProjectManagementDbContext _dbContext;

    public CachingSamplesController(
        IFusionCache cache,
        SampleProjectManagementDbContext dbContext)
    {
        _cache = cache;
        _dbContext = dbContext;
    }

    [HttpGet("project-summary/{divisionId:guid}")]
    [EndpointSummary("Get a cached project summary")]
    [EndpointDescription("Returns the project count for a division through FusionCache. Repeat the call to observe the same generated timestamp.")]
    [ProducesResponseType<ProjectCacheSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ProjectCacheSample>> GetProjectSummary(
        Guid divisionId,
        CancellationToken cancellationToken)
    {
        var cacheKey = ProjectSummaryCacheKey(divisionId);
        var summary = await _cache.GetOrSetAsync(
            cacheKey,
            async token => new ProjectCacheSample(
                cacheKey,
                divisionId,
                await _dbContext.Projects
                    .AsNoTracking()
                    .CountAsync(project => project.DivisionId == divisionId, token),
                DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(2),
            cancellationToken);

        return Ok(summary);
    }

    [HttpDelete("project-summary/{divisionId:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Invalidate a cached project summary")]
    [EndpointDescription("Removes the composed division project-summary key so that the next GET recomputes the cached value.")]
    [ProducesResponseType<CacheInvalidationSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> InvalidateProjectSummary(
        Guid divisionId,
        CancellationToken cancellationToken)
    {
        var cacheKey = ProjectSummaryCacheKey(divisionId);
        await _cache.RemoveAsync(cacheKey, token: cancellationToken);

        return Ok(new CacheInvalidationSample(cacheKey, true));
    }

    private static string ProjectSummaryCacheKey(Guid divisionId)
        => NhCacheKey.Create("project-management", "project-summary", divisionId.ToString("N"));
}

public sealed record ProjectCacheSample(
    string CacheKey,
    Guid DivisionId,
    int ProjectCount,
    DateTimeOffset GeneratedAtUtc);
