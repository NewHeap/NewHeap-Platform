using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Services;

namespace WebAPI.Controllers;

[Route("[controller]")]
public class UserController : NhBaseUserController
{
    protected const string READ_POLICY = "app.user.view";
    protected const string MANAGE_POLICY = "app.user.manage";

    public UserController(
        IConfiguration config, 
        IMapper mapper, 
        ILogger<UserController> logger, 
        IStringLocalizer<UserController> localizer, 
        NhUserManager userManager, 
        IHttpCollectionProcessingService collectionRequestProcessingService
        ) 
        : base(config, mapper, logger, localizer, userManager, collectionRequestProcessingService)
    {
    }
}