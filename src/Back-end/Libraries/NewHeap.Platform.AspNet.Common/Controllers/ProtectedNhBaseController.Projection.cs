using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class ProtectedNhBaseController
{
    [NonAction]
    protected virtual void ConfigureProjectedCollectionProcessing<TEntity, TViewModel>(
        CollectionProcessingOptionsBuilder<TViewModel, TViewModel> options)
        where TEntity : class
        where TViewModel : class
    {
    }

    [NonAction]
    protected virtual Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModel<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _httpCollectionProcessingService.GetProjectedCollectionResultModelAsync(
            requestModel,
            queryable,
            projection,
            options => ConfigureProjectedCollectionProcessing<TEntity, TViewModel>(options),
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<IActionResult> GetProjectedCollectionResultAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
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
    protected virtual Task<IQueryable<TViewModel>> GetProjectedCollectionResultQuery<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _httpCollectionProcessingService.GetProjectedCollectionResultQueryAsync(
            queryable,
            projection,
            options => ConfigureProjectedCollectionProcessing<TEntity, TViewModel>(options),
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }
}
