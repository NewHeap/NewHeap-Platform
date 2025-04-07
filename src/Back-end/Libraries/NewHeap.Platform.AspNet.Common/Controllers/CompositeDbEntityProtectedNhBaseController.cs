using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class CompositeDbEntityProtectedNhBaseController<TDbEntity, TMutateModel, TServiceResultModel, TViewModel, TBaseDbEntityService, TCollectionRequestModel> : ProtectedNhBaseController
    where TDbEntity : class, IdDbEntity
    where TMutateModel : class
    where TServiceResultModel : class
    where TViewModel : class
    where TBaseDbEntityService : ICompositeBaseDbEntityService<TDbEntity, TMutateModel, TServiceResultModel>
    where TCollectionRequestModel : CollectionRequestModel, new()
{
    protected readonly TBaseDbEntityService _compositeDbEntityService;

    protected CompositeDbEntityProtectedNhBaseController(
        IMapper mapper, 
        ILogger logger, 
        IConfiguration config, 
        IStringLocalizer localizer, 
        IHttpCollectionProcessingService httpCollectionProcessingService,
        TBaseDbEntityService dbEntityService


        ) : base(mapper, logger, config, localizer, httpCollectionProcessingService)
    {
        _compositeDbEntityService = dbEntityService;
    }

    [NonAction]
    protected virtual (Expression<Func<TDbEntity, object>> orderByKey, ListSortDirection sortDirection)[] GetDefaultCollectionResultOrderBy()
    {
        return [
            (x => x.CreationDateTime, ListSortDirection.Ascending)
        ];
    }

    [NonAction]
    protected virtual Task<IQueryable<TDbEntity>> GetQueryableAsync(CancellationToken cancellationToken = default)
    {
        var query = _compositeDbEntityService
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
    protected virtual Task<IActionResult> DoGet(TCollectionRequestModel requestModel, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
    {
        return DoGet<TViewModel>(requestModel, overrideQuery, cancellationToken);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGet<TCustomViewModel>(TCollectionRequestModel requestModel, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
        where TCustomViewModel : class
    {
        requestModel ??= new TCollectionRequestModel();
        var query = overrideQuery?.AsNoTracking() ?? (await GetQueryableAsync(cancellationToken)).AsNoTracking();

        var result = await GetCollectionResultModel<TDbEntity, TCustomViewModel>(query, cancellationToken: cancellationToken, GetDefaultCollectionResultOrderBy());

        return Ok(result);
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGetById(Guid id, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
    { 
        return DoGetById<TViewModel>(id, overrideQuery, cancellationToken);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGetById<TCustomViewModel>(Guid id, IQueryable<TDbEntity>? overrideQuery = null, CancellationToken cancellationToken = default)
        where TCustomViewModel : class
    {
        var query = overrideQuery?.AsNoTracking() ?? (await GetQueryableAsync(cancellationToken)).AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity == null)
        {
            return NotFound();
        }

        var viewModel = _mapper.Map<TCustomViewModel>(entity);

        return Ok(viewModel);
    }

    [NonAction]
    protected virtual Task<IActionResult> DoCreate([FromBody] TMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        return DoCreate<TViewModel>(mutateModel, cancellationToken);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoCreate<TCustomViewModel>([FromBody] TMutateModel mutateModel, CancellationToken cancellationToken = default)
        where TCustomViewModel : class
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var createTaskResult = await _compositeDbEntityService.CreateAsync(mutateModel, UserId, cancellationToken: cancellationToken);

        if (!createTaskResult.Success)
        {
            createTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        var entity = createTaskResult.Data;
        var viewModel = _mapper.Map<TCustomViewModel>(entity);

        return Ok(viewModel);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoUpdate([FromRoute] Guid id, [FromBody] TMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updateTaskResult = await _compositeDbEntityService.UpdateAsync(id, mutateModel, UserId, cancellationToken: cancellationToken);

        if (!updateTaskResult.Success)
        {
            updateTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
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

        var deleteTaskResult = await _compositeDbEntityService.DeleteAsync(id, UserId, cancellationToken: cancellationToken);

        if (!deleteTaskResult.Success)
        {
            deleteTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }
}
