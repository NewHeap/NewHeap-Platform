using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class ProtectedNhBaseController : NhBaseController
{
    protected ProtectedNhBaseController(
        IMapper mapper, 
        ILogger logger, 
        IConfiguration config, 
        IStringLocalizer localizer, 
        IHttpCollectionProcessingService httpCollectionProcessingService
        ) : base(mapper, logger, config, localizer, httpCollectionProcessingService)
    {
    }

    [NonAction]
    protected async Task<IActionResult> GetCollectionResultAsync<TModel, TViewModel>(
        IQueryable<TModel> query,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class where TViewModel : class
    {
        maxItemsPerPage ??= _httpCollectionProcessingService.GetDefaultMaxItemsPerPage();
        var collectionRequestModel = GetCollectionRequestModel(maxItemsPerPage);

        var collectionResponse = await GetCollectionResultModel<TModel, TViewModel>(
            collectionRequestModel,
            query,
            resultQueryableFunc,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );

        return Ok(collectionResponse);
    }

    [NonAction]
    protected virtual ICollectionRequestModel GetCollectionRequestModel(int? maxItemsPerPage = null)
    {
        return _httpCollectionProcessingService.GetCollectionRequestModel(maxItemsPerPage);
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

    [NonAction]
    protected virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _httpCollectionProcessingService.GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );
    }

    [NonAction]
    protected virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResultModel<TEntity, TViewModel>(requestModel, queryable, null, cancellationToken: cancellationToken, defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<OkObjectResult> Ok<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return Ok(await GetCollectionResultModel<TEntity, TViewModel>(queryable, cancellationToken, defaultOrderBy));
    }

    protected async Task<IActionResult> Csv<TModel, TRowModel>(IQueryable<TModel> query,
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        char delimiter = ';',
        bool includeHeaders = false,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class
        where TRowModel : class
    {
        var collectionRequestModel = GetCollectionRequestModel(int.MaxValue);
        collectionRequestModel.ItemsPerPage = int.MaxValue;

        query = query.AsNoTracking();

        var collectionResponseModel =
            await GetCollectionResultModel<TModel, TModel>(collectionRequestModel, query, resultQueryableFunc,
                cancellationToken: cancellationToken,
                defaultOrderBy);

        IEnumerable<TRowModel>? rows = null;

        if (convert != null)
        {
            rows = convert(collectionResponseModel.Items);
        }
        else if (typeof(TRowModel) != typeof(TModel))
        {
            rows = _mapper.Map<IEnumerable<TRowModel>>(collectionResponseModel.Items);
        }
        else
        {
            rows = collectionResponseModel.Items.Select(x => (x as TRowModel)!).ToList();
        }

        var rowType = typeof(TRowModel);
        var properties = rowType.GetProperties();
        var fileStream = new MemoryStream(); // This is disposed by the File method call
        await fileStream.WriteAsync(Encoding.UTF8.GetPreamble(), cancellationToken: cancellationToken); //Set file encoding to UTF-8

        if (includeHeaders)
        {
            await fileStream.WriteAsync(Encoding.UTF8.GetBytes(string.Join(delimiter, properties.Select(x => x.Name)) +
                                                               Environment.NewLine), cancellationToken: cancellationToken);
        }

        foreach (var row in rows)
        {
            await fileStream.WriteAsync(Encoding.UTF8.GetBytes(
                    string.Join(delimiter, properties.Select(p => p.GetMethod!.Invoke(row, null))) + Environment.NewLine
                ), cancellationToken: cancellationToken
            );
        }

        fileStream.Seek(0,
            SeekOrigin.Begin); // Reset stream to the start or else we're not going to write much to the response

        return File(fileStream, "text/csv");
    }

}
