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

    [HttpGet]
    [Authorize(Policy = "app.address.view")]
    public Task<IActionResult> Get([FromQuery] AddressCollectionRequestModel requestModel)
    {
        return DoGet(requestModel);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "app.address.view")]
    public Task<IActionResult> GetById(Guid id)
    {
        return DoGetById(id);
    }

    [HttpPost]
    [Authorize(Policy = "app.address.manage")]
    public Task<IActionResult> Create([FromBody] AddressMutateModel mutateModel)
    {
        return DoCreate(mutateModel);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "app.address.manage")]
    public Task<IActionResult> Update([FromRoute] Guid id, [FromBody] AddressMutateModel mutateModel)
    {
        return DoUpdate(id, mutateModel);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "app.address.manage")]
    public Task<IActionResult> Delete([FromRoute] Guid id)
    {
        return DoDelete(id);
    }
}