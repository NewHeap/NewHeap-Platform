using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.AspNet.Common.DAL;
using SampleProjectManagement.DAL;
using OneOf;
using System.Globalization;
using NewHeap.Platform.Common.Events;
using NewHeap.Platform.Common.Extensions;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Controllers;

[ApiController]
[Route("library-samples")]
public class LibrarySamplesController : ControllerBase
{
    private readonly INhEventPublisher _eventPublisher;
    private readonly SampleEventLog _eventLog;
    private readonly IStringLocalizer<LibrarySamplesController> _localizer;
    private readonly ObservabilitySampleService _observabilitySampleService;
    private readonly SampleStartupState _startupState;

    public LibrarySamplesController(
        INhEventPublisher eventPublisher,
        SampleEventLog eventLog,
        IStringLocalizer<LibrarySamplesController> localizer,
        ObservabilitySampleService observabilitySampleService,
        SampleStartupState startupState)
    {
        _eventPublisher = eventPublisher;
        _eventLog = eventLog;
        _localizer = localizer;
        _observabilitySampleService = observabilitySampleService;
        _startupState = startupState;
    }

    [HttpGet("startup-configuration")]
    [AllowAnonymous]
    [EndpointSummary("Get startup-configuration diagnostics")]
    [EndpointDescription("Returns when the sample IStartupConfiguration hook was executed.")]
    [ProducesResponseType<StartupConfigurationSample>(StatusCodes.Status200OK)]
    public IActionResult StartupConfiguration() =>
        Ok(new StartupConfigurationSample(_startupState.ConfiguredAtUtc));

    [HttpGet("http/text")]
    [AllowAnonymous]
    [EndpointSummary("Get a plain-text response")]
    [EndpointDescription("Returns a text/plain response for the NewHeap HTTP-client text helper sample.")]
    [Produces("text/plain")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    public IActionResult GetText() => Content("Sample Project Management text response", "text/plain");

    [HttpGet("http/binary")]
    [AllowAnonymous]
    [EndpointSummary("Get a binary response")]
    [EndpointDescription("Returns an application/octet-stream payload for the NewHeap HTTP-client binary helper sample.")]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetBinary()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Sample Project Management binary response");
        return File(bytes, "application/octet-stream");
    }

    [HttpGet("http/download")]
    [AllowAnonymous]
    [EndpointSummary("Download a CSV export")]
    [EndpointDescription("Returns a named CSV file for the NewHeap HTTP-client download helper sample.")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Download()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("project-key,project-name\nNHP,NewHeap samples");
        return File(bytes, "text/csv", "project-export.csv");
    }

    [HttpGet("http/query")]
    [AllowAnonymous]
    [EndpointSummary("Echo a query-string value")]
    [EndpointDescription("Echoes a bound query-string value and its length to demonstrate typed query parameters.")]
    [ProducesResponseType<QueryEchoSample>(StatusCodes.Status200OK)]
    public IActionResult EchoQuery([FromQuery] string value)
        => Ok(new QueryEchoSample(value, value.Length));

