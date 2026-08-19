using NewHeap.Platform.Common.Models;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Services;

public static class CollectionProcessingServiceShortProjectionExtensions
{
    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedSimpleCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default);
    }

    public static Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultQueryAsync(
            service,
            requestModel,
            queryable,
            projection,
            asNoTracking: true,
            cancellationToken: default);
    }
}
