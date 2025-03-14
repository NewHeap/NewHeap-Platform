using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Services;

namespace WebAPI.Controllers;

[Route("[controller]")]
public class DivisionUserController : NhBaseDivisionUserController
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public DivisionUserController(
        IConfiguration config, 
        IMapper mapper, 
        ILogger<DivisionUserController> logger, 
        IStringLocalizer<DivisionUserController> localizer, 
        INhUserManager userService, 
        DivisionUserService divisionUserService, 
        IHttpCollectionProcessingService collectionRequestProcessingService
        ) : base(config, mapper, logger, localizer, userService, divisionUserService, collectionRequestProcessingService)
    {
    }
}