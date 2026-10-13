using NewHeap.Platform.Mapping;
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

/// <summary>
/// The people on a project. Their role decides what they may do with the project's agent threads:
/// readers see them, participants also type and cancel runs, approvers also decide proposals.
/// </summary>
[Route("project-members")]
public class ProjectMemberController : DbEntityProtectedNhBaseController<
    ProjectMember,
    ProjectMemberMutateModel,
    ProjectMemberViewModel,
    ProjectMemberService,
    ProjectMemberCollectionRequestModel>
{
    private readonly ProjectMemberService _projectMemberService;

    public ProjectMemberController(
        IMapper mapper,
        ILogger<ProjectMemberController> logger,
        IConfiguration configuration,
        IStringLocalizer<ProjectMemberController> localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        ProjectMemberService projectMemberService)
        : base(mapper, logger, configuration, localizer, collectionProcessingService, projectMemberService)
    {
        _projectMemberService = projectMemberService;
    }

    protected override (Expression<Func<ProjectMember, object>> orderByKey, ListSortDirection sortDirection)[]
        GetDefaultCollectionResultOrderBy()
    {
        return [(x => x.Role, ListSortDirection.Descending), (x => x.CreationDateTime, ListSortDirection.Ascending)];
    }

    [HttpGet]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get project members")]
    [EndpointDescription("Returns a filtered, ordered and paged collection of project members with their role.")]
    [ProducesResponseType<CollectionResultModel<ProjectMemberViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Get(
        [FromQuery] ProjectMemberCollectionRequestModel requestModel,
        CancellationToken cancellationToken = default)
    {
        return DoGet(requestModel, _projectMemberService.GetCollectionQuery(requestModel), cancellationToken);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "app.project.view")]
    [EndpointSummary("Get a project member")]
    [EndpointDescription("Returns one project member by identifier.")]
    [ProducesResponseType<ProjectMemberViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
        => DoGetById(id, _projectMemberService.GetQueryWithUser(), cancellationToken);

    [HttpPost]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Add a project member")]
    [EndpointDescription("Adds a person of the project's division to the project with a role and refreshes the project's agent threads.")]
    [ProducesResponseType<ProjectMemberViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Create(
        [FromBody] ProjectMemberMutateModel mutateModel,
        CancellationToken cancellationToken = default)
        => DoCreate(mutateModel, cancellationToken: cancellationToken);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Change a project member")]
    [EndpointDescription("Changes the role of a project member; the new role applies to the project's agent threads at once.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Update(
        Guid id,
        [FromBody] ProjectMemberMutateModel mutateModel,
        CancellationToken cancellationToken = default)
        => DoUpdate(id, mutateModel, cancellationToken: cancellationToken);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "app.project.manage")]
    [EndpointSummary("Remove a project member")]
    [EndpointDescription("Removes a person from the project; they lose access to the project's agent threads at once.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
        => DoDelete(id, cancellationToken);
}
