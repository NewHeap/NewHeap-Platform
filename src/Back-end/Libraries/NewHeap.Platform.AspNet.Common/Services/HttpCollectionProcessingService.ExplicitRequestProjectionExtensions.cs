using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services;

public static class HttpCollectionProcessingServiceExplicitRequestProjectionExtensions
{
    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
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
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Action<CollectionProcessingOptionsBuilder<TViewModel, TViewModel>> configureOptions,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            configureOptions,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
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
        return CollectionProcessingServiceProjectionExtensions.GetProjectedSimpleCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Action<CollectionProcessingOptionsBuilder<TViewModel, TViewModel>> configureOptions,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedSimpleCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            configureOptions,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultQueryAsync(
            service,
            requestModel,
            queryable,
            projection,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Action<CollectionProcessingOptionsBuilder<TViewModel, TViewModel>> configureOptions,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultQueryAsync(
            service,
            requestModel,
            queryable,
            projection,
            configureOptions,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }
}
