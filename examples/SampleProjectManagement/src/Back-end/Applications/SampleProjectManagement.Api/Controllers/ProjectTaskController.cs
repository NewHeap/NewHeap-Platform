using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Services;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using System.ComponentModel;
using System.Linq.Expressions;

namespace SampleProjectManagement.Api.Controllers;

[Route("project-tasks")]
public class ProjectTaskController : DbEntityProtectedNhBaseController<
    ProjectTask,
    ProjectTaskMutateModel,
    ProjectTaskViewModel,
    ProjectTaskService,
    ProjectTaskCollectionRequestModel>
{
    private readonly ProjectTaskService _projectTaskService;

    public ProjectTaskController(
        IMapper mapper,
        ILogger<ProjectTaskController> logger,
        IConfiguration configuration,
        IStringLocalizer<ProjectTaskController> localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        ProjectTaskService projectTaskService)
        : base(mapper, logger, configuration, localizer, collectionProcessingService, projectTaskService)
    {
        _projectTaskService = projectTaskService;
    }

    protected override (Expression<Func<ProjectTask, object>> orderByKey, ListSortDirection sortDirection)[]
        GetDefaultCollectionResultOrderBy()
    {
        return [(x => x.Title, ListSortDirection.Ascending)];
    }

    [HttpGet]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get project tasks")]
    [EndpointDescription("Returns a filtered, ordered and paged project-task collection.")]
    [ProducesResponseType<CollectionResultModel<ProjectTaskViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Get(
        [FromQuery] ProjectTaskCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, _projectTaskService.GetCollectionQuery(requestModel), cancellationToken);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get a project task")]
    [EndpointDescription("Returns one project task by identifier.")]
    [ProducesResponseType<ProjectTaskViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
        => DoGetById(id, cancellationToken: cancellationToken);

    [HttpPost]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Create a project task")]
    [EndpointDescription("Validates and creates a task for a project.")]
    [ProducesResponseType<ProjectTaskViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Create(
        [FromBody] ProjectTaskMutateModel mutateModel,
        CancellationToken cancellationToken = default)
        => DoCreate(mutateModel, cancellationToken: cancellationToken);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Update a project task")]
    [EndpointDescription("Validates and updates the mutable task fields.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Update(
        Guid id,
        [FromBody] ProjectTaskMutateModel mutateModel,
        CancellationToken cancellationToken = default)
        => DoUpdate(id, mutateModel, cancellationToken: cancellationToken);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Delete a project task")]
    [EndpointDescription("Deletes one task after checking that it exists.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
        => DoDelete(id, cancellationToken);
}
