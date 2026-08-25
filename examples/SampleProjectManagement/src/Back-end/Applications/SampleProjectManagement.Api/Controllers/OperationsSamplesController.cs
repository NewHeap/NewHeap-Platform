using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.AspNet.Common.Models.View;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Route("operations-samples")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "app.project.manage")]
public sealed class OperationsSamplesController : ControllerBase
{
    private readonly OperationsSampleService _operationsService;

    public OperationsSamplesController(OperationsSampleService operationsService)
    {
        _operationsService = operationsService;
    }

    [HttpPost("jobs/enqueue-overdue")]
    [EndpointSummary("Enqueue the overdue-project job")]
    [EndpointDescription("Enqueues an immediate Hangfire job that processes overdue projects.")]
    [ProducesResponseType<JobSampleResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult EnqueueOverdueJob()
    {
        return Accepted(_operationsService.EnqueueOverdueJob());
    }

    [HttpPost("jobs/schedule-draft-cleanup")]
    [EndpointSummary("Schedule the draft-cleanup job")]
    [EndpointDescription("Schedules a delayed Hangfire job that cleans up stale draft projects.")]
    [ProducesResponseType<JobSampleResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ScheduleDraftCleanup()
    {
        return Accepted(_operationsService.ScheduleDraftCleanup());
    }

    [HttpPost("jobs/register-recurring")]
    [EndpointSummary("Register the recurring maintenance job")]
    [EndpointDescription("Registers or updates the recurring project-maintenance job and returns its stable Hangfire identifier.")]
    [ProducesResponseType<RecurringJobSampleResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult RegisterRecurringJob()
    {
        var jobId = _operationsService.RegisterRecurringJob();
        return Ok(new RecurringJobSampleResult(jobId));
    }

    [HttpPost("mail")]
    [EndpointSummary("Send a sample project email")]
    [EndpointDescription("Renders the localized project-assignment Razor template and submits the email through the configured mail service.")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SendMail(
        [FromBody] SendSampleMailMutateModel model,
        CancellationToken cancellationToken)
    {
        await _operationsService.SendMailAsync(model, cancellationToken);
        return Accepted();
    }

    [HttpPost("notifications")]
    [EndpointSummary("Create a sample user notification")]
    [EndpointDescription("Creates a persisted user notification with its configured delivery records.")]
    [ProducesResponseType<NotificationCreatedSampleResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateNotification(
        [FromBody] CreateSampleNotificationMutateModel model,
        CancellationToken cancellationToken)
    {
        var result = await _operationsService.CreateNotificationAsync(
            model,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Accepted(new NotificationCreatedSampleResult(
            result.Data.Id,
            result.Data.Deliveries.Count));
    }

    [HttpPost("background-operations/project-portfolio-analysis")]
    [EndpointSummary("Start the project portfolio background operation")]
    [EndpointDescription("Enqueues a durable, idempotent and division-exclusive operation that demonstrates weighted phases, nested steps, batch counters, checkpoints, retries, cancellation, notifications, polling and SignalR updates.")]
    [ProducesResponseType<NhBackgroundOperationViewModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> EnqueueProjectPortfolioAnalysis(
        [FromBody] ProjectPortfolioAnalysisMutateModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = HttpContext.GetUserId();
        var divisionId = HttpContext.GetActiveDivisionId();
        if (!userId.HasValue || !divisionId.HasValue)
        {
            ModelState.AddModelError(string.Empty, "An authenticated user and active division are required.");
            return BadRequest(ModelState);
        }

        if (!await HttpContext.HasDivisionAccessAsync(
                divisionId,
                cancellationToken: cancellationToken))
        {
            return Forbid();
        }

        var result = await _operationsService.EnqueuePortfolioAnalysisAsync(
            model,
            userId.Value,
            divisionId.Value,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Accepted(result.Data);
    }

    [HttpPost("background-operations/project-ai-portfolio-report")]
    [EndpointSummary("Start an approval-gated durable AI portfolio report")]
    [EndpointDescription("Captures an application-owned project snapshot, stores a versioned AI checkpoint reference, releases the worker while awaiting approval, and resumes without using chat history as authoritative state.")]
    [ProducesResponseType<NhBackgroundOperationViewModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> EnqueueProjectAiPortfolioReport(
        [FromBody] ProjectAiPortfolioReportMutateModel model,
        CancellationToken cancellationToken)
    {
        if (model.ApprovalExpiresAt <= DateTimeOffset.UtcNow
            || model.ApprovalExpiresAt > DateTimeOffset.UtcNow.AddDays(7))
        {
            ModelState.AddModelError(
                nameof(model.ApprovalExpiresAt),
                "Approval expiry must be in the future and no more than seven days away.");
        }
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = HttpContext.GetUserId();
        var divisionId = HttpContext.GetActiveDivisionId();
        if (!userId.HasValue || !divisionId.HasValue)
        {
            ModelState.AddModelError(string.Empty, "An authenticated user and active division are required.");
            return BadRequest(ModelState);
        }
        if (!await HttpContext.HasDivisionAccessAsync(
                divisionId,
                cancellationToken: cancellationToken))
        {
            return Forbid();
        }

        var result = await _operationsService.EnqueueAiPortfolioReportAsync(
            model,
            userId.Value,
            divisionId.Value,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }
        return Accepted(result.Data);
    }

    [HttpPost("background-operations/{operationId:guid}/project-ai-portfolio-report-approval")]
    [EndpointSummary("Signal approval for a durable AI portfolio report")]
    [EndpointDescription("Persists an exact proposal-bound approval signal and wakes the caller-owned operation. Duplicate identical signals are idempotent and conflicting signals are rejected.")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ApproveProjectAiPortfolioReport(
        Guid operationId,
        [FromBody] ProjectAiPortfolioReportApprovalMutateModel model,
        CancellationToken cancellationToken)
    {
        if (model.ApprovalId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(model.ApprovalId), "Approval ID is required.");
        }
        if (model.ProposalId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(model.ProposalId), "Proposal ID is required.");
        }
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }
        var userId = HttpContext.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var result = await _operationsService.ApproveAiPortfolioReportAsync(
            operationId,
            model,
            userId.Value,
            userId.Value,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }
        return Accepted(result.Data);
    }
}
