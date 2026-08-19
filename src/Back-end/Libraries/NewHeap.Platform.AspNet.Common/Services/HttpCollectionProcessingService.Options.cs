using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class HttpCollectionProcessingService
{
    public virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return GetCollectionResultModelAsync(
            requestModel,
            queryable,
            configureOptions,
            null,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return GetSimpleCollectionResultModelAsync(
            requestModel,
            queryable,
            configureOptions,
            null,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public virtual async Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();
        var options = CreateCollectionProcessingOptions(configureOptions);

        var processedResult = await ProcessQueryable(
            requestModel,
            queryable,
            options,
            cancellationToken,
            defaultOrderBy);

        return processedResult.queryable;
    }
}
