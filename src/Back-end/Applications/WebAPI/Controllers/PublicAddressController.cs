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
using System.Threading;
using System.Threading.Tasks;
using WebAPI.DAL.Entities;
using WebAPI.Models.Mutate;
using WebAPI.Models.View;
using WebAPI.Services;

namespace WebAPI.Controllers;

[Route("")]
public class PublicAddressController : PublicNhBaseController
{
    protected readonly AddressService _addressService;

    public PublicAddressController(
        IStringLocalizer<PublicAddressController> localizer,
        ILogger<PublicAddressController> logger,
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
    [AllowAnonymous]
    public async Task<IActionResult> Get([FromQuery] PublicAddressRequestModel requestModel, CancellationToken cancellationToken)
    {
        requestModel ??= new PublicAddressRequestModel();
        var query = (await GetQueryableAsync()).AsNoTracking();

        if (requestModel.CountryCodes?.Any() == true)
        {
            query = query
                .Where(x => requestModel.CountryCodes.Contains(x.CountryCode));
        }

        var result = await GetCollectionResultModel<Address, AddressViewModel>(
            requestModel, 
            query
            , cancellationToken: cancellationToken,
            (x => x.CreationDateTime, ListSortDirection.Ascending)
        );

        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
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
}