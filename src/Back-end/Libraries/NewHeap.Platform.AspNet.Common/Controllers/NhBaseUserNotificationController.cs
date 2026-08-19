using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using System.ComponentModel;
using System.Linq.Expressions;

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

        query = query
            .Where(x => x.UserId == UserId!.Value)
            .Where(x => !x.IsArchived)
        ;

        return Task.FromResult(query);
    }

    protected override IQueryable<NhUserNotification> AddBaseQueryableIncludesAsync(IQueryable<NhUserNotification> query, CancellationToken cancellationToken = default)
    {
        return query
            .Include(x => x.Messages.OrderByDescending(c => c.CreationDateTime))
          as IQueryable<NhUserNotification>
      ;
    }

    protected override (Expression<Func<NhUserNotification, object>> orderByKey, ListSortDirection sortDirection)[] GetDefaultCollectionResultOrderBy()
    {
        return [
            (x => x.IsLastRead, ListSortDirection.Ascending),
            (x => x.CreationDateTime, ListSortDirection.Descending)
        ];
    }

    [HttpGet("overview")]
    public virtual async Task<IActionResult> GetOverview(CancellationToken cancellationToken = default)
    {
        var overviewModel = await _dbEntityService.GetOverviewByUserIdAsync(UserId!.Value, cancellationToken: cancellationToken);

        return Ok(overviewModel);
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

        var notificationExists = await (await GetQueryableAsync(cancellationToken))
            .AnyAsync(x => x.Id == id, cancellationToken);

        if (!notificationExists)
        {
            return NotFound();
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

    [HttpPut("{id}/Archive")]
    public virtual async Task<IActionResult> Archive([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var notificationExists = await (await GetQueryableAsync(cancellationToken))
            .AnyAsync(x => x.Id == id, cancellationToken);

        if (!notificationExists)
        {
            return NotFound();
        }

        var taskResult = await _dbEntityService.ArchiveAsync(id, true, cancellationToken: cancellationToken);
        if (!taskResult.Success)
        {
            taskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

    [HttpPut("ArchiveAll")]
    public virtual async Task<IActionResult> ArchiveAll(CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var taskResult = await _dbEntityService.ArchiveAllByUserIdAsync(UserId!.Value, true, cancellationToken: cancellationToken);
        if (!taskResult.Success)
        {
            taskResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok();
    }

}