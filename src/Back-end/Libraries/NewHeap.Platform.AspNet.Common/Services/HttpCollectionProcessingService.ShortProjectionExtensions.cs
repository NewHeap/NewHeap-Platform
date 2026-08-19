using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services;

public static class HttpCollectionProcessingServiceShortProjectionExtensions
{
    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultModelAsync(
            service,
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedSimpleCollectionResultModelAsync(
            service,
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default);
    }

    public static Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultQueryAsync(
            service,
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            asNoTracking: true,
            cancellationToken: default);
    }
}
