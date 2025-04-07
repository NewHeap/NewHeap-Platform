using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using WebAPI.DAL.Entities;
using WebAPI.Models.Mutate;
using WebAPI.Models.View;
using WebAPI.Services;

namespace WebAPI.Controllers;

[Route("address")]
public class AddressController : DbEntityProtectedNhBaseController<Address, AddressMutateModel, AddressViewModel, AddressService, AddressCollectionRequestModel>
{
    public AddressController(
        IStringLocalizer<AddressController> localizer,
        ILogger<AddressController> logger,
        IConfiguration config,
        IHttpCollectionProcessingService collectionRequestProcessingService,
        IMapper mapper,
        AddressService addressService
        )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService, addressService)
    {
    }

    protected override (Expression<Func<Address, object>> orderByKey, ListSortDirection sortDirection)[] GetDefaultCollectionResultOrderBy()
    {
        return [
            (x => x.CreationDateTime, ListSortDirection.Ascending)
        ];
    }

    [HttpGet]
    [Authorize(Policy = "app.address.view")]
    public Task<IActionResult> Get([FromQuery] AddressCollectionRequestModel requestModel, CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, cancellationToken: cancellationToken);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "app.address.view")]
    public Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        return DoGetById(id, cancellationToken: cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = "app.address.manage")]
    public Task<IActionResult> Create([FromBody] AddressMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        return DoCreate(mutateModel, cancellationToken: cancellationToken);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "app.address.manage")]
    public Task<IActionResult> Update([FromRoute] Guid id, [FromBody] AddressMutateModel mutateModel, CancellationToken cancellationToken = default)
    {
        return DoUpdate(id, mutateModel, cancellationToken: cancellationToken);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "app.address.manage")]
    public Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        return DoDelete(id, cancellationToken: cancellationToken);
    }
}