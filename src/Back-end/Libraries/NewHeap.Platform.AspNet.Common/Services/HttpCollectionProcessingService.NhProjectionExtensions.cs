using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services;

public static class HttpCollectionProcessingServiceNhProjectionExtensions
{
    public static Task<CollectionResultModel<TProjection>> GetProjectedCollectionResultModelAsync<TEntity, TProjection>(
        this IHttpCollectionProcessingService service,
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
            queryable,
            projection,
            configureOptions: null,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<CollectionResultModel<TProjection>> GetProjectedCollectionResultModelAsync<TEntity, TProjection>(
        this IHttpCollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);

        return CollectionProcessingServiceNhProjectionExtensions.GetProjectedCollectionResultModelAsync(
            service,
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            configureOptions,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TProjection>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TProjection>(
        this IHttpCollectionProcessingService service,
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
            queryable,
            projection,
            configureOptions: null,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TProjection>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TProjection>(
        this IHttpCollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);

        return CollectionProcessingServiceNhProjectionExtensions.GetProjectedSimpleCollectionResultModelAsync(
            service,
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            configureOptions,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TProjection>> GetProjectedCollectionResultQueryAsync<TEntity, TProjection>(
        this IHttpCollectionProcessingService service,
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
            queryable,
            projection,
            configureOptions: null,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TProjection>> GetProjectedCollectionResultQueryAsync<TEntity, TProjection>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        NhProjectionDefinition<TEntity, TProjection> projection,
        Action<CollectionProcessingOptionsBuilder<TProjection, TProjection>>? configureOptions,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(service);

        return CollectionProcessingServiceNhProjectionExtensions.GetProjectedCollectionResultQueryAsync(
            service,
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            configureOptions,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }
}
