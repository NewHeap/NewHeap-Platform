using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Net;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface IHttpCollectionProcessingService : ICollectionProcessingService
{
    ICollectionRequestModel GetCollectionRequestModel(int? maxItemsPerPage = null);
    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;
    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, bool asNoTracking = true, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, bool asNoTracking = true, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;
}

public partial class HttpCollectionProcessingService : CollectionProcessingService, IHttpCollectionProcessingService
{
    protected readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCollectionProcessingService(
        IMapper mapper,
        IHttpContextAccessor httpContextAccessor
        )
        : base(mapper)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public virtual ICollectionRequestModel GetCollectionRequestModel(int? maxItemsPerPage = null)
    {
        var request = _httpContextAccessor?.HttpContext?.Request;

        if (request == null)
        {
            throw new Exception("HttpContext is null");
        }

        maxItemsPerPage ??= GetDefaultMaxItemsPerPage();
        var defaultItemsPerPage = 20;

        var qPage = request.Query["page"];
        var qItemsPerPage = request.Query["itemsPerPage"];
        string? qOrderBy = request.Query["orderBy"];
        string? qSearch = request.Query["search"];
        string? qFilter = request.Query["filter"];

        if (!int.TryParse(qPage.FirstOrDefault(), out var page) || page < 1)
        {
            page = 1;
        }

        int.TryParse(qItemsPerPage.FirstOrDefault(), out var itemsPerPage2);

        if (!int.TryParse(qItemsPerPage.FirstOrDefault(), out var itemsPerPage) || itemsPerPage > maxItemsPerPage)
        {
            itemsPerPage = defaultItemsPerPage;
        }

        qSearch = qSearch?.Trim();

        List<OrderByCollectionRequestModel> orderBy = new List<OrderByCollectionRequestModel>();
        var filter = new List<FilterCollectionRequestModel>();

        try
        {
            if (!string.IsNullOrWhiteSpace(qOrderBy))
            {
                orderBy = JsonConvert.DeserializeObject<List<OrderByCollectionRequestModel>>(qOrderBy)!;
            }

            if (!string.IsNullOrWhiteSpace(qFilter))
            {
                filter = JsonConvert.DeserializeObject<List<FilterCollectionRequestModel>>(qFilter);
            }
        }
        catch
        {
            //Ignore
        }

        return new CollectionRequestModel
        {
            Page = page,
            ItemsPerPage = itemsPerPage,
            Search = qSearch,
            OrderBy = orderBy ?? [],
            Filter = filter ?? []
        };
    }

    protected async Task<CollectionResultModel<TViewModel>> _GetCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
        )
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await _GetCollectionResultModelAsync<TEntity, TViewModel>(requestModel, queryable, defaultOrderBy, null, asNoTracking: asNoTracking, cancellationToken: cancellationToken);
    }

    public virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return await GetCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            asNoTracking: true,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );
    }

    public virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return await _GetCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            defaultOrderBy.ToList(),
            cancellationToken: cancellationToken
        );
    }

    public virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return await GetCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            asNoTracking: true,
            cancellationToken: cancellationToken
        );
    }

    public virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return await _GetCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            new List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)>(),
            asNoTracking: asNoTracking,
            cancellationToken: cancellationToken
        );
    }

    protected async Task<SimpleCollectionResultModel<TViewModel>> _GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
        )
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        var resultModel = await _GetCollectionResultModelAsync<TEntity, TViewModel>(queryable, defaultOrderBy, asNoTracking: asNoTracking, cancellationToken: cancellationToken);

        return new SimpleCollectionResultModel<TViewModel>
        {
            Items = resultModel.Items,
            TotalCount = resultModel.TotalCount,
            Page = resultModel.Page,
            ItemsPerPage = resultModel.ItemsPerPage
        };
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            asNoTracking: true,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            defaultOrderBy.ToList(),
            asNoTracking: asNoTracking,
            cancellationToken: cancellationToken
        );
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            asNoTracking: true,
            cancellationToken: cancellationToken
        );
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return _GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            queryable,
            new List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)>(),
            asNoTracking: asNoTracking,
            cancellationToken: cancellationToken
        );
    }

    protected async Task<IQueryable<TEntity>> _GetCollectionResultQueryAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        CancellationToken cancellationToken = default
        )
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        var processedResult = await _ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            defaultOrderBy,
            cancellationToken: cancellationToken
        );

        queryable = processedResult.queryable;

        return queryable;
    }

    public virtual Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _GetCollectionResultQueryAsync<TEntity, TViewModel>(
            queryable,
            cancellationToken: cancellationToken,
            defaultOrderBy: defaultOrderBy.ToList()
        );
    }

    public virtual Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return _GetCollectionResultQueryAsync<TEntity, TViewModel>(
            queryable,
            cancellationToken: cancellationToken,
            defaultOrderBy: new List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)>()
        );
    }
}