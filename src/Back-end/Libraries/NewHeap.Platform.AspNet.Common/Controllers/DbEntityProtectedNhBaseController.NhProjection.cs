using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class DbEntityProtectedNhBaseController<
    TDbEntity,
    TCreateMutateModel,
    TUpdateMutateModel,
    TDeleteMutateModel,
    TViewModel,
    TBaseDbEntityService,
    TCollectionRequestModel>
    where TDbEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TViewModel : class
    where TBaseDbEntityService : IBaseDbEntityService<
        TDbEntity,
        TCreateMutateModel,
        TUpdateMutateModel,
        TDeleteMutateModel>
    where TCollectionRequestModel : CollectionRequestModel, new()
{
    [NonAction]
    protected virtual Task<IActionResult> DoGetProjected<TProjection>(
        TCollectionRequestModel requestModel,
        NhProjectionDefinition<TDbEntity, TProjection> projection,
        IQueryable<TDbEntity>? overrideQuery = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TProjection : class
    {
        return DoGetProjected(
            requestModel,
            projection,
            configureOptions: null,
            overrideQuery,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGetProjected<TProjection>(
        TCollectionRequestModel requestModel,
        NhProjectionDefinition<TDbEntity, TProjection> projection,
        Action<CollectionProcessingOptionsBuilder<TProjection, TProjection>>? configureOptions,
        IQueryable<TDbEntity>? overrideQuery = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TProjection, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TProjection : class
    {
        requestModel ??= new TCollectionRequestModel();
        var query = overrideQuery ?? await GetQueryableAsync(cancellationToken);

        if (defaultOrderBy.Length == 0)
        {
            defaultOrderBy = GetDefaultProjectedCollectionResultOrderBy<TProjection>();
        }

        try
        {
            var result = await CollectionProcessingServiceNhProjectionExtensions.GetProjectedCollectionResultModelAsync(
                _httpCollectionProcessingService,
                requestModel,
                query,
                projection,
                options =>
                {
                    ConfigureProjectedCollectionProcessing<TDbEntity, TProjection>(options);
                    configureOptions?.Invoke(options);
                },
                resultQueryableFunc: null,
                asNoTracking,
                cancellationToken,
                defaultOrderBy);

            return Ok(result);
        }
        catch (InvalidFilterCollectionResultException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return BadRequest(ModelState);
        }
    }
}
