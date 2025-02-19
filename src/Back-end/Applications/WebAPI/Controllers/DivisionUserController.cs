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
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class DivisionUserController : BaseController<DivisionUserController, DivisionUser>
{
    private readonly DivisionUserService _divisionUserService;

    public DivisionUserController(
        IConfiguration config,
        IMapper mapper,
        ILogger<DivisionUserController> logger,
        DbLogService dbLogService,
        IStringLocalizer<DivisionUserController> localizer,
        NhUserManager userService,
        DivisionUserService divisionUserService
    )
        : base(mapper, logger, config, localizer, userService)
    {
        _divisionUserService = divisionUserService;
    }

    public Task<IQueryable<DivisionUser>> GetQueryableAsync()
    {
        IQueryable<DivisionUser> query = _divisionUserService
                .GetRepository()
                .GetAll()
                .Include(x => x.User)
                .Include(x => x.Division)
                .Include(x => x.DivisionUserRoles)
                .ThenInclude(x => x.DivisionRole)
            ;

        query = ApplyDivisionFilter(query, x => x.DivisionId == ActiveDivisionId);

        return Task.FromResult(query);
    }

    [HttpGet]
    [Authorize(Policy = "app.division.manage")]
    public async Task<IActionResult> Get()
    {
        var query = (await GetQueryableAsync()).AsNoTracking();

        var result =
            await GetCollectionResponseModel<DivisionUser, DivisionUserViewModel>(query,
                (x => x.User.Email, ListSortDirection.Ascending));

        return Ok(result);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "app.division.manage")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var query = (await GetQueryableAsync()).AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            return NotFound();
        }

        var viewModel = _mapper.Map<DivisionUserViewModel>(entity);

        return Ok(viewModel);
    }

    [HttpPost]
    [Authorize(Policy = "app.division.manage")]
    public async Task<IActionResult> Create([FromBody] DivisionUserMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var createTaskResult = await _divisionUserService.CreateAsync(mutateModel, UserId);

        if (!createTaskResult.Success)
        {
            createTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        var divisionUser = createTaskResult.Data;

        return CreatedAtAction(nameof(GetById), new { id = divisionUser.Id }, divisionUser);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "app.division.manage")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] DivisionUserMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updateTaskResult = await _divisionUserService.UpdateAsync(id, mutateModel, UserId);

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

        var deleteTaskResult = await _divisionUserService.DeleteAsync(id, UserId);

        if (!deleteTaskResult.Success)
        {
            deleteTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }
}