using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Services;

public static class CollectionProcessingServiceNhProjectionExtensions
{
    public static Task<CollectionResultModel<TProjection>> GetProjectedCollectionResultModelAsync<TEntity, TProjection>(
        this ICollectionProcessingService service,
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
        return GetProjectedCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            configureOptions: null,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<CollectionResultModel<TProjection>> GetProjectedCollectionResultModelAsync<TEntity, TProjection>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        Action<CollectionProcessingOptionsBuilder<TProjection, TProjection>>? configureOptions,
        Func<IQueryable<TProjection>, CancellationToken, Task<IQueryable<TProjection>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(projection);

        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection.Selector,
            options =>
            {
                projection.ApplyTo(options);
                configureOptions?.Invoke(options);
            },
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TProjection>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TProjection>(
        this ICollectionProcessingService service,
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
        return GetProjectedSimpleCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection,
            configureOptions: null,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TProjection>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TProjection>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        Action<CollectionProcessingOptionsBuilder<TProjection, TProjection>>? configureOptions,
        Func<IQueryable<TProjection>, CancellationToken, Task<IQueryable<TProjection>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(projection);

        return CollectionProcessingServiceProjectionExtensions.GetProjectedSimpleCollectionResultModelAsync(
            service,
            requestModel,
            queryable,
            projection.Selector,
            options =>
            {
                projection.ApplyTo(options);
                configureOptions?.Invoke(options);
            },
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TProjection>> GetProjectedCollectionResultQueryAsync<TEntity, TProjection>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        return GetProjectedCollectionResultQueryAsync(
            service,
            requestModel,
            queryable,
            projection,
            configureOptions: null,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TProjection>> GetProjectedCollectionResultQueryAsync<TEntity, TProjection>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        Action<CollectionProcessingOptionsBuilder<TProjection, TProjection>>? configureOptions,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(projection);

        return CollectionProcessingServiceProjectionExtensions.GetProjectedCollectionResultQueryAsync(
            service,
            requestModel,
            queryable,
            projection.Selector,
            options =>
            {
                projection.ApplyTo(options);
                configureOptions?.Invoke(options);
            },
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }
}
