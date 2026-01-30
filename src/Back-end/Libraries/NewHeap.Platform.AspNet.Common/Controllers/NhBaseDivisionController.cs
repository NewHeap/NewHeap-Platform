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

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract class NhBaseDivisionController<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TDivisionMutateModel,
    TDivisionViewModel,
    TDivisionRoleViewModel
    > : DbEntityProtectedNhBaseController<TDivision, TDivisionMutateModel, TDivisionViewModel, NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionMutateModel>, DivisionCollectionRequestModel>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>, new()
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivisionMutateModel : NhDivisionMutateModel
    where TDivisionViewModel : NhDivisionViewModel
    where TDivisionRoleViewModel : NhDivisionRoleViewModel
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public NhBaseDivisionController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseDivisionController<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionMutateModel, TDivisionViewModel, TDivisionRoleViewModel>> logger,
        IStringLocalizer<NhBaseDivisionController<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionMutateModel, TDivisionViewModel, TDivisionRoleViewModel>> localizer,
        NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionMutateModel> divisionService,
        IHttpCollectionProcessingService collectionRequestProcessingService
    )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService, divisionService)
    {
    }

    protected override Task<IQueryable<TDivision>> GetQueryableAsync(CancellationToken cancellationToken = default)
    {
        var query = _dbEntityService
                .GetRepository()
                .GetAll()
            ;

        query = ApplyDivisionFilter(query, x => x.Id == ActiveDivisionId);

        return Task.FromResult(query);
    }

    [HttpGet]
    [Authorize(Policy = READ_POLICY)]
    public virtual Task<IActionResult> Get([FromQuery] DivisionCollectionRequestModel requestModel, CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, cancellationToken);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = READ_POLICY)]
    public virtual Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return DoGetById(id);
    }

    [HttpPost]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Create([FromBody] TDivisionMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        return DoCreate(mutateModel);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Update([FromRoute] Guid id, [FromBody] TDivisionMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        return DoUpdate(id, mutateModel);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        return DoDelete(id);
    }

    [HttpGet("roles")]
    [Authorize(Policy = READ_POLICY)]
    public virtual async Task<IActionResult> GetDivisionRoles(CancellationToken cancellationToken = default)
    {
        var query = _dbEntityService.GetRoleRepository().GetAll();

        var result =
            await GetCollectionResultModel<TDivisionRole, TDivisionRoleViewModel>(query, cancellationToken: cancellationToken,
                (x => x.Name, ListSortDirection.Ascending));

        return Ok(result);
    }
}