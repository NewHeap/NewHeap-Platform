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
        //var test = compositeAddressService.TestLocalization();

        var taskResult = new TaskResult();
        var ex = new Exception("System.Exception: commercetools.Base.Client.Error.BadRequestException: Client error response https://api.eu-central-1.aws.commercetools.com/opg-production/products 400 Bad Request {\"statusCode\":400,\"message\":\"The referenced object of type 'product-type' with key 'plastic-sheet' was not found. It either doesn't exist, or it can't be accessed from this endpoint (e.g., if the endpoint filters by store or customer account).\",\"errors\":[{\"code\":\"ReferencedResourceNotFound\",\"message\":\"The referenced object of type 'product-type' with key 'plastic-sheet' was not found. It either doesn't exist, or it can't be accessed from this endpoint (e.g., if the endpoint filters by store or customer account).\",\"typeId\":\"product-type\",\"key\":\"plastic-sheet\"}]}\r\n   at commercetools.Base.Client.ErrorHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\r\n   at commercetools.Base.Client.LoggerHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\r\n   at Microsoft.Extensions.ServiceDiscovery.Http.ResolvingHttpDelegatingHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) in /_/src/Microsoft.Extensions.ServiceDiscovery/Http/ResolvingHttpDelegatingHandler.cs:line 53\r\n   at Microsoft.Extensions.ServiceDiscovery.Http.ResolvingHttpDelegatingHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) in /_/src/Microsoft.Extensions.ServiceDiscovery/Http/ResolvingHttpDelegatingHandler.cs:line 53\r\n   at Microsoft.Extensions.ServiceDiscovery.Http.ResolvingHttpDelegatingHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) in /_/src/Microsoft.Extensions.ServiceDiscovery/Http/ResolvingHttpDelegatingHandler.cs:line 53\r\n   at Microsoft.Extensions.Http.Resilience.ResilienceHandler.<>c.<<SendAsync>b__3_0>d.MoveNext()\r\n--- End of stack trace from previous location ---\r\n   at Microsoft.Extensions.Http.Resilience.ResilienceHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\r\n   at Sentry.SentryMessageHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\r\n   at Microsoft.Extensions.Http.Logging.LoggingScopeHttpMessageHandler.<SendCoreAsync>g__Core|4_0(HttpRequestMessage request, Boolean useAsync, CancellationToken cancellationToken)\r\n   at System.Net.Http.HttpClient.<SendAsync>g__Core|83_0(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationTokenSource cts, Boolean disposeCts, CancellationTokenSource pendingRequestsCts, CancellationToken originalCancellationToken)\r\n   at commercetools.Base.Client.Middlewares.StreamHttpMiddleware.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\r\n   at commercetools.Base.Client.Middlewares.AuthorizationMiddleware.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\r\n   at commercetools.Base.Client.Middlewares.CorrelationIdMiddleware.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\r\n   at commercetools.Base.Client.StreamCtpClient.SendAsAsync(HttpRequestMessage requestMessage, CancellationToken cancellationToken)\r\n   at commercetools.Base.Client.StreamCtpClient.SendAsync[T](HttpRequestMessage requestMessage, CancellationToken cancellationToken)\r\n   at commercetools.Base.Client.StreamCtpClient.ExecuteAsync[T](HttpRequestMessage requestMessage, CancellationToken cancellationToken)\r\n   at commercetools.Sdk.Api.Client.RequestBuilders.Products.ByProjectKeyProductsPost.ExecuteAsync(CancellationToken cancellationToken)\r\n   at OPG.Platform.CommerceTools.CommerceToolsProductService.CreateAsync(IReadonlyExternalProduct mutateModel, ExternalProductServiceContext context, CancellationToken cancellationToken) in C:\\Users\\Lars\\source\\repos\\OPG\\platform\\src\\Back-end\\Libraries\\OPG.Platform.CommerceTools\\CommerceToolsProductService.cs:line 55\r\n   at OPG.Platform.Commerce.Core.Services.ProductService.CreateAsync(ProductMutateModel mutateModel, Nullable`1 committedByUserId, Action`1 beforeSave, CancellationToken cancellationToken, CompositeBaseDbEntityServiceOperationOptions options) in C:\\Users\\Lars\\source\\repos\\OPG\\platform\\src\\Back-end\\Libraries\\OPG.Platform.Commerce.Core\\Services\\ProductService.cs:line 205\r\n   at OPG.Interop.Systems.Services.OpgSystemsInteropService.SeedMaterials(IJobContext jobContext, CancellationToken cancellationToken) in C:\\Users\\Lars\\source\\repos\\OPG\\platform\\src\\Back-end\\Libraries\\OPG.Interop.Systems\\Services\\SeedProductsService.cs:line 536\r\n   at OPG.Platform.BackgroundJobs.Jobs.SeedProductsJob.ExecuteAsync(JobContext context, CancellationToken cancellationToken) in C:\\Users\\Lars\\source\\repos\\OPG\\platform\\src\\Back-end\\Libraries\\OPG.Platform.BackgroundJobs\\Jobs\\SeedProductsJob.cs:line 49");
        taskResult = taskResult.WithKeylessError(ex.ToString());

        var t = taskResult.AllErrorMessages.FirstOrDefault();
        var t2 = t?.ToString();

        var test = string.Join(", ", taskResult.AllErrorMessages);

        var testing1 = _localizer["Failed '{0}': {1}", "matname", string.Join(", ", taskResult.AllErrorMessages)];

        return Ok(taskResult.AllErrorMessages.Select(x => x.ToString()));
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