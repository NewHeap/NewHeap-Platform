using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;

namespace WebAPI.Controllers;

[Route("[controller]")]
public class DivisionController : NhBaseDivisionController<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionMutateModel, NhDivisionViewModel, NhDivisionRoleViewModel>
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public DivisionController(
        IConfiguration config, 
        IMapper mapper, 
        ILogger<DivisionController> logger, 
        IStringLocalizer<DivisionController> localizer, 
        NhDivisionService divisionService, 
        IHttpCollectionProcessingService collectionRequestProcessingService
        ) : base(config, mapper, logger, localizer, divisionService, collectionRequestProcessingService)
    {
    }
}