    [HttpGet("http/deduplicated")]
    [AllowAnonymous]
    [EndpointSummary("Run a deduplicated GET")]
    [EndpointDescription("Returns an execution identifier after a short delay so concurrent identical GET requests can prove request deduplication.")]
    [ProducesResponseType<DeduplicatedHttpSample>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeduplicatedGet(CancellationToken cancellationToken)
    {
        // Every actual controller execution receives its own ID. Two concurrent,
        // identical GET requests should therefore receive the same ID through the NewHeap interceptor.
        var executionId = Guid.NewGuid();
        await Task.Delay(250, cancellationToken);
        return Ok(new DeduplicatedHttpSample(executionId, DateTimeOffset.UtcNow));
    }

    [HttpGet("http/trace")]
    [AllowAnonymous]
    [EndpointSummary("Get HTTP trace identifiers")]
    [EndpointDescription("Returns the ASP.NET trace identifier and the propagated correlation response header.")]
    [ProducesResponseType<HttpTraceSample>(StatusCodes.Status200OK)]
    public IActionResult Trace()
        => Ok(new HttpTraceSample(
            HttpContext.TraceIdentifier,
            Response.Headers["X-Correlation-ID"].ToString()));

    [HttpGet("http/error")]
    [AllowAnonymous]
    [EndpointSummary("Trigger the central exception handler")]
    [EndpointDescription("Throws an intentional exception so the sample's centralized problem response and logging can be inspected.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public IActionResult ThrowSampleError()
        => throw new InvalidOperationException("Intentional sample exception for the central exception handler.");

    [HttpGet("validation/model-state")]
    [AllowAnonymous]
    [EndpointSummary("Get a nested ModelState validation response")]
    [EndpointDescription("Returns field-level and form-level validation errors in the standard NewHeap ModelState response shape.")]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    public IActionResult ModelStateValidation()
    {
        ModelState.AddModelError("project.name", "This nested name error comes from ModelState.");
        ModelState.AddModelError(string.Empty, "This general server error belongs to the form.");
        return BadRequest(ModelState);
    }


    [HttpGet("authorization/edit-or-admin")]
    [Authorize(Policy = "app.project.edit-or-admin")]
    [EndpointSummary("Test permission-or-role authorization")]
    [EndpointDescription("Succeeds when the current user has the project-manage permission or the administrator role.")]
    [ProducesResponseType<AuthorizationResultSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult EditOrAdmin() =>
        Ok(new AuthorizationResultSample("permission-or-role"));

    [HttpGet("authorization/active-division")]
    [Authorize(Policy = "app.active-division.project.view")]
    [EndpointSummary("Test active-division authorization")]
    [EndpointDescription("Succeeds when the current user has project-view access in the active division.")]
    [ProducesResponseType<AuthorizationResultSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ActiveDivision() =>
        Ok(new AuthorizationResultSample("active-division"));

    [HttpGet("authorization/match-one")]
    [Authorize]
    [EndpointSummary("Evaluate match-one authorization")]
    [EndpointDescription("Evaluates multiple authorization alternatives and returns which rule granted editor access.")]
    [ProducesResponseType<MatchOneAuthorizationSampleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult MatchOne(
        [FromServices] ProjectAuthorizationSampleService authorizationService)
    {
        var result = authorizationService.EvaluateEditorAccess(User);
        return result.Allowed ? Ok(result) : Forbid();
    }



    [HttpGet("database/project-count")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Count projects through EF Core")]
    [EndpointDescription("Executes a provider-neutral EF Core count query that works with both SQL Server and PostgreSQL.")]
    [ProducesResponseType<ProjectCountSample>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ProjectCount(
        [FromServices] SampleProjectManagementDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var count = await dbContext.Projects.CountAsync(cancellationToken);
        return Ok(new ProjectCountSample(count));
    }

    [HttpGet("database/project-chunks")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Read projects in chunks")]
    [EndpointDescription("Executes the NewHeap ChunkAsync extension over a stable server-side project query.")]
    [ProducesResponseType<ProjectChunksSampleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<SampleProblem>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ProjectChunks(
        [FromServices] SampleProjectManagementDbContext dbContext,
        [FromQuery] int chunkSize = 2,
        CancellationToken cancellationToken = default)
    {
        if (chunkSize is < 1 or > 100)
        {
            return BadRequest(new SampleProblem(
                "chunk-size-out-of-range",
                "Use a chunkSize between 1 and 100."));
        }

        // ChunkAsync pages with Skip/Take. A stable OrderBy is therefore essential:
        // without a fixed order, rows can move between chunks.
        var query = dbContext.Projects
            .AsNoTracking()
            .OrderBy(project => project.Id)
            .Select(project => new ProjectChunkRowSample(
                project.Id,
                project.Key,
                project.Name));

        var chunks = new List<ProjectChunkSample>();
        var chunkNumber = 1;

        await foreach (var rows in query.ChunkAsync(chunkSize, cancellationToken))
        {
            chunks.Add(new ProjectChunkSample(chunkNumber++, rows.Count, rows));
        }

        return Ok(new ProjectChunksSampleResponse(
            chunkSize,
            chunks.Sum(chunk => chunk.Count),
            chunks));
    }

    [HttpGet("localization")]
    [AllowAnonymous]
    [EndpointSummary("Get a localized sample value")]
    [EndpointDescription("Returns the current UI culture and a value resolved through IStringLocalizer.")]
    [ProducesResponseType<LocalizationSample>(StatusCodes.Status200OK)]
    public IActionResult Localization()
        => Ok(new LocalizationSample(
            CultureInfo.CurrentUICulture.Name,
            _localizer["Project created"].Value));

    [HttpGet("one-of/{id:guid}")]
    [AllowAnonymous]
    [EndpointSummary("Get a OneOf response")]
    [EndpointDescription("Returns either the success or problem contract so the OneOf OpenAPI schema transformer can expose both variants.")]
    [ProducesResponseType<OneOf<OneOfSuccessSample, OneOfProblemSample>>(StatusCodes.Status200OK)]
    public OneOf<OneOfSuccessSample, OneOfProblemSample> OneOfResult(Guid id, [FromQuery] bool fail = false)
        => fail
            ? new OneOfProblemSample("project-not-found", $"Project {id} was not found")
            : new OneOfSuccessSample(id, "Project contract is valid");

    [HttpPost("invariant-form")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [EndpointSummary("Bind invariant form values")]
    [EndpointDescription("Binds decimal and date form fields using the sample's invariant form-value provider.")]
    [ProducesResponseType<InvariantFormSample>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    public IActionResult InvariantForm([FromForm] decimal budget, [FromForm] DateTime deadline)
        => Ok(new InvariantFormSample(budget, deadline, CultureInfo.CurrentCulture.Name));

    [HttpGet("observability")]
    [AllowAnonymous]
    [EndpointSummary("Run the observability sample")]
    [EndpointDescription("Runs a traced operation with structured logging. Optionally records a handled failure without exposing sensitive exception details in the response.")]
    [ProducesResponseType<ObservabilityResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Observability(
        [FromQuery] bool includeHandledFailure = false,
        CancellationToken cancellationToken = default) =>
        Ok(await _observabilitySampleService.RunAsync(includeHandledFailure, cancellationToken));

    [HttpPost("events/project-created")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Publish a project-created event")]
    [EndpointDescription("Publishes the standard project-created event through the configured NewHeap event publisher and CAP outbox.")]
    [ProducesResponseType<PublishedEventSample>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PublishProjectCreated(
        [FromBody] PublishProjectCreatedSampleModel model,
        CancellationToken cancellationToken)
    {
        var @event = new ProjectCreatedEvent
        {
            ProjectId = model.ProjectId,
            ProjectKey = model.ProjectKey.Trim().ToUpperInvariant()
        };

        await _eventPublisher.PublishAsync(@event);
        return Accepted(new PublishedEventSample(@event.EventId, ProjectCreatedEvent.Topic));
    }


    [HttpPost("events/project-priority")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Publish a priority-project event")]
    [EndpointDescription("Publishes a custom-topic event through the configured NewHeap event publisher and CAP outbox.")]
    [ProducesResponseType<PublishedEventSample>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PublishProjectPriority(
        [FromBody] ProjectPrioritySampleEvent @event,
        CancellationToken cancellationToken)
    {
        await _eventPublisher.PublishAsync(@event);
        return Accepted(new PublishedEventSample(@event.EventId, ProjectPrioritySampleEvent.Topic));
    }

    [HttpGet("events")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get consumed project events")]
    [EndpointDescription("Returns the bounded in-memory log of project-created events consumed by this sample instance.")]
    [ProducesResponseType<IReadOnlyCollection<ProjectCreatedEvent>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetConsumedEvents()
    {
        return Ok(_eventLog.Events.OrderByDescending(item => item.OccurredAt));
    }
}

public sealed class PublishProjectCreatedSampleModel
{
    public Guid ProjectId { get; set; }

    public string ProjectKey { get; set; } = "";
}

public sealed record OneOfSuccessSample(Guid Id, string Message);

public sealed record OneOfProblemSample(string Code, string Message);

public sealed record ProjectChunkRowSample(Guid Id, string Key, string Name);

public sealed record ProjectChunkSample(
    int ChunkNumber,
    int Count,
    IReadOnlyList<ProjectChunkRowSample> Rows);

public sealed record ProjectChunksSampleResponse(
    int ChunkSize,
    int TotalCount,
    IReadOnlyList<ProjectChunkSample> Chunks);
