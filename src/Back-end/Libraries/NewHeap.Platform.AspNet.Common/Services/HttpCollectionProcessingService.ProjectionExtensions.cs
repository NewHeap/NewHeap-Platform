using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services;

public static class HttpCollectionProcessingServiceProjectionExtensions
{
    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return service.GetProjectedCollectionResultModelAsync(
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.GetProjectedCollectionResultModelAsync(
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);

        return service.GetProjectedCollectionResultModelAsync(
            service.GetCollectionRequestModel(),
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
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return service.GetProjectedSimpleCollectionResultModelAsync(
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.GetProjectedSimpleCollectionResultModelAsync(
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);

        return service.GetProjectedSimpleCollectionResultModelAsync(
            service.GetCollectionRequestModel(),
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
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.GetProjectedCollectionResultQueryAsync(
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this IHttpCollectionProcessingService service,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Action<CollectionProcessingOptionsBuilder<TViewModel, TViewModel>> configureOptions,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.GetProjectedCollectionResultQueryAsync(
            service.GetCollectionRequestModel(),
            queryable,
            projection,
            configureOptions,
            asNoTracking,
            cancellationToken,
            defaultOrderBy);
    }
}
