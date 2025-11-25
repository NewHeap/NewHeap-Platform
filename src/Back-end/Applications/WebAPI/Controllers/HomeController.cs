using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebAPI.Models.Mutate;
using WebAPI.Services;

namespace WebAPI.Controllers;

[Route("")]
public class HomeController : PublicNhBaseController
{
    public HomeController(
        IStringLocalizer<HomeController> localizer,
        ILogger<HomeController> logger,
        IConfiguration config,
        IHttpCollectionProcessingService collectionRequestProcessingService,
        IMapper mapper)
        : base(mapper, logger, config, localizer, collectionRequestProcessingService)
    {
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get()
    {
        var typedTaskResult = new TaskResult<object>();
        var nonTypedTaskResult = new TaskResult();

        typedTaskResult.ApplyToTaskResult(nonTypedTaskResult);
        nonTypedTaskResult.ApplyToTaskResult(typedTaskResult);


        return Ok("Hi");
    }

    [HttpGet("notification/email")]
    [AllowAnonymous]
    public async Task<IActionResult> TestEmailNotification([FromServices] INhNotificationService nhNotificationService)
    {
        var notification = NhNotificationBuilder.Create("Test notification")
            .WithPriority(NhNotificationPriority.Normal)
            .WithEmailDelivery(
                delivery: new NhEmailDeliveryData()
                {
                    // FromDisplayName = "NewHeap",
                    // FromEmail = "no-reply@newheap.com",
                    To = new List<string> { "daniel@newheap.com" },
                    Subject = "Test notification",
                    Body = "This is a test notification sent from the NewHeap platform.",
                    IsBodyHtml = true
                },
                priority: NhNotificationPriority.Normal // Overide the priority if needed
            )
            .Build();

        var result = await nhNotificationService.CreateAsync(notification);
        if (!result.Success)
        { 
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok($"Created: {result.Data!.Id}");
    }

    [HttpGet("notification/user")]
    [AllowAnonymous]
    public async Task<IActionResult> TestUserNotification([FromServices] INhNotificationService nhNotificationService)
    {
        var notification = NhNotificationBuilder.Create("Test notification")
            .WithPriority(NhNotificationPriority.Normal)
            .WithUserNotificationDelivery(
                delivery: new NhUserNotificationDeliveryData()
                {
                    Notification = new NhUserNotificationMutateModel()
                    {
                        Title = "Test notification",
                        Message = "This is a test notification sent from the NewHeap platform.",
                        UserId = Guid.Parse("07E35556-54F2-4975-A563-417EB5FBFA7D")//UserId!.Value // Ensure you have a valid UserId here, or set it to null if not applicable
                        ,Url = "https://newheap.com"
                        ,UrlInNewTab = true
                    }
                },
                priority: NhNotificationPriority.Normal // Overide the priority if needed
            )
            .Build();

        var result = await nhNotificationService.CreateAsync(notification);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok($"Created: {result.Data!.Id}");
    }

    [HttpPost("localization/test")]
    [AllowAnonymous]
    public async Task<IActionResult> GetLocalizationTest([FromServices] CompositeAddressService compositeAddressService, [FromBody] LocalizationTestModel requestModel, CancellationToken cancellationToken = default)
    {
        var test = compositeAddressService.TestLocalization();

        var test2 = _localizer["abc", "test", "test", "test", "test", "test"];

        var testing1 = _localizer["Failed '{0}': {1}", "matname", string.Join(", ", test.AllErrorMessages)];
        var testing = _localizer["Failed '{MaterialName}': {Messages}", "matname", string.Join(", ", test.AllErrorMessages)];

        return Ok(test.AllErrorMessages.Select(x => x.ToString()));
    }

    [HttpGet("composite/test")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCompositeTest([FromServices] CompositeAddressService compositeAddressService, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var result = await compositeAddressService.CreateAsync(new AddressMutateModel()
        {
            Street = "123 Main St",
            Country = "USA"
        }, cancellationToken: cancellationToken);

        if (!result.Success)
        { 
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }


}