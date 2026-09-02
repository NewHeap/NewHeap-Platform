using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract class NhBaseDivisionUserController<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TDivisionUserMutateModel,
    TUserViewModel,
    TDivisionViewModel,
    TDivisionRoleViewModel,
    TDivisionUserViewModel
    > : DbEntityProtectedNhBaseController<TDivisionUser, TDivisionUserMutateModel, TDivisionUserViewModel, NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionUserMutateModel>, DivisionUserCollectionRequestModel>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>, new()
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>, new()
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivisionUserMutateModel : NhDivisionUserMutateModel
    where TUserViewModel : NhUserViewModel<TDivisionViewModel>
    where TDivisionViewModel : NhDivisionViewModel
    where TDivisionRoleViewModel : NhDivisionRoleViewModel
    where TDivisionUserViewModel : DivisionUserViewModel<TUserViewModel, TDivisionViewModel, TDivisionRoleViewModel>
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public NhBaseDivisionUserController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseDivisionUserController<TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TDivisionUserMutateModel,
        TUserViewModel,
        TDivisionViewModel,
        TDivisionRoleViewModel,
        TDivisionUserViewModel>> logger,
        IStringLocalizer<NhBaseDivisionUserController<TUser,
        TDivision,
        TDivisionUser,
        TDivisionRole,
        TDivisionUserRole,
        TDivisionRoleClaim,
        TDivisionUserMutateModel,
        TUserViewModel,
        TDivisionViewModel,
        TDivisionRoleViewModel,
        TDivisionUserViewModel>> localizer,
        INhUserManager userService,
        NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionUserMutateModel> divisionUserService,
        IHttpCollectionProcessingService collectionRequestProcessingService
    )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService, divisionUserService)
    {
    }

    protected override (Expression<Func<TDivisionUser, object>> orderByKey, ListSortDirection sortDirection)[] GetDefaultCollectionResultOrderBy()
    {
        return [
            (x => x.User.Email!, ListSortDirection.Ascending)
        ];
    }

    protected override Task<IQueryable<TDivisionUser>> GetQueryableAsync(CancellationToken cancellationToken = default)
    {
        IQueryable<TDivisionUser> query = _dbEntityService
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
    [Authorize(Policy = READ_POLICY)]
    public virtual Task<IActionResult> Get([FromQuery] DivisionUserCollectionRequestModel requestModel, CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = READ_POLICY)]
    public virtual Task<IActionResult> GetById(Guid id)
    {
        return DoGetById(id);
    }

    [HttpPost]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Create([FromBody] TDivisionUserMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        return DoCreate(mutateModel);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Update([FromRoute] Guid id, [FromBody] TDivisionUserMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        return DoUpdate(id, mutateModel);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        return DoDelete(id);
    }
}