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

public interface IHttpCollectionRequestProcessingService : ICollectionRequestProcessingService
{
    CollectionRequestModel GetCollectionRequestModel(int? maxItemsPerPage = null);
    Task<CollectionResponseModel<TViewModel>> GetCollectionResponseModelAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;
    Task<IQueryable<TEntity>> GetCollectionResponseQueryAsync<TEntity, TViewModel>(IQueryable<TEntity> queryable, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    int GetDefaultMaxItemsPerPage();
}

public partial class HttpCollectionRequestProcessingService : CollectionRequestProcessingService, IHttpCollectionRequestProcessingService
{
    protected readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCollectionRequestProcessingService(
        IMapper mapper,
        IHttpContextAccessor httpContextAccessor
        )
        : base(mapper)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public virtual int GetDefaultMaxItemsPerPage()
    {
        // TODO: Get this from the configuration / factory
        return 1000;
    }

    public virtual CollectionRequestModel GetCollectionRequestModel(int? maxItemsPerPage = null)
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

        List<OrderByRequestModel> orderBy = new List<OrderByRequestModel>();
        var filter = new List<FilterRequestModel>();

        try
        {
            if (!string.IsNullOrWhiteSpace(qOrderBy))
            {
                orderBy = JsonConvert.DeserializeObject<List<OrderByRequestModel>>(qOrderBy)!;
            }

            if (!string.IsNullOrWhiteSpace(qFilter))
            {
                filter = JsonConvert.DeserializeObject<List<FilterRequestModel>>(qFilter);
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

    public virtual async Task<CollectionResponseModel<TViewModel>> GetCollectionResponseModelAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResponseModelAsync<TEntity, TViewModel>(requestModel, queryable, null, defaultOrderBy);
    }

    public virtual async Task<IQueryable<TEntity>> GetCollectionResponseQueryAsync<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        var processedResult = await ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            defaultOrderBy
        );

        queryable = processedResult.queryable;

        return queryable;
    }
}