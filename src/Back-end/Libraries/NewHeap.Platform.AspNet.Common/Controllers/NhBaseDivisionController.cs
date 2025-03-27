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

public abstract class NhBaseDivisionController : DbEntityProtectedNhBaseController<NhDivision, DivisionMutateModel, DivisionViewModel, DivisionService, DivisionCollectionRequestModel>
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public NhBaseDivisionController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseDivisionController> logger,
        IStringLocalizer<NhBaseDivisionController> localizer,
        DivisionService divisionService,
        IHttpCollectionProcessingService collectionRequestProcessingService
    )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService, divisionService)
    {
    }

    protected override Task<IQueryable<NhDivision>> GetQueryableAsync()
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
    public virtual Task<IActionResult> Get([FromQuery] DivisionCollectionRequestModel requestModel)
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
    public virtual Task<IActionResult> Create([FromBody] DivisionMutateModel mutateModel)
    {
        return DoCreate(mutateModel);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Update([FromRoute] Guid id, [FromBody] DivisionMutateModel mutateModel)
    {
        return DoUpdate(id, mutateModel);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = MANAGE_POLICY)]
    public virtual Task<IActionResult> Delete([FromRoute] Guid id)
    {
        return DoDelete(id);
    }

    [HttpGet("roles")]
    [Authorize(Policy = READ_POLICY)]
    public virtual async Task<IActionResult> GetDivisionRoles()
    {
        var query = _dbEntityService.GetRoleRepository().GetAll();

        var result =
            await GetCollectionResultModel<NhDivisionRole, DivisionRoleViewModel>(query,
                (x => x.Name, ListSortDirection.Ascending));

        return Ok(result);
    }
}