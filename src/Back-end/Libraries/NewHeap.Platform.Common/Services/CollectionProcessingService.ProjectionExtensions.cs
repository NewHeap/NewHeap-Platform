using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Services;

public static class CollectionProcessingServiceProjectionExtensions
{
    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return service.GetProjectedCollectionResultModelAsync(
            requestModel,
            queryable,
            projection,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);
        var projectedQuery = CreateProjectedQuery(queryable, projection, asNoTracking);

        return service.GetCollectionResultModelAsync<TViewModel, TViewModel>(
            requestModel,
            projectedQuery,
            resultQueryableFunc,
            asNoTracking: false,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<CollectionResultModel<TViewModel>> GetProjectedCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var projectedQuery = CreateProjectedQuery(queryable, projection, asNoTracking);

        return service.GetCollectionResultModelAsync<TViewModel, TViewModel>(
            requestModel,
            projectedQuery,
            configureOptions,
            resultQueryableFunc,
            asNoTracking: false,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        Func<IQueryable<TViewModel>, CancellationToken, Task<IQueryable<TViewModel>>>? resultQueryableFunc = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return service.GetProjectedSimpleCollectionResultModelAsync(
            requestModel,
            queryable,
            projection,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);
        var projectedQuery = CreateProjectedQuery(queryable, projection, asNoTracking);

        return service.GetSimpleCollectionResultModelAsync<TViewModel, TViewModel>(
            requestModel,
            projectedQuery,
            resultQueryableFunc,
            asNoTracking: false,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<SimpleCollectionResultModel<TViewModel>> GetProjectedSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var projectedQuery = CreateProjectedQuery(queryable, projection, asNoTracking);

        return service.GetSimpleCollectionResultModelAsync<TViewModel, TViewModel>(
            requestModel,
            projectedQuery,
            configureOptions,
            resultQueryableFunc,
            asNoTracking: false,
            cancellationToken,
            defaultOrderBy);
    }

    public static Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return service.GetProjectedCollectionResultQueryAsync(
            requestModel,
            queryable,
            projection,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    public static async Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(service);
        var projectedQuery = CreateProjectedQuery(queryable, projection, asNoTracking);

        var processedResult = await service.ProcessQueryable<TViewModel, TViewModel>(
            requestModel,
            projectedQuery,
            cancellationToken,
            defaultOrderBy);

        return processedResult.queryable;
    }

    public static async Task<IQueryable<TViewModel>> GetProjectedCollectionResultQueryAsync<TEntity, TViewModel>(
        this ICollectionProcessingService service,
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
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var projectedQuery = CreateProjectedQuery(queryable, projection, asNoTracking);
        var options = CreateCollectionProcessingOptions(configureOptions);

        var processedResult = await service.ProcessQueryable(
            requestModel,
            projectedQuery,
            options,
            cancellationToken,
            defaultOrderBy);

        return processedResult.queryable;
    }

    private static IQueryable<TViewModel> CreateProjectedQuery<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, TViewModel>> projection,
        bool asNoTracking)
        where TEntity : class
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(queryable);
        ArgumentNullException.ThrowIfNull(projection);

        if (asNoTracking)
        {
            queryable = queryable.AsNoTracking();
        }

        return queryable.Select(projection);
    }

    private static CollectionProcessingOptions<TEntity, TViewModel> CreateCollectionProcessingOptions<TEntity, TViewModel>(
        Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions)
        where TEntity : class
        where TViewModel : class
    {
        var builder = new CollectionProcessingOptionsBuilder<TEntity, TViewModel>();
        configureOptions.Invoke(builder);
        return builder.Build();
    }
}
