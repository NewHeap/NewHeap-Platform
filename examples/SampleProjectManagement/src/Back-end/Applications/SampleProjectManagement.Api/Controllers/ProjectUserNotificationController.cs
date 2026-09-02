using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Controllers;

[Route("project-user-notifications")]
[Authorize]
public sealed class ProjectUserNotificationController : NhBaseUserNotificationController
{
    public ProjectUserNotificationController(
        IConfiguration config,
        IMapper mapper,
        ILogger<ProjectUserNotificationController> logger,
        IStringLocalizer<ProjectUserNotificationController> localizer,
        INhUserNotificationService userNotificationService,
        IHttpCollectionProcessingService collectionRequestProcessingService)
        : base(config, mapper, logger, localizer, userNotificationService, collectionRequestProcessingService)
    {
    }

    [EndpointSummary("Get the user-notification overview")]
    [EndpointDescription("Returns unread and current notification totals for the authenticated user.")]
    [ProducesResponseType<NhOverviewUserNotificationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public override Task<IActionResult> GetOverview(CancellationToken cancellationToken = default) =>
        base.GetOverview(cancellationToken);

    [EndpointSummary("Get user notifications")]
    [EndpointDescription("Returns a filtered, ordered and paged notification collection for the authenticated user.")]
    [ProducesResponseType<CollectionResultModel<NhUserNotificationViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public override Task<IActionResult> Get(
        [FromQuery] NhUserNotificationCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default) =>
        base.Get(requestModel, cancellationToken);

    [EndpointSummary("Get a user notification")]
    [EndpointDescription("Returns one non-archived notification belonging to the authenticated user.")]
    [ProducesResponseType<NhUserNotificationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public override Task<IActionResult> GetById(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default) =>
        base.GetById(id, cancellationToken);

    [EndpointSummary("Mark a user notification as read")]
    [EndpointDescription("Marks one notification belonging to the authenticated user as read.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public override Task<IActionResult> MarkAsRead(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default) =>
        base.MarkAsRead(id, cancellationToken);

    [EndpointSummary("Mark all user notifications as read")]
    [EndpointDescription("Marks every current notification belonging to the authenticated user as read.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public override Task<IActionResult> MarkAllAsRead(CancellationToken cancellationToken = default) =>
        base.MarkAllAsRead(cancellationToken);

    [EndpointSummary("Archive a user notification")]
    [EndpointDescription("Archives one notification belonging to the authenticated user.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public override Task<IActionResult> Archive(
        [FromRoute] Guid id,
        CancellationToken cancellationToken = default) =>
        base.Archive(id, cancellationToken);

    [EndpointSummary("Archive all user notifications")]
    [EndpointDescription("Archives every current notification belonging to the authenticated user.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public override Task<IActionResult> ArchiveAll(CancellationToken cancellationToken = default) =>
        base.ArchiveAll(cancellationToken);
}
