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
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using WebAPI.DAL.Entities;
using WebAPI.Models.Mutate;
using WebAPI.Models.View;
using WebAPI.Services;

namespace WebAPI.Controllers;

[Route("")]
public class AddressController : ProtectedNhBaseController
{
    protected readonly AddressService _addressService;

    public AddressController(
        IStringLocalizer<AddressController> localizer,
        ILogger<AddressController> logger,
        IConfiguration config,
        IHttpCollectionProcessingService collectionRequestProcessingService,
        IMapper mapper,
        AddressService addressService
        )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService)
    {
        _addressService = addressService;
    }

    [NonAction]
    public Task<IQueryable<Address>> GetQueryableAsync()
    {
        var query = _addressService
            .GetRepository()
            .GetAll()
            .AsSplitQuery()
        ;

        query = AddBaseQueryableIncludesAsync(query);

        return Task.FromResult(query);
    }

    [NonAction]
    public IQueryable<Address> AddBaseQueryableIncludesAsync(IQueryable<Address> query)
    {
        return query
            as IQueryable<Address>
        ;
    }

    [HttpGet]
    [Authorize(Policy = "app.address.view")]
    public async Task<IActionResult> Get([FromQuery] AddressRequestModel requestModel)
    {
        requestModel ??= new AddressRequestModel();
        var query = (await GetQueryableAsync()).AsNoTracking();

        if (requestModel.CountryCodes?.Any() == true)
        {
            query = query
                .Where(x => requestModel.CountryCodes.Contains(x.CountryCode));
        }

        var result = await GetCollectionResultModel<Address, AddressViewModel>(query,
            (x => x.CreationDateTime, ListSortDirection.Ascending));

        return Ok(result);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "app.address.view")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var query = (await GetQueryableAsync()).AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            return NotFound();
        }

        var viewModel = _mapper.Map<AddressViewModel>(entity);

        return Ok(viewModel);
    }

    [HttpPost]
    [Authorize(Policy = "app.address.manage")]
    public async Task<IActionResult> Create([FromBody] AddressMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var createTaskResult = await _addressService.CreateAsync(mutateModel, UserId);

        if (!createTaskResult.Success)
        {
            createTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        var address = createTaskResult.Data;

        return CreatedAtAction(nameof(GetById), new { id = address.Id }, address);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "app.address.manage")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] AddressMutateModel mutateModel)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updateTaskResult = await _addressService.UpdateAsync(id, mutateModel, UserId);

        if (!updateTaskResult.Success)
        {
            updateTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "app.address.manage")]
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

        var deleteTaskResult = await _addressService.DeleteAsync(id, UserId);

        if (!deleteTaskResult.Success)
        {
            deleteTaskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }
}