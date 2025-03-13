using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace WebAPI.Controllers;

[Route("api/[controller]")]
public class DivisionController : NhBaseController
{
    private readonly DivisionService _divisionService;

    public DivisionController(
        IConfiguration config,
        IMapper mapper,
        ILogger<DivisionController> logger,
        IStringLocalizer<DivisionController> localizer,
        DivisionService divisionService
    )
        : base(mapper, logger, config, localizer)
    {
        _divisionService = divisionService;
    }

    public Task<IQueryable<Division>> GetQueryableAsync()
    {
        var query = _divisionService
                .GetRepository()
                .GetAll()
            ;

        query = ApplyDivisionFilter(query, x => x.Id == ActiveDivisionId);

        return Task.FromResult(query);
    }

    [HttpGet]
    [Authorize(Policy = "app.division.view")]
    public async Task<IActionResult> Get()
    {
        var query = (await GetQueryableAsync()).AsNoTracking();

        var result =
            await GetCollectionResponseModel<Division, DivisionViewModel>(query,
                (x => x.Name, ListSortDirection.Ascending));

        return Ok(result);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "app.division.view")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var query = (await GetQueryableAsync()).AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            return NotFound();
        }

        var viewModel = _mapper.Map<DivisionViewModel>(entity);

        return Ok(viewModel);
    }

    [HttpPost]
    [Authorize(Policy = "app.division.manage")]
    public async Task<IActionResult> Create([FromBody] DivisionMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var createTaskResult = await _divisionService.CreateAsync(mutateModel, UserId);

        if (!createTaskResult.Success)
        {
            createTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        var division = createTaskResult.Data;

        return CreatedAtAction(nameof(GetById), new { id = division.Id }, division);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "app.division.manage")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] DivisionMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updateTaskResult = await _divisionService.UpdateAsync(id, mutateModel, UserId);

        if (!updateTaskResult.Success)
        {
            updateTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "app.division.manage")]
    public async Task<IActionResult> Delete([FromRoute] Guid id)
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

        var deleteTaskResult = await _divisionService.DeleteAsync(id, UserId);

        if (!deleteTaskResult.Success)
        {
            deleteTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

    [HttpGet("roles")]
    [Authorize(Policy = "app.division.view")]
    public async Task<IActionResult> GetDivisionRoles()
    {
        var query = _divisionService.GetRoleRepository().GetAll();

        var result =
            await GetCollectionResponseModel<DivisionRole, DivisionRoleViewModel>(query,
                (x => x.Name, ListSortDirection.Ascending));

        return Ok(result);
    }
}