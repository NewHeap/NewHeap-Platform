using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Mapping;
using System.ComponentModel;

namespace NewHeap.Platform.AspNet.Common.Controllers;

[ApiController]
[Route("background-operations")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + ",Identity.Application")]
public sealed class NhBackgroundOperationController : ProtectedNhBaseController
{
    private readonly INhBackgroundOperationService _operations;

    public NhBackgroundOperationController(
        IConfiguration configuration,
        IMapper mapper,
        ILogger<NhBackgroundOperationController> logger,
        IStringLocalizer<NhBackgroundOperationController> localizer,
        IHttpCollectionProcessingService collectionProcessing,
        INhBackgroundOperationService operations)
        : base(mapper, logger, configuration, localizer, collectionProcessing)
    {
        _operations = operations;
    }

    [HttpGet]
    [EndpointSummary("List the current user's background operations")]
    [EndpointDescription("Returns filtered and paged operation summaries for the authenticated user and active division. The response is the canonical polling fallback for live updates.")]
    [ProducesResponseType<CollectionResultModel<NhBackgroundOperationViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(
        [FromQuery] NhBackgroundOperationCollectionRequestModel request,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var activeDivisionId = HttpContext.GetActiveDivisionId();
        if (activeDivisionId.HasValue
            && !await HttpContext.HasDivisionAccessAsync(
                activeDivisionId,
                cancellationToken: cancellationToken))
        {
            return Forbid();
        }

        var query = _operations.QueryForOwner(userId.Value, activeDivisionId);
        var result = await GetCollectionResultModel<NhBackgroundOperation, NhBackgroundOperationViewModel>(
            request,
            query,
            null,
            true,
            cancellationToken,
            (x => x.LastModifiedDateTime, ListSortDirection.Descending));

        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a background operation")]
    [EndpointDescription("Returns the durable operation snapshot, nested progress tree, fan-out child hierarchy, attempts, and user-visible events after an optional event sequence.")]
    [ProducesResponseType<NhBackgroundOperationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] Guid id,
        [FromQuery] long? eventsAfterSequence = null,
        CancellationToken cancellationToken = default)
    {
        var operation = await GetVisibleOperationAsync(id, eventsAfterSequence, cancellationToken);
        return operation is null ? NotFound() : Ok(operation);
    }

    [HttpPost("{id:guid}/cancel")]
    [EndpointSummary("Request background operation cancellation")]
    [EndpointDescription("Durably requests cooperative cancellation for the operation and its child hierarchy. Running handlers observe the request through their context and heartbeat cancellation token.")]
    [ProducesResponseType<NhBackgroundOperationViewModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<TaskResult<NhBackgroundOperationViewModel>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        var visible = await GetVisibleOperationAsync(id, null, cancellationToken);
        if (visible is null)
        {
            return NotFound();
        }

        var result = await _operations.RequestCancellationAsync(id, visible.OwnerUserId, cancellationToken);
        return result.Success ? Accepted(result.Data) : BadRequest(result);
    }

    [HttpPost("{id:guid}/retry")]
    [EndpointSummary("Retry an unsuccessful background operation")]
    [EndpointDescription("Queues a new fenced attempt for an unsuccessful terminal operation and its unsuccessful descendants when their registered idempotency policies permit retry.")]
    [ProducesResponseType<NhBackgroundOperationViewModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<TaskResult<NhBackgroundOperationViewModel>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retry([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        var visible = await GetVisibleOperationAsync(id, null, cancellationToken);
        if (visible is null)
        {
            return NotFound();
        }

        var result = await _operations.RetryAsync(id, visible.OwnerUserId, cancellationToken);
        return result.Success ? Accepted(result.Data) : BadRequest(result);
    }

    private async Task<NhBackgroundOperationViewModel?> GetVisibleOperationAsync(
        Guid operationId,
        long? eventsAfterSequence,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        if (!userId.HasValue)
        {
            return null;
        }

        var activeDivisionId = HttpContext.GetActiveDivisionId();
        if (activeDivisionId.HasValue
            && !await HttpContext.HasDivisionAccessAsync(
                activeDivisionId,
                cancellationToken: cancellationToken))
        {
            return null;
        }

        var operation = await _operations.GetAsync(operationId, userId.Value, eventsAfterSequence, cancellationToken);
        if (operation?.DivisionId is not null && operation.DivisionId != activeDivisionId)
        {
            return null;
        }

        return operation;
    }
}