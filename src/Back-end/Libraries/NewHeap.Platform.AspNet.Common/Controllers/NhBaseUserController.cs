using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
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

public abstract class NhBaseUserController<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TUserViewModel,
    TDivisionViewModel
    > : ProtectedNhBaseController
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TUserViewModel : NhUserViewModel<TDivisionViewModel>
    where TDivisionViewModel : NhDivisionViewModel
{
    protected const string READ_POLICY = "app.user.view";
    protected const string MANAGE_POLICY = "app.user.manage";
    protected readonly INhUserManager<TUser> _userManager;

    public NhBaseUserController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseUserController<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TUserViewModel, TDivisionViewModel>> logger,
        IStringLocalizer<NhBaseUserController<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TUserViewModel, TDivisionViewModel>> localizer,
        INhUserManager<TUser> userManager,
        IHttpCollectionProcessingService collectionRequestProcessingService
    )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService)
    {
        _userManager = userManager;
    }

    [NonAction]
    public virtual Task<IQueryable<TUser>> GetQueryableAsync(CancellationToken cancellationToken)
    {
        
        var query = _userManager
            .GetRepository()
            .GetAll()
            as IQueryable<TUser>
        ;

        query = ApplyDivisionFilter(query, x => x.DivisionUsers.Any(c => c.DivisionId == ActiveDivisionId));

        return Task.FromResult(query);
    }

    [HttpGet]
    [Authorize(Policy = "app.user.view")]
    public virtual async Task<IActionResult> Get([FromQuery] UserCollectionRequestModel requestModel, CancellationToken cancellationToken = default)
    {
        var currentUser = await _userManager.FindByIdAsync(UserId!.Value.ToString());

        var query = (await GetQueryableAsync(cancellationToken)).AsNoTracking();

        if (requestModel?.Roles?.Any() == true)
        {
            var dbContext = (IdentityDbContext<TUser, NhUserRole, Guid>)_userManager.GetRepository().Context;
            query = query.Where(x => dbContext.UserRoles.Any(c => c.UserId == x.Id && dbContext.Roles.Where(v => requestModel.Roles.Contains(v.Name!)).Select(c => c.Id).Contains(c.RoleId)));
        }

        if (requestModel?.DivisionIds?.Any() == true)
        {
            query = query.Where(x => x.DivisionUsers.Any(c => requestModel.DivisionIds.Contains(c.DivisionId)));
        }

        if (requestModel?.ExcludeNonDivisionAccess == true)
        {
            query = query.Where(x => x.DivisionUsers.Any(c => c.DivisionUserRoles.Any()));
        }

        return await Ok<TUser, TUserViewModel>(query, cancellationToken: cancellationToken,
            (x => x.Email!, ListSortDirection.Ascending));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "app.user.view")]
    public virtual async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var user = await (await GetQueryableAsync(cancellationToken))
            .FirstOrDefaultAsync(m => m.Id == id);

        if (user == null)
        {
            return NotFound();
        }

        var userViewModel = _mapper.Map<TUserViewModel>(user);
        userViewModel.Roles = await _userManager.GetRolesAsync(user);

        return Ok(userViewModel);
    }
}