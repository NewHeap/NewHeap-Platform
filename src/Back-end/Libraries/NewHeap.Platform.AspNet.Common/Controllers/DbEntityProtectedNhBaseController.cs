using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class DbEntityProtectedNhBaseController<TDbEntity, TMutateModel, TViewModel, TBaseDbEntityService, TCollectionRequestModel> : DbEntityProtectedNhBaseController<TDbEntity, TMutateModel, TMutateModel, TMutateModel, TViewModel, TBaseDbEntityService, TCollectionRequestModel>
    where TDbEntity : class, IdDbEntity
    where TMutateModel : class
    where TViewModel : class
    where TBaseDbEntityService : IBaseDbEntityService<TDbEntity, TMutateModel>
    where TCollectionRequestModel : CollectionRequestModel, new()
{
    protected DbEntityProtectedNhBaseController(IMapper mapper, ILogger logger, IConfiguration config, IStringLocalizer localizer, IHttpCollectionProcessingService httpCollectionProcessingService, TBaseDbEntityService dbEntityService) : base(mapper, logger, config, localizer, httpCollectionProcessingService, dbEntityService)
    {
    }
}

public abstract partial class DbEntityProtectedNhBaseController<TDbEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel, TBaseDbEntityService, TCollectionRequestModel> : ProtectedNhBaseController
    where TDbEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TViewModel : class
    where TBaseDbEntityService : IBaseDbEntityService<TDbEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel>
    where TCollectionRequestModel : CollectionRequestModel, new()
{
    protected readonly TBaseDbEntityService _dbEntityService;

    protected DbEntityProtectedNhBaseController(
        IMapper mapper, 
        ILogger logger, 
        IConfiguration config, 
        IStringLocalizer localizer, 
        IHttpCollectionProcessingService httpCollectionProcessingService,
        TBaseDbEntityService dbEntityService


        ) : base(mapper, logger, config, localizer, httpCollectionProcessingService)
    {
        _dbEntityService = dbEntityService;
    }

    [NonAction]
    protected virtual (Expression<Func<TDbEntity, object>> orderByKey, ListSortDirection sortDirection)[] GetDefaultCollectionResultOrderBy()
    {
        return [
            (x => x.CreationDateTime, ListSortDirection.Ascending)
        ];
    }

    [NonAction]
    protected virtual void ConfigureCollectionProcessing(CollectionProcessingOptionsBuilder<TDbEntity, TViewModel> options)
    {
    }

    [NonAction]
    protected virtual void ConfigureCollectionProcessing<TCustomViewModel>(CollectionProcessingOptionsBuilder<TDbEntity, TCustomViewModel> options)
        where TCustomViewModel : class
    {
        if (typeof(TCustomViewModel) == typeof(TViewModel))
        {
            ConfigureCollectionProcessing((CollectionProcessingOptionsBuilder<TDbEntity, TViewModel>)(object)options);
        }
    }

    [NonAction]
    protected override void ConfigureCollectionProcessing<TEntity, TCustomViewModel>(CollectionProcessingOptionsBuilder<TEntity, TCustomViewModel> options)
    {
        base.ConfigureCollectionProcessing(options);

        if (typeof(TEntity) == typeof(TDbEntity))
        {
            ConfigureCollectionProcessing((CollectionProcessingOptionsBuilder<TDbEntity, TCustomViewModel>)(object)options);
        }
    }

    [NonAction]
    protected virtual Task<IQueryable<TDbEntity>> GetQueryableAsync(CancellationToken cancellationToken = default)
    {
        var query = _dbEntityService
            .GetRepository()
            .GetAll()
        ;

        query = AddBaseQueryableIncludesAsync(query, cancellationToken);

        return Task.FromResult(query);
    }

    [NonAction]
    protected virtual IQueryable<TDbEntity> AddBaseQueryableIncludesAsync(IQueryable<TDbEntity> query, CancellationToken cancellationToken = default)
    {
        return query
            as IQueryable<TDbEntity>
        ;
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGet(TCollectionRequestModel requestModel)
    {
        return DoGet(requestModel, default);
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGet(TCollectionRequestModel requestModel, CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, null, cancellationToken);
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGet(TCollectionRequestModel requestModel, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, overrideQuery, asNotracking: true, cancellationToken);
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGet(TCollectionRequestModel requestModel, IQueryable<TDbEntity>? overrideQuery, bool asNotracking, CancellationToken cancellationToken)
    {
        return DoGet<TViewModel>(requestModel, overrideQuery, asNotracking: asNotracking, cancellationToken);
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGet<TCustomViewModel>(TCollectionRequestModel requestModel, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
        where TCustomViewModel : class
    {
        return DoGet<TCustomViewModel>(requestModel, overrideQuery, asNotracking: true, cancellationToken);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGet<TCustomViewModel>(TCollectionRequestModel requestModel, IQueryable<TDbEntity>? overrideQuery, bool asNotracking, CancellationToken cancellationToken = default)
        where TCustomViewModel : class
    {
        requestModel ??= new TCollectionRequestModel();
        var query = overrideQuery?.AsNoTracking() ?? (await GetQueryableAsync(cancellationToken)).AsNoTracking();

        try
        {
            var result = await GetCollectionResultModel<TDbEntity, TCustomViewModel>(requestModel, query, null, asNoTracking: asNotracking, cancellationToken: cancellationToken, GetDefaultCollectionResultOrderBy());

            return Ok(result);
        }
        catch (InvalidFilterCollectionResultException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return BadRequest(ModelState);
        }
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGetById(Guid id, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
    { 
        return DoGetById<TViewModel>(id, overrideQuery, cancellationToken);
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGetById<TCustomViewModel>(Guid id, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
        where TCustomViewModel : class
    {
        return DoGetById<TCustomViewModel>(id, overrideQuery, asNoTracking: true, cancellationToken);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGetById<TCustomViewModel>(Guid id, IQueryable<TDbEntity>? overrideQuery, bool asNoTracking, CancellationToken cancellationToken = default)
    where TCustomViewModel : class
    {
        var query = overrideQuery ?? (await GetQueryableAsync(cancellationToken));

        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        var entity = await query.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity == null)
        {
            return NotFound();
        }

        var viewModel = _mapper.Map<TCustomViewModel>(entity);

        return Ok(viewModel);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoCreate([FromBody] TCreateMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var createTaskResult = await _dbEntityService.CreateAsync(mutateModel, UserId, cancellationToken: cancellationToken);

        if (!createTaskResult.Success)
        {
            createTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        var entity = createTaskResult.Data;
        var viewModel = _mapper.Map<TViewModel>(entity);

        return Ok(viewModel);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoUpdate([FromRoute] Guid id, [FromBody] TUpdateMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updateTaskResult = await _dbEntityService.UpdateAsync(id, mutateModel, UserId, cancellationToken: cancellationToken);

        if (!updateTaskResult.Success)
        {
            updateTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

    [NonAction]
    protected virtual Task<IActionResult> DoUpdatePartial(
        [FromRoute] Guid id,
        [FromBody] JObject? partialUpdate,
        CancellationToken cancellationToken = default)
    {
        return NhPartialUpdateControllerExecutor.ExecuteAsync<TUpdateMutateModel, TDbEntity>(
            this,
            _localizer,
            partialUpdate,
            CanPartiallyUpdateProperty,
            setters => _dbEntityService.UpdatePartialAsync(
                id,
                setters,
                committedByUserId: UserId,
                cancellationToken: cancellationToken));
    }

    [NonAction]
    protected virtual bool CanPartiallyUpdateProperty(string propertyName)
    {
        return true;
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoDelete([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var query = (await GetQueryableAsync(cancellationToken)).AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity == null)
        {
            return NotFound();
        }

        var deleteTaskResult = await _dbEntityService.DeleteAsync(id, UserId, cancellationToken: cancellationToken);

        if (!deleteTaskResult.Success)
        {
            deleteTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }
}
