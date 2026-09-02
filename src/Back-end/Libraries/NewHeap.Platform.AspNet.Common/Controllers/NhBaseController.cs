using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json;
using System.Collections;
using System.ComponentModel;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public abstract partial class NhBaseController : ControllerBase
{
    protected readonly IConfiguration _config;
    protected readonly IStringLocalizer _localizer;
    protected readonly ILogger _logger;
    protected readonly IMapper _mapper;
    protected readonly IHttpCollectionProcessingService _httpCollectionProcessingService;

    public NhBaseController(
        IMapper mapper,
        ILogger logger,
        IConfiguration config,
        IStringLocalizer localizer,
        IHttpCollectionProcessingService httpCollectionProcessingService
    )
    {
        _mapper = mapper;
        _logger = logger;
        _config = config;
        _localizer = localizer;
        _httpCollectionProcessingService = httpCollectionProcessingService;
    }

    protected Guid? UserId => HttpContext.GetUserId();

    protected Guid? ActiveDivisionId => HttpContext.Request.GetActiveDivisionId();

    [NonAction]
    protected virtual IQueryable<T> ApplyDivisionFilter<T>(IQueryable<T> query, Expression<Func<T, bool>> expression)
    {
        if (!User.HasClaim(NhPlatformClaimTypes.Permission,
                Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            query = query.Where(expression);
        }

        return query;
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IdentityResult identityResult)
    {
        var localizedErrors = new List<LocalizedString>();

        foreach (var error in identityResult.Errors)
        {
            localizedErrors.Add(_localizer[error.Description]);
        }

        return BadRequest(localizedErrors);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(LocalizedString error)
    {
        var response = new BadRequestHttpResponseModel(error);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IEnumerable<LocalizedString> errors)
    {
        var response = new BadRequestHttpResponseModel(errors);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(string error)
    {
        var response = new BadRequestHttpResponseModel(error);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IEnumerable<string> errors)
    {
        var response = new BadRequestHttpResponseModel(errors);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(TaskResult result)
    {
        result.ApplyToModelState(ModelState, _localizer);
        return BadRequest(ModelState);
    }
}
