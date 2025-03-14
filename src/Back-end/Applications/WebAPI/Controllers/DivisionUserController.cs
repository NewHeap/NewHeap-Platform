using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Services;

namespace WebAPI.Controllers;

[Route("[controller]")]
public class DivisionController : NhBaseDivisionController
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public DivisionController(
        IConfiguration config, 
        IMapper mapper, 
        ILogger<DivisionController> logger, 
        IStringLocalizer<DivisionController> localizer, 
        DivisionService divisionService, 
        IHttpCollectionProcessingService collectionRequestProcessingService
        ) : base(config, mapper, logger, localizer, divisionService, collectionRequestProcessingService)
    {
    }
}