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

/// <summary>
/// Cross-owner administration of background operations. The endpoints are available
/// only after <see cref="NhBackgroundOperationBuilder.UseAdministrationPolicy"/> and
/// require that policy for every request. Operations stay scoped to the accessible
/// active division plus global operations.
/// </summary>
[ApiController]
[Route("background-operations/administration")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + ",Identity.Application")]
public sealed class NhBackgroundOperationAdministrationController : ProtectedNhBaseController
{
    /// <summary>
    /// Largest page the administration list returns.
    /// </summary>
    public const int MaxItemsPerPage = 100;

    private readonly INhBackgroundOperationAdministrationService _operations;
    private readonly IAuthorizationService _authorizationService;
    private readonly NhBackgroundOperationsOptions _options;

    public NhBackgroundOperationAdministrationController(
        IConfiguration configuration,
        IMapper mapper,
        ILogger<NhBackgroundOperationAdministrationController> logger,
        IStringLocalizer<NhBackgroundOperationAdministrationController> localizer,
        IHttpCollectionProcessingService collectionProcessing,
        INhBackgroundOperationAdministrationService operations,
        IAuthorizationService authorizationService,
        NhBackgroundOperationsOptions options)
        : base(mapper, logger, configuration, localizer, collectionProcessing)
    {
        _operations = operations;
        _authorizationService = authorizationService;
        _options = options;
    }

    [HttpGet]
    [EndpointSummary("List background operations of all users")]
    [EndpointDescription("Returns filtered and paged root operations of every owner in the accessible active division plus global operations, including the owner's display name. Requires the configured background-operation administration policy; the endpoint returns 404 when administration is not enabled.")]
    [ProducesResponseType<CollectionResultModel<NhBackgroundOperationAdministrationViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromQuery] NhBackgroundOperationAdministrationCollectionRequestModel request,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAdministrationAsync(cancellationToken);
        if (access.Denied is not null)
        {
            return access.Denied;
        }

        // The administration list spans every owner and can cover a very large table, so
        // pages stay small regardless of the application-wide collection maximum.
        if (request.ItemsPerPage > MaxItemsPerPage)
        {
            request.ItemsPerPage = MaxItemsPerPage;
        }

        var query = _operations.QueryForAdministration(access.DivisionId);
        var result = await GetCollectionResultModel<NhBackgroundOperation, NhBackgroundOperationAdministrationViewModel>(
            request,
            query,
            (page, _) => Task.FromResult(page.SelectSummary()),
            true,
            cancellationToken,
            (x => x.LastModifiedDateTime, ListSortDirection.Descending));
        await _operations.PopulateOwnersAsync(result.Items, cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a background operation as administrator")]
    [EndpointDescription("Returns the operation snapshot with its progress tree, child hierarchy, attempts, operator-only events and scheduling diagnostics. Requires the configured background-operation administration policy.")]
    [ProducesResponseType<NhBackgroundOperationAdministrationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] Guid id,
        [FromQuery] long? eventsAfterSequence = null,
        CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAdministrationAsync(cancellationToken);
        if (access.Denied is not null)
        {
            return access.Denied;
        }

        var operation = await _operations.GetAsync(id, access.DivisionId, eventsAfterSequence, cancellationToken);
        return operation is null ? NotFound() : Ok(operation);
    }

    [HttpPost("{id:guid}/cancel")]
    [EndpointSummary("Cancel a background operation as administrator")]
    [EndpointDescription("Durably requests cooperative cancellation for another user's operation and its child hierarchy. The administrator is recorded as requester and the owner is notified when the operation is cancelled.")]
    [ProducesResponseType<NhBackgroundOperationAdministrationViewModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<TaskResult<NhBackgroundOperationAdministrationViewModel>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAdministrationAsync(cancellationToken);
        if (access.Denied is not null)
        {
            return access.Denied;
        }

        if (await _operations.GetAsync(id, access.DivisionId, null, cancellationToken) is null)
        {
            return NotFound();
        }

        var result = await _operations.RequestCancellationAsync(
            id,
            access.UserId,
            access.DivisionId,
            cancellationToken);
        return result.Success ? Accepted(result.Data) : BadRequest(result);
    }

    [HttpPost("{id:guid}/retry")]
    [EndpointSummary("Retry a background operation as administrator")]
    [EndpointDescription("Queues a new fenced attempt for another user's unsuccessful terminal operation and its unsuccessful descendants when their registered idempotency policies permit retry.")]
    [ProducesResponseType<NhBackgroundOperationAdministrationViewModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<TaskResult<NhBackgroundOperationAdministrationViewModel>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retry([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeAdministrationAsync(cancellationToken);
        if (access.Denied is not null)
        {
            return access.Denied;
        }

        if (await _operations.GetAsync(id, access.DivisionId, null, cancellationToken) is null)
        {
            return NotFound();
        }

        var result = await _operations.RetryAsync(
            id,
            access.UserId,
            access.DivisionId,
            cancellationToken);
        return result.Success ? Accepted(result.Data) : BadRequest(result);
    }

    private async Task<AdministrationAccess> AuthorizeAdministrationAsync(CancellationToken cancellationToken)
    {
        if (_options.AdministrationPolicy is null)
        {
            return Deny(NotFound());
        }

        var userId = HttpContext.GetUserId();
        if (!userId.HasValue)
        {
            return Deny(Unauthorized());
        }

        var authorization = await _authorizationService.AuthorizeAsync(User, _options.AdministrationPolicy);
        if (!authorization.Succeeded)
        {
            return Deny(Forbid());
        }

        var activeDivisionId = HttpContext.GetActiveDivisionId();
        if (activeDivisionId.HasValue
            && !await HttpContext.HasDivisionAccessAsync(
                activeDivisionId,
                cancellationToken: cancellationToken))
        {
            return Deny(Forbid());
        }

        return new AdministrationAccess(null, userId.Value, activeDivisionId);
    }

    private static AdministrationAccess Deny(IActionResult result)
    {
        return new AdministrationAccess(result, Guid.Empty, null);
    }

    private readonly record struct AdministrationAccess(IActionResult? Denied, Guid UserId, Guid? DivisionId);
}
