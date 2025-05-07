using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;

namespace WebAPI.Controllers;

[Route("[controller]")]
public class UserController : NhBaseUserController<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel>
{
    protected const string READ_POLICY = "app.user.view";
    protected const string MANAGE_POLICY = "app.user.manage";

    public UserController(
        IConfiguration config, 
        IMapper mapper, 
        ILogger<UserController> logger, 
        IStringLocalizer<UserController> localizer, 
        INhUserManager<NhUser> userManager, 
        IHttpCollectionProcessingService collectionRequestProcessingService
        ) 
        : base(config, mapper, logger, localizer, userManager, collectionRequestProcessingService)
    {
    }
}