using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Services;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json.Linq;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using System.ComponentModel;
using System.Linq.Expressions;

namespace SampleProjectManagement.Api.Controllers;

[Route("projects")]
public class ProjectController : DbEntityProtectedNhBaseController<
    Project,
    ProjectMutateModel,
    ProjectViewModel,
    ProjectService,
    ProjectCollectionRequestModel>
{
    private readonly ProjectService _projectService;
    private readonly ProjectCollectionSampleService _projectCollectionSampleService;

    public ProjectController(
        IMapper mapper,
        ILogger<ProjectController> logger,
        IConfiguration configuration,
        IStringLocalizer<ProjectController> localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        ProjectService projectService,
        ProjectCollectionSampleService projectCollectionSampleService)
        : base(
            mapper,
            logger,
            configuration,
            localizer,
            collectionProcessingService,
            projectService)
    {
        _projectService = projectService;
        _projectCollectionSampleService = projectCollectionSampleService;
    }

    protected override (Expression<Func<Project, object>> orderByKey, ListSortDirection sortDirection)[]
        GetDefaultCollectionResultOrderBy()
    {
        return [(x => x.Name, ListSortDirection.Ascending)];
    }

    protected override bool CanPartiallyUpdateProperty(string propertyName)
    {
        return propertyName is
            nameof(ProjectMutateModel.Status) or
            nameof(ProjectMutateModel.Deadline) or
            nameof(ProjectMutateModel.Description);
    }

    [HttpGet]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get projects")]
    [EndpointDescription("Returns a filtered, ordered and paged project collection using the NewHeap collection contract.")]
    [ProducesResponseType<CollectionResultModel<ProjectViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Get(
        [FromQuery] ProjectCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        return DoGet(
            requestModel,
            _projectService.GetCollectionQuery(requestModel),
            cancellationToken);
    }


    [HttpGet("mine")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get my projects")]
    [EndpointDescription("Returns the project collection restricted to projects owned by the authenticated user.")]
    [ProducesResponseType<CollectionResultModel<ProjectViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> GetMine(
        [FromQuery] ProjectCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        return DoGet(
            requestModel,
            _projectService.GetCollectionQuery(requestModel, UserId),
            cancellationToken);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get a project")]
    [EndpointDescription("Returns one project by identifier.")]
    [ProducesResponseType<ProjectViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return DoGetById(id, cancellationToken: cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Create a project")]
    [EndpointDescription("Validates and creates a project through the sample domain service.")]
    [ProducesResponseType<ProjectViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Create(
        [FromBody] ProjectMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        return DoCreate(mutateModel, cancellationToken: cancellationToken);
    }

    [HttpPost("transaction-rollback-sample")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Run the project rollback sample")]
    [EndpointDescription("Creates a project and event inside a transaction that is deliberately rolled back, then returns identifiers for verification.")]
    [ProducesResponseType<ProjectRollbackSampleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateRolledBackSample(
        [FromBody] ProjectMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        var result = await _projectService.CreateRolledBackSampleAsync(
            mutateModel,
            UserId,
            cancellationToken);
        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok(result.Data);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Update a project")]
    [EndpointDescription("Validates and updates the mutable fields of an existing project.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Update(
        Guid id,
        [FromBody] ProjectMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        return DoUpdate(id, mutateModel, cancellationToken: cancellationToken);
    }

    [HttpPatch("{id:guid}")]
    [Consumes("application/json")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Partially update a project")]
    [EndpointDescription("Applies a top-level partial JSON object through the NewHeap partial-update pipeline. Only status, deadline and description can be changed by this endpoint.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> UpdatePartial(
        [FromRoute] Guid id,
        [FromBody] JObject? partialUpdate,
        CancellationToken cancellationToken = default)
    {
        return DoUpdatePartial(id, partialUpdate, cancellationToken);
    }

    [HttpGet("public/statuses")]
    [AllowAnonymous]
    [EndpointSummary("Get public project statuses")]
    [EndpointDescription("Returns the numeric values and names used to build a project-status dropdown without authentication.")]
    [ProducesResponseType<IReadOnlyCollection<ProjectStatusOptionSample>>(StatusCodes.Status200OK)]
    public IActionResult GetPublicStatuses()
    {
        return Ok(Enum.GetValues<ProjectStatus>()
            .Select(status => new ProjectStatusOptionSample((int)status, status.ToString()))
            .ToArray());
    }

    [HttpGet("{id:guid}/composite")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get a project composite")]
    [EndpointDescription("Returns a project together with its tasks as a composed view model.")]
    [ProducesResponseType<ProjectCompositeViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComposite(Guid id, CancellationToken cancellationToken = default)
    {
        var viewModel = await _projectService.GetCompositeAsync(id, cancellationToken);
        if (viewModel is null)
        {
            return NotFound();
        }

        return Ok(viewModel);
    }

    [HttpGet("short")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get short project projections")]
    [EndpointDescription("Returns a lightweight projected project collection for selectors and lookups.")]
    [ProducesResponseType<SimpleCollectionResultModel<ProjectShortViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetShort(CancellationToken cancellationToken = default)
    {
        return Ok(await _projectCollectionSampleService.GetShortAsync());
    }

    [HttpGet("expression-resolver")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Resolve a collection expression")]
    [EndpointDescription("Demonstrates how a fluent collection selector is resolved to a nested filter path.")]
    [ProducesResponseType<CollectionExpressionSampleViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetExpressionResolverSample(
        [FromQuery] string taskTitle = "sample",
        CancellationToken cancellationToken = default)
    {
        return Ok(await _projectCollectionSampleService.ResolveOpenTaskTitleExpressionAsync(
            taskTitle,
            cancellationToken));
    }

    [HttpGet("projected")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get projected projects")]
    [EndpointDescription("Executes a server-side EF projection including a computed display name and open-task count.")]
    [ProducesResponseType<IReadOnlyCollection<ProjectProjectionViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProjected(CancellationToken cancellationToken = default)
    {
        return Ok(await _projectService.GetProjectedAsync(cancellationToken));
    }

    [HttpPut("{id:guid}/planning")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Update project planning")]
    [EndpointDescription("Applies the partial planning mutation to a project and returns the updated project.")]
    [ProducesResponseType<ProjectViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdatePlanning(
        Guid id,
        [FromBody] ProjectPlanningMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        var result = await _projectService.UpdatePlanningAsync(
            id,
            mutateModel,
            UserId,
            cancellationToken);

        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok(_mapper.Map<ProjectViewModel>(result.Data));
    }

    [HttpPost("bulk/mutations")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Apply bulk project mutations")]
    [EndpointDescription("Applies create, update and delete mutations in one service operation and reports the outcome counts.")]
    [ProducesResponseType<ProjectBulkMutationResultViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BulkMutations(
        [FromBody] ProjectBulkMutationSampleModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        var result = await _projectService.BulkMutationsAsync(
            mutateModel,
            UserId,
            cancellationToken);

        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok(result.Data);
    }

    [HttpPut("{id:guid}/status")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Update a project status")]
    [EndpointDescription("Changes the status of one project and returns the updated project contract.")]
    [ProducesResponseType<ProjectViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateStatus(
        Guid id,
        [FromBody] ProjectStatusMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updateResult = await _projectService.UpdateStatusAsync(
            id,
            mutateModel,
            UserId,
            cancellationToken);

        if (!updateResult.Success)
        {
            updateResult.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok(_mapper.Map<ProjectViewModel>(updateResult.Data));
    }

    [HttpPut("bulk/status")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Update multiple project statuses")]
    [EndpointDescription("Updates several project statuses and returns an itemized success and failure report.")]
    [ProducesResponseType<ProjectBulkStatusResultViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BulkUpdateStatus(
        [FromBody] ProjectBulkStatusMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var result = await _projectService.BulkUpdateStatusAsync(
            mutateModel,
            UserId,
            cancellationToken);

        if (!result.Success)
        {
            result.ApplyToModelState(ModelState);
            return BadRequest(ModelState);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Delete a project")]
    [EndpointDescription("Deletes one project after checking that it exists.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        return DoDelete(id, cancellationToken);
    }
}
