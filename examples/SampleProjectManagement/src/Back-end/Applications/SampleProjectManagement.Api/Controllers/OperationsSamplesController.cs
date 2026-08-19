using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Route("operations-samples")]
[Authorize(Policy = "app.project.manage")]
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
}
