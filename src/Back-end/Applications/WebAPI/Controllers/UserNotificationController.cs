using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;

namespace WebAPI.Controllers;

[Route("[controller]")]
public class UserNotificationController : NhBaseUserNotificationController
{
    public UserNotificationController(
        IConfiguration config, 
        IMapper mapper, 
        ILogger<UserNotificationController> logger, 
        IStringLocalizer<UserNotificationController> localizer, 
        INhUserNotificationService userNotificationService, 
        IHttpCollectionProcessingService collectionRequestProcessingService
        ) : base(config, mapper, logger, localizer, userNotificationService, collectionRequestProcessingService)
    {
    }
}