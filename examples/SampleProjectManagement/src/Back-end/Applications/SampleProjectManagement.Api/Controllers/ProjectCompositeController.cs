using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.Core.Models.View;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using System.ComponentModel;
using System.Linq.Expressions;

namespace SampleProjectManagement.Api.Controllers;

[Route("project-composites")]
public sealed class ProjectCompositeController
    : CompositeDbEntityProtectedNhBaseController<
        Project,
        ProjectMutateModel,
        Project,
        ProjectCompositeViewModel,
        ProjectCompositeService,
        ProjectCollectionRequestModel>
{
    private readonly ProjectSetupService _projectSetupService;

    public ProjectCompositeController(
        IMapper mapper,
        ILogger<ProjectCompositeController> logger,
        IConfiguration configuration,
        IStringLocalizer<ProjectCompositeController> localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        ProjectCompositeService compositeService,
        ProjectSetupService projectSetupService)
        : base(
            mapper,
            logger,
            configuration,
            localizer,
            collectionProcessingService,
            compositeService)
    {
        _projectSetupService = projectSetupService;
    }

    protected override (Expression<Func<Project, object>> orderByKey, ListSortDirection sortDirection)[]
        GetDefaultCollectionResultOrderBy()
    {
        return [(project => project.Name, ListSortDirection.Ascending)];
    }

    protected override IQueryable<Project> AddBaseQueryableIncludesAsync(
        IQueryable<Project> query,
        CancellationToken cancellationToken = default)
    {
        return query.Include(project => project.Tasks);
    }

    [HttpGet]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get project composites")]
    [EndpointDescription("Returns a filtered, ordered and paged collection of projects composed with their tasks.")]
    [ProducesResponseType<CollectionResultModel<ProjectCompositeViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Get(
        [FromQuery] ProjectCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, cancellationToken: cancellationToken);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get a project composite")]
    [EndpointDescription("Returns one project and its task collection by project identifier.")]
    [ProducesResponseType<ProjectCompositeViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return DoGetById(id, cancellationToken: cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Create a project composite")]
    [EndpointDescription("Creates a project through the composite base service and returns its composed representation.")]
    [ProducesResponseType<ProjectCompositeViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Create(
        [FromBody] ProjectMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        return DoCreate(mutateModel, cancellationToken);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Update a project composite")]
    [EndpointDescription("Updates the project through the composite base service.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Update(
        Guid id,
        [FromBody] ProjectMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        return DoUpdate(id, mutateModel, cancellationToken);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Delete a project composite")]
    [EndpointDescription("Deletes one project after checking that its composite root exists.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        return DoDelete(id, cancellationToken);
    }

    [HttpPost("with-initial-task")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Create a project with its initial task")]
    [EndpointDescription("Creates a project and its first task atomically in one explicit transaction.")]
    [ProducesResponseType<ProjectCompositeViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateWithInitialTask(
        [FromBody] ProjectWithInitialTaskMutateModel mutateModel,
        CancellationToken cancellationToken = default)
    {
        var result = await _projectSetupService.CreateWithInitialTaskAsync(
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
}
