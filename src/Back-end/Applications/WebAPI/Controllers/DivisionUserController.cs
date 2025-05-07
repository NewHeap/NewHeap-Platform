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
public class DivisionUserController : NhBaseDivisionUserController<
    NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, DivisionUserMutateModel,
    NhUserViewModel<NhDivisionViewModel>,
    NhDivisionViewModel, NhDivisionRoleViewModel,
    DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>>
{
    protected const string READ_POLICY = "app.division.view";
    protected const string MANAGE_POLICY = "app.division.manage";

    public DivisionUserController(
        IConfiguration config, 
        IMapper mapper, 
        ILogger<DivisionUserController> logger, 
        IStringLocalizer<DivisionUserController> localizer, 
        INhUserManager userService, 
        NhDivisionUserService divisionUserService, 
        IHttpCollectionProcessingService collectionRequestProcessingService
        ) : base(config, mapper, logger, localizer, userService, divisionUserService, collectionRequestProcessingService)
    {
    }
}