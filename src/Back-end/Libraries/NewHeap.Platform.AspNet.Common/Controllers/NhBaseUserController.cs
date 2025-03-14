using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract class NhBaseUserController : ProtectedNhBaseController
{
    protected const string READ_POLICY = "app.user.view";
    protected const string MANAGE_POLICY = "app.user.manage";
    protected readonly NhUserManager _userManager;

    public NhBaseUserController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseUserController> logger,
        IStringLocalizer<NhBaseUserController> localizer,
        NhUserManager userManager,
        IHttpCollectionProcessingService collectionRequestProcessingService
    )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService)
    {
        _userManager = userManager;
    }

    [NonAction]
    public Task<IQueryable<User>> GetQueryableAsync()
    {
        
        var query = _userManager
            .GetRepository()
            as IQueryable<User>
        ;

        query = ApplyDivisionFilter(query, x => x.DivisionUsers.Any(c => c.DivisionId == ActiveDivisionId));

        return Task.FromResult(query);
    }

    [HttpGet]
    [Authorize(Policy = "app.user.view")]
    public async Task<IActionResult> Get([FromQuery] UserCollectionRequestModel requestModel)
    {
        var currentUser = await _userManager.FindByIdAsync(UserId!.Value.ToString());

        var query = (await GetQueryableAsync()).AsNoTracking();

        if (requestModel?.Roles?.Any() == true)
        {
            query = query.Where(x => _userManager.GetRepository().Context.UserRoles.Any(c => c.UserId == x.Id && _userManager.GetRepository().Context.Roles.Where(v => requestModel.Roles.Contains(v.Name)).Select(c => c.Id).Contains(c.RoleId)));
        }

        if (requestModel?.DivisionIds?.Any() == true)
        {
            query = query.Where(x => x.DivisionUsers.Any(c => requestModel.DivisionIds.Contains(c.DivisionId)));
        }

        if (requestModel?.ExcludeNonDivisionAccess == true)
        {
            query = query.Where(x => x.DivisionUsers.Any(c => c.DivisionUserRoles.Any(v => v.DivisionRole.Name == "User")));
        }

        return await Ok<User, UserViewModel>(query,
            (x => x.Email, ListSortDirection.Ascending));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "app.user.view")]
    public async Task<IActionResult> Get([FromRoute] Guid id)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var user = await (await GetQueryableAsync())
            .FirstOrDefaultAsync(m => m.Id == id);

        if (user == null)
        {
            return NotFound();
        }

        var userViewModel = _mapper.Map<UserViewModel>(user);
        userViewModel.Roles = await _userManager.GetRolesAsync(user);

        return Ok(userViewModel);
    }
}