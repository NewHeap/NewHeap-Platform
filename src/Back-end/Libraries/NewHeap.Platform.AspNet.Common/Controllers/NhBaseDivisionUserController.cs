using AutoMapper;
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

public abstract class NhBaseDivisionUserController : DbEntityProtectedNhBaseController<DivisionUser, DivisionUserMutateModel, DivisionUserViewModel, DivisionUserService, DivisionUserCollectionRequestModel>
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public NhBaseDivisionUserController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseDivisionUserController> logger,
        IStringLocalizer<NhBaseDivisionUserController> localizer,
        INhUserManager userService,
        DivisionUserService divisionUserService,
        IHttpCollectionProcessingService collectionRequestProcessingService
    )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService, divisionUserService)
    {
    }

    protected override (Expression<Func<DivisionUser, object>> orderByKey, ListSortDirection sortDirection)[] GetDefaultCollectionResultOrderBy()
    {
        return [
            (x => x.User.Email, ListSortDirection.Ascending)
        ];
    }

    protected override Task<IQueryable<DivisionUser>> GetQueryableAsync()
    {
        IQueryable<DivisionUser> query = _dbEntityService
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
    public virtual Task<IActionResult> Get([FromQuery] DivisionUserCollectionRequestModel requestModel)
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
    public virtual Task<IActionResult> Create([FromBody] DivisionUserMutateModel mutateModel)
    {
        return DoCreate(mutateModel);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Update([FromRoute] Guid id, [FromBody] DivisionUserMutateModel mutateModel)
    {
        return DoUpdate(id, mutateModel);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Delete([FromRoute] Guid id)
    {
        return DoDelete(id);
    }
}