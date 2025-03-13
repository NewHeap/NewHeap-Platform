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

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class DbEntityProtectedNhBaseController<TDbEntity, TMutateModel, TViewModel, TBaseDbEntityService, TCollectionRequestModel> : ProtectedNhBaseController
    where TDbEntity : class, IdDbEntity
    where TMutateModel : class
    where TViewModel : class
    where TBaseDbEntityService : BaseDbEntityService<TDbEntity, TMutateModel, TViewModel, TBaseDbEntityService>
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
    protected virtual Task<IQueryable<TDbEntity>> GetQueryableAsync()
    {
        var query = _dbEntityService
            .GetRepository()
            .GetAll()
        ;

        query = AddBaseQueryableIncludesAsync(query);

        return Task.FromResult(query);
    }

    [NonAction]
    protected virtual IQueryable<TDbEntity> AddBaseQueryableIncludesAsync(IQueryable<TDbEntity> query)
    {
        return query
            as IQueryable<TDbEntity>
        ;
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGet([FromQuery] TCollectionRequestModel requestModel)
    {
        requestModel ??= new TCollectionRequestModel();
        var query = (await GetQueryableAsync()).AsNoTracking();

        var result = await GetCollectionResultModel<TDbEntity, TViewModel>(query,
            (x => x.CreationDateTime, ListSortDirection.Ascending));

        return Ok(result);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGetById(Guid id)
    {
        var query = (await GetQueryableAsync()).AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            return NotFound();
        }

        var viewModel = _mapper.Map<TViewModel>(entity);

        return Ok(viewModel);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoCreate([FromBody] TMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var createTaskResult = await _dbEntityService.CreateAsync(mutateModel, UserId);

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
    protected virtual async Task<IActionResult> DoUpdate([FromRoute] Guid id, [FromBody] TMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updateTaskResult = await _dbEntityService.UpdateAsync(id, mutateModel, UserId);

        if (!updateTaskResult.Success)
        {
            updateTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoDelete([FromRoute] Guid id)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var query = (await GetQueryableAsync()).AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            return NotFound();
        }

        var deleteTaskResult = await _dbEntityService.DeleteAsync(id, UserId);

        if (!deleteTaskResult.Success)
        {
            deleteTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }
}
