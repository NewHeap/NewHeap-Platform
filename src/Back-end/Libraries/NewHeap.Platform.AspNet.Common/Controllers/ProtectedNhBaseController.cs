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
    private async Task<IActionResult> _GetCollectionResultAsync<TModel, TViewModel>(
        IQueryable<TModel> query,
        List<(Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
        )
        where TModel : class where TViewModel : class
    {
        maxItemsPerPage ??= _httpCollectionProcessingService.GetDefaultMaxItemsPerPage();
        var collectionRequestModel = GetCollectionRequestModel(maxItemsPerPage);

        var collectionResponse = await GetCollectionResultModel<TModel, TViewModel>(
            collectionRequestModel,
            query,
            resultQueryableFunc,
            cancellationToken: cancellationToken,
            defaultOrderBy.ToArray()
        );

        return Ok(collectionResponse);
    }

    [NonAction]
    protected Task<IActionResult> GetCollectionResultAsync<TModel, TViewModel>(
        IQueryable<TModel> query,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class where TViewModel : class
    {
        return GetCollectionResultAsync<TModel, TViewModel>(
            query,
            null,
            null,
            cancellationToken,
            defaultOrderBy
        );
    }

    [NonAction]
    protected Task<IActionResult> GetCollectionResultAsync<TModel, TViewModel>(
        IQueryable<TModel> query,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class where TViewModel : class
    {
        return GetCollectionResultAsync<TModel, TViewModel>(
            query,
            resultQueryableFunc,
            maxItemsPerPage,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy
        );
    }

    [NonAction]
    protected Task<IActionResult> GetCollectionResultAsync<TModel, TViewModel>(
        IQueryable<TModel> query,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class where TViewModel : class
    {
        return _GetCollectionResultAsync<TModel, TViewModel>(
            query,
            defaultOrderBy.ToList(),
            resultQueryableFunc,
            maxItemsPerPage,
            asNoTracking,
            cancellationToken
        );
    }

    [NonAction]
    protected Task<IActionResult> GetCollectionResultAsync<TModel, TViewModel>(
        IQueryable<TModel> query,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        CancellationToken cancellationToken = default)
        where TModel : class where TViewModel : class
    {
        return GetCollectionResultAsync<TModel, TViewModel>(
            query,
            resultQueryableFunc,
            maxItemsPerPage,
            asNoTracking: true,
            cancellationToken
        );
    }

    [NonAction]
    protected Task<IActionResult> GetCollectionResultAsync<TModel, TViewModel>(
    IQueryable<TModel> query,
    Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
    int? maxItemsPerPage = null,
    bool asNoTracking = true,
    CancellationToken cancellationToken = default)
    where TModel : class where TViewModel : class
    {
        return _GetCollectionResultAsync<TModel, TViewModel>(
            query,
            new List<(Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)>(),
            resultQueryableFunc,
            maxItemsPerPage,
            asNoTracking,
            cancellationToken
        );
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
    protected virtual Task<IQueryable<TEntity>> GetCollectionResultQuery<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return _httpCollectionProcessingService.GetCollectionResultQueryAsync<TEntity, TViewModel>(queryable, cancellationToken: cancellationToken);
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
        return GetCollectionResultModel<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );
    }

    [NonAction]
    protected virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _httpCollectionProcessingService.GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: asNoTracking,
            cancellationToken: cancellationToken,
            defaultOrderBy
        );
    }

    [NonAction]
    protected virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
       ICollectionRequestModel requestModel,
       IQueryable<TEntity> queryable,
       Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
       CancellationToken cancellationToken = default)
       where TEntity : class
       where TViewModel : class
    {
        return GetCollectionResultModel<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken: cancellationToken
        );
    }

    [NonAction]
    protected virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
       ICollectionRequestModel requestModel,
       IQueryable<TEntity> queryable,
       Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
       bool asNoTracking = true,
       CancellationToken cancellationToken = default)
       where TEntity : class
       where TViewModel : class
    {
        return _httpCollectionProcessingService.GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: asNoTracking,
            cancellationToken: cancellationToken
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
        return await GetCollectionResultModel<TEntity, TViewModel>(queryable, asNoTracking: true, cancellationToken: cancellationToken, defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
    IQueryable<TEntity> queryable,
    bool asNoTracking = true,
    CancellationToken cancellationToken = default,
    params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
    where TEntity : class
    where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResultModel<TEntity, TViewModel>(requestModel, queryable, null, asNoTracking: asNoTracking, cancellationToken: cancellationToken, defaultOrderBy);
    }


    [NonAction]
    protected virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
       IQueryable<TEntity> queryable,
       CancellationToken cancellationToken = default)
       where TEntity : class
       where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResultModel<TEntity, TViewModel>(queryable, asNoTracking: true, cancellationToken: cancellationToken);
    }

    [NonAction]
    protected virtual async Task<CollectionResultModel<TViewModel>> GetCollectionResultModel<TEntity, TViewModel>(
       IQueryable<TEntity> queryable,
       bool asNoTracking = true,
       CancellationToken cancellationToken = default)
       where TEntity : class
       where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResultModel<TEntity, TViewModel>(requestModel, queryable, null, asNoTracking: asNoTracking, cancellationToken: cancellationToken);
    }


    [NonAction]
    protected virtual async Task<OkObjectResult> Ok<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return Ok(await GetCollectionResultModel<TEntity, TViewModel>(queryable, asNoTracking: true, cancellationToken, defaultOrderBy));
    }

    [NonAction]
    protected virtual async Task<OkObjectResult> Ok<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return Ok(await GetCollectionResultModel<TEntity, TViewModel>(queryable, asNoTracking: asNoTracking, cancellationToken, defaultOrderBy));
    }

    [NonAction]
    protected virtual async Task<OkObjectResult> Ok<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return Ok(await GetCollectionResultModel<TEntity, TViewModel>(queryable, cancellationToken));
    }

    [NonAction]
    protected virtual async Task<OkObjectResult> Ok<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        return Ok(await GetCollectionResultModel<TEntity, TViewModel>(queryable, asNoTracking, cancellationToken));
    }

    private async Task<IActionResult> _Csv<TModel, TRowModel>(IQueryable<TModel> query,
        List<(Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        char delimiter = ';',
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        bool includeHeaders = false,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
        )
        where TModel : class
        where TRowModel : class
    {
        var collectionRequestModel = GetCollectionRequestModel(int.MaxValue);
        collectionRequestModel.ItemsPerPage = int.MaxValue;

        query = query.AsNoTracking();

        var collectionResponseModel =
            await GetCollectionResultModel<TModel, TModel>(
                collectionRequestModel, 
                query, 
                resultQueryableFunc,
                asNoTracking: asNoTracking,
                cancellationToken: cancellationToken,
                defaultOrderBy.ToArray());

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

    protected Task<IActionResult> Csv<TModel, TRowModel>(IQueryable<TModel> query,
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        char delimiter = ';',
        bool includeHeaders = false,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class
        where TRowModel : class
    {
        return _Csv(query, defaultOrderBy.ToList(), delimiter, convert, resultQueryableFunc, includeHeaders, asNoTracking: true, cancellationToken);
    }

    protected Task<IActionResult> Csv<TModel, TRowModel>(IQueryable<TModel> query,
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        char delimiter = ';',
        bool includeHeaders = false,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class
        where TRowModel : class
    {
        return _Csv(query, defaultOrderBy.ToList(), delimiter, convert, resultQueryableFunc, includeHeaders, asNoTracking: asNoTracking, cancellationToken);
    }

    protected Task<IActionResult> Csv<TModel, TRowModel>(IQueryable<TModel> query,
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        char delimiter = ';',
        bool includeHeaders = false,
        CancellationToken cancellationToken = default
    )
        where TModel : class
        where TRowModel : class
    {
        return _Csv(query, new List<(Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)>(), delimiter, convert, resultQueryableFunc, includeHeaders, asNoTracking: true, cancellationToken);
    }

    protected Task<IActionResult> Csv<TModel, TRowModel>(IQueryable<TModel> query,
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, CancellationToken, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        char delimiter = ';',
        bool includeHeaders = false,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
    )
        where TModel : class
        where TRowModel : class
    {
        return _Csv(query, new List<(Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)>(), delimiter, convert, resultQueryableFunc, includeHeaders, asNoTracking: asNoTracking, cancellationToken);
    }
}
