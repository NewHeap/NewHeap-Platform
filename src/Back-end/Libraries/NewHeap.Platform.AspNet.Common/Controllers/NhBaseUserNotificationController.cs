using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract class NhBaseUserNotificationController : DbEntityProtectedNhBaseController<NhUserNotification, NhUserNotificationMutateModel, NhUserNotificationViewModel, INhUserNotificationService, NhUserNotificationCollectionRequestModel>
{
    public NhBaseUserNotificationController(
        IConfiguration config,
        IMapper mapper,
        ILogger<NhBaseUserNotificationController> logger,
        IStringLocalizer<NhBaseUserNotificationController> localizer,
        INhUserNotificationService userNotificationService,
        IHttpCollectionProcessingService collectionRequestProcessingService
    )
        : base(mapper, logger, config, localizer, collectionRequestProcessingService, userNotificationService)
    {
    }

    protected override Task<IQueryable<NhUserNotification>> GetQueryableAsync(CancellationToken cancellationToken = default)
    {
        var query = _dbEntityService
                .GetRepository()
                .GetAll()
            ;

        query = query.Where(x => x.UserId == UserId!.Value);

        return Task.FromResult(query);
    }

    [HttpGet]
    public virtual Task<IActionResult> Get([FromQuery] NhUserNotificationCollectionRequestModel requestModel, CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, cancellationToken: cancellationToken);
    }

    [HttpGet("{id}")]
    public virtual Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return DoGetById(id);
    }

    [HttpPut("{id}/MarkAsRead")]
    public virtual async Task<IActionResult> MarkAsRead([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if(!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var taskResult = await _dbEntityService.MarkIsLastReadAsync(id, true, cancellationToken: cancellationToken);
        if (!taskResult.Success)
        {
            taskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

    [HttpPut("MarkAllAsRead")]
    public virtual async Task<IActionResult> MarkAllAsRead(CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var taskResult = await _dbEntityService.MarkAllIsLastReadByUserIdAsync(UserId!.Value, true, cancellationToken: cancellationToken);
        if (!taskResult.Success)
        {
            taskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

}