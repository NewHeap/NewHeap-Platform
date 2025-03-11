using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json;
using System.Collections;
using System.ComponentModel;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public abstract partial class NhBaseController : ControllerBase
{
    protected readonly IConfiguration _config;
    protected readonly IStringLocalizer _localizer;
    protected readonly ILogger _logger;
    protected readonly IMapper _mapper;
    protected readonly IHttpCollectionRequestProcessingService _httpCollectionRequestProcessingService;

    public NhBaseController(
        IMapper mapper,
        ILogger logger,
        IConfiguration config,
        IStringLocalizer localizer,
        IHttpCollectionRequestProcessingService httpCollectionRequestProcessingService
    )
    {
        _mapper = mapper;
        _logger = logger;
        _config = config;
        _localizer = localizer;
        _httpCollectionRequestProcessingService = httpCollectionRequestProcessingService;
    }

    protected Guid? UserId
    {
        get
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                if (Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                {
                    return userId;
                }
            }

            return null;
        }
    }

    protected Guid? ActiveDivisionId => HttpContext.Request.GetActiveDivisionId();

    [NonAction]
    protected virtual IQueryable<T> ApplyDivisionFilter<T>(IQueryable<T> query, Expression<Func<T, bool>> expression)
    {
        if (!User.HasClaim(NhPlatformClaimTypes.Permission,
                Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            query = query.Where(expression);
        }

        return query;
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IdentityResult identityResult)
    {
        var localizedErrors = new List<LocalizedString>();

        foreach (var error in identityResult.Errors)
        {
            localizedErrors.Add(_localizer[error.Description]);
        }

        return BadRequest(localizedErrors);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(LocalizedString error)
    {
        var response = new BadRequestHttpResponseModel(error);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IEnumerable<LocalizedString> errors)
    {
        var response = new BadRequestHttpResponseModel(errors);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(string error)
    {
        var response = new BadRequestHttpResponseModel(error);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IEnumerable<string> errors)
    {
        var response = new BadRequestHttpResponseModel(errors);

        return BadRequest(response);
    }

    [NonAction]
    protected async Task<IActionResult> CollectionResultAsync<TModel, TViewModel>(IQueryable<TModel> query,
        Func<IQueryable<TModel>, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class where TViewModel : class
    {
        maxItemsPerPage ??= _httpCollectionRequestProcessingService.GetDefaultMaxItemsPerPage();
        var collectionRequestModel = GetCollectionRequestModel(maxItemsPerPage);

        var collectionResponse = await GetCollectionResponseModel<TModel, TViewModel>(
            collectionRequestModel,
            query,
            resultQueryableFunc,
            defaultOrderBy
        );

        return Ok(collectionResponse);
    }

    /// <summary>
    ///     Output a CSV file
    /// </summary>
    /// <typeparam name="TModel">Type to query</typeparam>
    /// <typeparam name="TRowModel">Type representing the csv rows</typeparam>
    /// <param name="query">Query object</param>
    /// <param name="convert">
    ///     Method to convert
    ///     <typeparam name="TModel"></typeparam>
    ///     to
    ///     <typeparam name="TRowModel"></typeparam>
    ///     .
    ///     When null the default mapper will be used.
    /// </param>
    /// <param name="resultQueryableFunc">Function for selecting
    ///     <typeparam name="TModel"></typeparam>
    ///     . Can be used to include extra data.
    /// </param>
    /// <param name="includeHeaders"></param>
    /// <param name="defaultOrderBy">Order by clauses</param>
    /// <param name="delimiter"></param>
    /// <returns></returns>
    protected async Task<IActionResult> Csv<TModel, TRowModel>(IQueryable<TModel> query,
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        char delimiter = ';',
        bool includeHeaders = false,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class
        where TRowModel : class
    {
        var collectionRequestModel = GetCollectionRequestModel(int.MaxValue);
        collectionRequestModel.ItemsPerPage = int.MaxValue;

        query = query.AsNoTracking();

        var collectionResponseModel =
            await GetCollectionResponseModel<TModel, TModel>(collectionRequestModel, query, resultQueryableFunc,
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
        await fileStream.WriteAsync(Encoding.UTF8.GetPreamble()); //Set file encoding to UTF-8

        if (includeHeaders)
        {
            await fileStream.WriteAsync(Encoding.UTF8.GetBytes(string.Join(delimiter, properties.Select(x => x.Name)) +
                                                               Environment.NewLine));
        }

        foreach (var row in rows)
        {
            await fileStream.WriteAsync(Encoding.UTF8.GetBytes(
                    string.Join(delimiter, properties.Select(p => p.GetMethod!.Invoke(row, null))) + Environment.NewLine
                )
            );
        }

        fileStream.Seek(0,
            SeekOrigin.Begin); // Reset stream to the start or else we're not going to write much to the response

        return File(fileStream, "text/csv");
    }


    [NonAction]
    protected virtual CollectionRequestModel GetCollectionRequestModel(int? maxItemsPerPage = null)
    {
        return _httpCollectionRequestProcessingService.GetCollectionRequestModel(maxItemsPerPage);
    }

    [NonAction]
    protected virtual Task<IQueryable<TEntity>> GetCollectionResponseQuery<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
       return _httpCollectionRequestProcessingService.GetCollectionResponseQueryAsync<TEntity, TViewModel>(queryable, defaultOrderBy);
    }

    /// <summary>
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <typeparam name="TViewModel"></typeparam>
    /// <param name="requestModel">RequestModel, <see cref="GetCollectionRequestModel" /></param>
    /// <param name="queryable">Collection to search</param>
    /// <param name="resultQueryableFunc">Function to execute on the result</param>
    /// <param name="defaultOrderBy">Order by properties</param>
    /// <returns></returns>
    [NonAction]
    protected virtual Task<CollectionResponseModel<TViewModel>> GetCollectionResponseModel<TEntity, TViewModel>(
        CollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _httpCollectionRequestProcessingService.GetCollectionResponseModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            defaultOrderBy
        );
    }

    [NonAction]
    protected virtual async Task<CollectionResponseModel<TViewModel>> GetCollectionResponseModel<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResponseModel<TEntity, TViewModel>(requestModel, queryable, null, defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<OkObjectResult> Ok<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return Ok(await GetCollectionResponseModel<TEntity, TViewModel>(queryable, defaultOrderBy));
    }
}

internal record SearchClosure(string Value);