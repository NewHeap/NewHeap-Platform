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
    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;
    Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
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

    public virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default, 
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResultModelAsync<TEntity, TViewModel>(requestModel, queryable, null, cancellationToken: cancellationToken, defaultOrderBy);
    }

    public virtual async Task<IQueryable<TEntity>> GetCollectionResultQueryAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        var processedResult = await ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );

        queryable = processedResult.queryable;

        return queryable;
    }
}