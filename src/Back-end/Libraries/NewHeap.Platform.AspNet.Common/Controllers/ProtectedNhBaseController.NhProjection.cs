using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class ProtectedNhBaseController
{
    [NonAction]
    protected virtual Task<CollectionResultModel<TProjection>> GetProjectedCollectionResultModel<TEntity, TProjection>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        Func<IQueryable<TProjection>, CancellationToken, Task<IQueryable<TProjection>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        return CollectionProcessingServiceNhProjectionExtensions.GetProjectedCollectionResultModelAsync(
            _httpCollectionProcessingService,
            requestModel,
            queryable,
            projection,
            options => ConfigureProjectedCollectionProcessing<TEntity, TProjection>(options),
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<IActionResult> GetProjectedCollectionResultAsync<TEntity, TProjection>(
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        Func<IQueryable<TProjection>, CancellationToken, Task<IQueryable<TProjection>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        maxItemsPerPage ??= _httpCollectionProcessingService.GetDefaultMaxItemsPerPage();
        var requestModel = GetCollectionRequestModel(maxItemsPerPage);

        var result = await GetProjectedCollectionResultModel(
            requestModel,
            queryable,
            projection,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);

        return Ok(result);
    }

    [NonAction]
    protected virtual Task<IQueryable<TProjection>> GetProjectedCollectionResultQuery<TEntity, TProjection>(
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        return HttpCollectionProcessingServiceNhProjectionExtensions.GetProjectedCollectionResultQueryAsync(
            _httpCollectionProcessingService,
            queryable,
            projection,
            options => ConfigureProjectedCollectionProcessing<TEntity, TProjection>(options),
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }
}
