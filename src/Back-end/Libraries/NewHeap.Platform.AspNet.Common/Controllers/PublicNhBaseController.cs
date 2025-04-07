using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class PublicNhBaseController : NhBaseController
{
    protected PublicNhBaseController(
        IMapper mapper, 
        ILogger logger, 
        IConfiguration config, 
        IStringLocalizer localizer, 
        IHttpCollectionProcessingService httpCollectionProcessingService
        ) : base(mapper, logger, config, localizer, httpCollectionProcessingService)
    {
    }

    [NonAction]
    protected virtual Task<SimpleCollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
        IBaseCollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    { 
        return GetCollectionResultModel<TEntity, TViewModel>(requestModel, queryable, null, cancellationToken: cancellationToken, defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<SimpleCollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
            IBaseCollectionRequestModel requestModel,
            IQueryable<TEntity> queryable,
            Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
            CancellationToken cancellationToken = default,
            params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
            where TEntity : class
            where TViewModel : class
    {
        var collectionRequestModel = new CollectionRequestModel()
        {
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage
        };

        var collectionResult = await _httpCollectionProcessingService.GetCollectionResultModelAsync<TEntity, TViewModel>(
            collectionRequestModel,
            queryable,
            resultQueryableFunc,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );

        return new SimpleCollectionResultModel<TViewModel>()
        {
            Items = collectionResult.Items,
            TotalCount = collectionResult.TotalCount,
            ResultCount = collectionResult.ResultCount,
            Page = collectionResult.Page,
            ItemsPerPage = collectionResult.ItemsPerPage
        };
    }

    [NonAction]
    protected virtual Task<IQueryable<TEntity>> GetCollectionResultQuery<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _httpCollectionProcessingService.GetCollectionResultQueryAsync<TEntity, TViewModel>(queryable, cancellationToken: cancellationToken, defaultOrderBy);
    }
}
